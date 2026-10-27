using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Audio;

namespace AudioBridge
{
    internal interface IPlaybackLink : IDisposable
    {
        bool IsOpen { get; }
        event EventHandler StateChanged;
        Task Start(CancellationToken token);
        Task<string> Open(CancellationToken token);
    }

    internal sealed class WindowsPlaybackLink : IPlaybackLink
    {
        readonly AudioPlaybackConnection native;
        public event EventHandler StateChanged;
        public bool IsOpen { get { return native.State == AudioPlaybackConnectionState.Opened; } }

        internal WindowsPlaybackLink(string id)
        {
            native = AudioPlaybackConnection.TryCreateFromId(id);
            if (native == null) throw new InvalidOperationException("Источник недоступен. Проверь Bluetooth и сопряжение телефона.");
            native.StateChanged += Changed;
        }

        void Changed(AudioPlaybackConnection sender, object args)
        {
            var handler = StateChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public Task Start(CancellationToken token) { return NativeAsync.Done(native.StartAsync(), token); }

        public async Task<string> Open(CancellationToken token)
        {
            var result = await NativeAsync.Result(native.OpenAsync(), token);
            if (result.Status == AudioPlaybackConnectionOpenResultStatus.Success) return null;
            string code = result.ExtendedError == null ? "" : "; HRESULT=0x" + result.ExtendedError.HResult.ToString("X8");
            return result.Status + code;
        }

        public void Dispose()
        {
            try { native.StateChanged -= Changed; }
            finally { native.Dispose(); }
        }
    }

    // Called on the UI thread; native notifications are posted to that thread.
    // An unsuccessful Open does not mean Start failed. Keep the enabled sink
    // alive so Android can connect during the interval between our attempts.
    internal sealed class PhoneReceiver : IDisposable
    {
        readonly Func<string, IPlaybackLink> factory;
        readonly Action<Action> dispatch;
        readonly Action<string> log;
        readonly Func<DateTime> now;
        IPlaybackLink current;
        string deviceId;
        bool started;
        bool observedOpen;
        bool opening;
        bool renewBeforeOpen;
        int failures;
        int failedOpens;

        internal event Action<bool> StateChanged;
        internal IPlaybackLink Current { get { return current; } }
        internal bool IsOpen { get { return observedOpen; } }
        internal string Error { get; private set; }
        internal DateTime NextAttempt { get; private set; }
        internal bool RetryDue { get { return !opening && !IsOpen && now() >= NextAttempt; } }

        internal PhoneReceiver(Func<string, IPlaybackLink> factory, Action<Action> dispatch, Action<string> log, Func<DateTime> now)
        {
            this.factory = factory; this.dispatch = dispatch; this.log = log; this.now = now;
        }

        internal async Task<bool> Open(string id, CancellationToken token)
        {
            if (opening) return false;
            opening = true;
            IPlaybackLink attempt = null;
            var watch = Stopwatch.StartNew();
            try
            {
                token.ThrowIfCancellationRequested();
                if (current != null && (deviceId != id || renewBeforeOpen)) Release();
                if (current == null)
                {
                    current = factory(id);
                    if (current == null) throw new InvalidOperationException("Источник Bluetooth пока недоступен.");
                    deviceId = id;
                    current.StateChanged += Changed;
                }
                attempt = current;
                if (!started)
                {
                    log("Bluetooth: включаю приёмник (StartAsync).");
                    await attempt.Start(token);
                    token.ThrowIfCancellationRequested();
                    if (current != attempt) return false;
                    started = true;
                }
                Poll();
                if (IsOpen) return true;
                log("Bluetooth: попытка OpenAsync.");
                string failure = await attempt.Open(token);
                token.ThrowIfCancellationRequested();
                if (current != attempt) return false;
                Poll();
                if (IsOpen)
                {
                    log("Bluetooth: соединение открыто за " + watch.Elapsed.TotalSeconds.ToString("F1") + " с.");
                    return true;
                }
                Error = failure ?? "Соединение закрылось во время подключения.";
                log("Bluetooth: " + Error + "; длительность " + watch.Elapsed.TotalSeconds.ToString("F1") + " с.");
                // A stale sink after an adapter/service restart may still accept
                // API calls. Occasionally renew only our registration, not pairing.
                if (++failedOpens >= 3)
                {
                    renewBeforeOpen = true;
                    log("Bluetooth: обновлю регистрацию приёмника перед следующей попыткой после трёх неудач.");
                }
                ScheduleRetry();
                return false;
            }
            catch (OperationCanceledException)
            {
                if (current == attempt) Release();
                throw;
            }
            catch (Exception ex)
            {
                // Stop may have disposed a link while its async call completed.
                if (attempt != null && current != attempt) return false;
                Error = ex.Message;
                log("Bluetooth: " + ex.GetType().Name + "; HRESULT=0x" + ex.HResult.ToString("X8") + "; " + ex.Message);
                Release();
                ScheduleRetry();
                return false;
            }
            finally { opening = false; }
        }

        void Changed(object sender, EventArgs args)
        {
            dispatch(delegate { if (Object.ReferenceEquals(current, sender)) Poll(); });
        }

        internal void Poll()
        {
            if (current == null) return;
            bool isOpen;
            try { isOpen = current.IsOpen; }
            catch (Exception ex)
            {
                bool wasOpen = observedOpen;
                Error = ex.Message;
                log("Bluetooth: состояние недоступно; HRESULT=0x" + ex.HResult.ToString("X8") + "; " + ex.Message);
                Release();
                ScheduleRetry();
                var failed = StateChanged;
                if (wasOpen && failed != null) failed(false);
                return;
            }
            if (isOpen == observedOpen) return;
            observedOpen = isOpen;
            log("Bluetooth: состояние " + (isOpen ? "Opened" : "Closed") + ".");
            if (isOpen) { failures = failedOpens = 0; renewBeforeOpen = false; Error = null; NextAttempt = DateTime.MaxValue; }
            else ScheduleRetry();
            var handler = StateChanged;
            if (handler != null) handler(isOpen);
        }

        void ScheduleRetry()
        {
            failures = Math.Min(failures + 1, 4);
            int seconds = failures == 4 ? 15 : 1 << failures;
            NextAttempt = now().AddSeconds(seconds);
            log("Bluetooth: следующая попытка через " + seconds + " с; приёмник " + (started ? "остаётся включён" : "будет создан заново") + ".");
        }

        void Release()
        {
            var old = current;
            current = null;
            started = observedOpen = false;
            renewBeforeOpen = false;
            failedOpens = 0;
            if (old == null) return;
            old.StateChanged -= Changed;
            try { old.Dispose(); } catch { }
        }

        public void Dispose()
        {
            Release();
            failures = 0;
            Error = null;
            NextAttempt = now();
        }
    }

    internal static class DiagnosticLog
    {
        internal static readonly string Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioBridge", "connection.log");
        static readonly object gate = new object();

        internal static void Write(string message)
        {
            // Logging must never interrupt playback, including on a read-only disk.
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                    if (File.Exists(Path) && new FileInfo(Path).Length > 512 * 1024)
                    {
                        File.Copy(Path, Path + ".previous", true);
                        File.WriteAllText(Path, "", Encoding.UTF8);
                    }
                    File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message.Replace("\r\n", " ") + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
