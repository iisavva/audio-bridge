using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Audio;
using Windows.Media.Devices;

[assembly: AssemblyTitle("Audio Bridge")]
[assembly: AssemblyProduct("Audio Bridge")]
[assembly: AssemblyDescription("Звук Android и ПК через выбранный аудиовыход Windows")]

namespace AudioBridge
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            using (var singleInstance = new Mutex(false, "Local\\AudioBridge"))
            {
                if (!singleInstance.WaitOne(0))
                {
                    MessageBox.Show("Приёмник уже запущен. Найди его окно или значок в области уведомлений.", "Audio Bridge");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { Application.Run(new BridgeForm()); }
                finally { singleInstance.ReleaseMutex(); }
            }
        }
    }

    // Polling keeps this portable build independent of the Windows SDK and
    // preserves cancellation/timeout handling for native WinRT operations.
    static class NativeAsync
    {
        public static async Task<T> Result<T>(IAsyncOperation<T> operation, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                while (operation.Status == AsyncStatus.Started)
                {
                    token.ThrowIfCancellationRequested();
                    if (watch.Elapsed.TotalSeconds > 25) throw new TimeoutException("Телефон не ответил за 25 секунд.");
                    await Task.Delay(100, token);
                }
                token.ThrowIfCancellationRequested();
                return operation.GetResults();
            }
            finally
            {
                Cleanup(operation);
            }
        }

        public static async Task Done(IAsyncAction operation, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                while (operation.Status == AsyncStatus.Started)
                {
                    token.ThrowIfCancellationRequested();
                    if (watch.Elapsed.TotalSeconds > 25) throw new TimeoutException("Windows не ответила за 25 секунд.");
                    await Task.Delay(100, token);
                }
                token.ThrowIfCancellationRequested();
                operation.GetResults();
            }
            finally
            {
                Cleanup(operation);
            }
        }

        static void Cleanup(IAsyncInfo operation)
        {
            // Cancellation can complete asynchronously. Never close an operation
            // that is still Started, or mask its original exception in cleanup.
            try
            {
                if (operation.Status == AsyncStatus.Started) operation.Cancel();
                if (operation.Status != AsyncStatus.Started) operation.Close();
            }
            catch { }
        }
    }

    class PhoneItem
    {
        public DeviceInformation Device;
        public override string ToString() { return Device.Name; }
    }

    sealed class BridgeForm : Form
    {
        readonly ComboBox phones = new ComboBox();
        readonly Button connect = new Button();
        readonly Button stop = new Button();
        readonly Button refresh = new Button();
        readonly Label output = new Label();
        readonly Label status = new Label();
        readonly CheckBox reconnect = new CheckBox();
        readonly TextBox log = new TextBox();
        readonly TrackBar phoneVolume = new TrackBar();
        readonly Label phonePercent = new Label();
        readonly Label volumeHint = new Label();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer volumeTimer = new System.Windows.Forms.Timer();
        CancellationTokenSource cancellation = new CancellationTokenSource();
        readonly PhoneReceiver receiver;
        IPlaybackLink connection { get { return receiver.Current; } }
        string wantedId;
        bool busy;
        bool closing;
        bool exitRequested;
        bool wantConnection;
        int audioGeneration;
        bool checkingTick;
        bool suspended;
        DateTime nextVolumeCheck;
        string connectedOutputId;
        string lastAudioSummary;
        int? restorePhonePercent;
        string phoneSessionId;
        bool updatingVolumes;
        bool adjustingVolume;
        PhoneAmplifier amplifier;
        MixerSession volumeControl;
        bool volumePending;
        int requestedPhonePercent;
        DateTime lastVolumeEdit;

        public BridgeForm()
        {
            receiver = new PhoneReceiver(id => new WindowsPlaybackLink(id), Post, AddLog, () => DateTime.UtcNow);
            receiver.StateChanged += ReceiverStateChanged;
            DiagnosticLog.Write("Audio Bridge: запуск приёмника.");
            Text = "Audio Bridge — звук ПК и Android";
            Font = new Font("Segoe UI", 10F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(720, 740);
            MinimumSize = new Size(700, 740);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(248, 249, 251);
            Icon = SystemIcons.Application;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 10 };
            for (int i = 0; i < 9; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            layout.Controls.Add(Label("Звук ПК и телефона вместе", 20, FontStyle.Bold, 46));
            layout.Controls.Add(Label("Android → Bluetooth → ПК → наушники или колонки", 11, FontStyle.Regular, 32));

            output.AutoSize = false;
            output.Dock = DockStyle.Top;
            output.Height = 54;
            output.Text = "Выход Windows: проверяю…";
            layout.Controls.Add(output);

            var settings = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 12) };
            settings.Controls.Add(Button("Bluetooth Windows", delegate { Open("ms-settings:bluetooth"); }));
            settings.Controls.Add(Button("Выход звука", delegate { Open("ms-settings:sound"); }));
            settings.Controls.Add(Button("Звонки: Связь с телефоном", delegate { Open("ms-phone:"); }));
            layout.Controls.Add(settings);

            var source = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = new Padding(0, 0, 0, 12) };
            source.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            source.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            phones.DropDownStyle = ComboBoxStyle.DropDownList;
            phones.Dock = DockStyle.Fill;
            phones.Margin = new Padding(0, 4, 10, 0);
            phones.AccessibleName = "Телефон — источник звука";
            phones.SelectedIndexChanged += delegate { UpdateButtons(); };
            source.Controls.Add(phones, 0, 0);
            refresh.Text = "Обновить";
            refresh.Height = 34;
            refresh.Dock = DockStyle.Fill;
            refresh.Click += async delegate { await Scan(); };
            source.Controls.Add(refresh, 1, 0);
            layout.Controls.Add(source);

            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 12) };
            connect.Text = "Принимать звук телефона";
            connect.AutoSize = true;
            connect.Height = 38;
            connect.Padding = new Padding(8, 4, 8, 4);
            connect.Click += async delegate
            {
                var item = phones.SelectedItem as PhoneItem;
                if (item == null) return;
                wantedId = item.Device.Id;
                wantConnection = true;
                await Connect(receiver.IsOpen);
            };
            stop.Text = "Остановить";
            stop.AutoSize = true;
            stop.Height = 38;
            stop.Padding = new Padding(8, 4, 8, 4);
            stop.Click += delegate { Disconnect(); };
            reconnect.Text = "Восстанавливать связь";
            reconnect.Checked = true;
            reconnect.AutoSize = true;
            reconnect.Margin = new Padding(10, 10, 0, 0);
            actions.Controls.Add(connect);
            actions.Controls.Add(stop);
            actions.Controls.Add(reconnect);
            layout.Controls.Add(actions);

            var volumes = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 0, 0, 8) };
            volumes.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
            volumes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            volumes.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55));
            volumes.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            volumes.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            volumes.Controls.Add(Label("Телефон", 10, FontStyle.Bold, 30), 0, 0);
            foreach (var slider in new[] { phoneVolume })
            {
                slider.Minimum = 0;
                slider.Maximum = 200;
                slider.TickFrequency = 10;
                slider.TickStyle = TickStyle.None;
                slider.LargeChange = 5;
                slider.SmallChange = 1;
                slider.Dock = DockStyle.Fill;
                slider.Enabled = false;
                slider.Margin = new Padding(0);
            }
            phoneVolume.AccessibleName = "Громкость телефона";
            phoneVolume.ValueChanged += delegate
            {
                if (updatingVolumes) return;
                requestedPhonePercent = phoneVolume.Value;
                phonePercent.Text = requestedPhonePercent + "%";
                lastVolumeEdit = DateTime.UtcNow;
                volumePending = true;
                volumeTimer.Start();
            };
            // Keep thumb movement independent of COM enumeration/graph setup.
            // Coalesce changes and apply the latest value at most 25 times/sec.
            volumeTimer.Interval = 40;
            volumeTimer.Tick += async delegate
            {
                if (adjustingVolume) return;
                if (!volumePending || closing) { volumeTimer.Stop(); return; }
                volumePending = false;
                await ChangePhoneVolume();
                if (!volumePending) volumeTimer.Stop();
            };
            phonePercent.Dock = DockStyle.Fill;
            phonePercent.TextAlign = ContentAlignment.TopRight;
            phonePercent.Text = "—";
            volumes.Controls.Add(phoneVolume, 1, 0);
            volumes.Controls.Add(phonePercent, 2, 0);
            volumeHint.Text = "Включи воспроизведение на телефоне, чтобы появился его регулятор.";
            volumeHint.Dock = DockStyle.Fill;
            volumeHint.ForeColor = Color.DimGray;
            volumeHint.Font = new Font("Segoe UI", 9F);
            volumes.Controls.Add(volumeHint, 0, 1);
            volumes.SetColumnSpan(volumeHint, 3);
            layout.Controls.Add(volumes);

            status.Dock = DockStyle.Top;
            status.Height = 55;
            status.Text = "Ищу сопряжённые телефоны…";
            status.Font = new Font(Font, FontStyle.Bold);
            layout.Controls.Add(status);

            layout.Controls.Add(Label(
                "1. Выбери наушники или колонки как выход звука Windows.\r\n" +
                "2. На Android выбери этот ПК для «Мультимедиа» в Bluetooth.\r\n" +
                "3. Запусти звук на телефоне и на ПК.\r\n\r\n" +
                "Принимается звук, который Android отправляет как мультимедиа.\r\n" +
                "Звонки — через «Связь с телефоном». Часть сигналов Android может звучать только на телефоне.",
                10, FontStyle.Regular, 145));

            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.Dock = DockStyle.Fill;
            log.BackColor = Color.White;
            log.AccessibleName = "Журнал подключения";
            var logMenu = new ContextMenuStrip();
            logMenu.Items.Add("Открыть файл журнала", null, delegate { Open(DiagnosticLog.Path); });
            log.ContextMenuStrip = logMenu;
            layout.Controls.Add(log);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть", null, delegate { ShowWindow(); });
            menu.Items.Add("Остановить приём", null, delegate { Disconnect(); });
            menu.Items.Add("Журнал подключения", null, delegate { Open(DiagnosticLog.Path); });
            menu.Items.Add("Выйти", null, delegate { exitRequested = true; Close(); });
            tray.Icon = Icon;
            tray.Text = "Audio Bridge";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowWindow(); };
            tray.Visible = true;
            Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
            Shown += async delegate { await Scan(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!exitRequested && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                    return;
                }
                closing = true;
                SystemEvents.PowerModeChanged -= PowerChanged;
                timer.Stop();
                volumeTimer.Stop();
                cancellation.Cancel();
                ReleaseConnection();
                tray.Visible = false;
                tray.Dispose();
            };
            SystemEvents.PowerModeChanged += PowerChanged;
            timer.Interval = 1000;
            timer.Tick += async delegate { await Tick(); };
            timer.Start();
            UpdateButtons();
        }

        static Label Label(string text, float size, FontStyle style, int height)
        {
            return new Label { Text = text, Height = height, Dock = DockStyle.Top, Font = new Font("Segoe UI", size, style), Margin = new Padding(0) };
        }

        static Button Button(string text, EventHandler action)
        {
            var button = new Button { Text = text, AutoSize = true, Padding = new Padding(5), Margin = new Padding(0, 0, 8, 0) };
            button.Click += action;
            return button;
        }

        void Open(string uri)
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus("Не удалось открыть настройки: " + ex.Message, true); }
        }

        void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

        void Post(Action action)
        {
            if (closing || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        void PowerChanged(object sender, PowerModeChangedEventArgs args)
        {
            Post(delegate
            {
                if (closing || !wantConnection) return;
                AddLog("Питание Windows: " + args.Mode + ".");
                if (args.Mode == PowerModes.Suspend || args.Mode == PowerModes.Resume)
                {
                    suspended = args.Mode == PowerModes.Suspend;
                    cancellation.Cancel();
                    ReleaseConnection();
                    SetStatus(suspended ? "Windows переходит в спящий режим." : "Windows вышла из сна. "
                        + (reconnect.Checked ? "Восстанавливаю связь с телефоном…" : "Нажми «Повторить подключение»."), false);
                }
            });
        }

        void ReceiverStateChanged(bool opened)
        {
            if (closing) return;
            if (!opened) ReleaseAudio();
            else
            {
                connectedOutputId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
                AddLog("Аудиовыход при открытии: " + connectedOutputId);
            }
            SetStatus(opened ? "Bluetooth подключён. Запусти звук на Android.\r\nЕсли звука нет, нажми «Восстановить звук»."
                : "Связь с телефоном прервалась. " + (reconnect.Checked ? "Восстанавливаю автоматически…" : "Нажми «Повторить подключение»."), !opened);
            UpdateVolumes();
            UpdateButtons();
        }

        void UpdateButtons()
        {
            connect.Enabled = !busy && !suspended && phones.SelectedItem != null;
            connect.Text = receiver.IsOpen ? "Восстановить звук" : wantConnection ? "Повторить подключение" : "Принимать звук телефона";
            refresh.Enabled = !busy && !wantConnection;
            phones.Enabled = !busy && !wantConnection;
            stop.Enabled = wantConnection || connection != null;
        }

        void SetStatus(string message, bool error)
        {
            if (closing) return;
            status.Text = message;
            status.ForeColor = error ? Color.FromArgb(175, 45, 30) : Color.FromArgb(28, 85, 60);
            AddLog(message);
        }

        void AddLog(string message)
        {
            DiagnosticLog.Write(message);
            if (closing) return;
            if (log.TextLength > 15000) log.Clear();
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message.Replace("\r\n", " ") + Environment.NewLine);
        }

        async Task Scan()
        {
            if (busy || closing) return;
            busy = true;
            UpdateButtons();
            try
            {
                await CheckOutput();
                var items = await NativeAsync.Result(DeviceInformation.FindAllAsync(AudioPlaybackConnection.GetDeviceSelector()), cancellation.Token);
                if (closing) return;
                var previous = phones.SelectedItem as PhoneItem;
                string selectedId = previous == null ? wantedId : previous.Device.Id;
                phones.Items.Clear();
                foreach (var device in items)
                {
                    var item = new PhoneItem { Device = device };
                    phones.Items.Add(item);
                    if (device.Id == selectedId) phones.SelectedItem = item;
                }
                if (phones.SelectedIndex < 0 && phones.Items.Count > 0) phones.SelectedIndex = 0;
                UpdateVolumes();
                if (connection == null && !wantConnection)
                    SetStatus(items.Count > 0 ? "Телефон найден. Нажми «Принимать звук телефона»." : "Телефоны не найдены. Выполни сопряжение и нажми «Обновить».", items.Count == 0);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SetStatus("Не удалось проверить устройства: " + ex.Message, true); }
            finally { FinishOperation(); }
        }

        async Task CheckOutput()
        {
            try
            {
                string id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
                if (String.IsNullOrEmpty(id)) { output.Text = "В Windows не выбран выход звука."; return; }
                var device = await NativeAsync.Result(DeviceInformation.CreateFromIdAsync(id), cancellation.Token);
                if (closing) return;
                output.Text = device != null
                    ? "Выход Windows: устройство по умолчанию.\r\nЗвук ПК и входящий звук телефона направляются в этот выход."
                    : "Выход звука недоступен. Выбери устройство в настройках Windows.";
            }
            catch (OperationCanceledException) { throw; }
            catch { output.Text = "Выход Windows: проверь выбранное устройство в настройках звука."; }
        }

        async Task Connect(bool restart = false)
        {
            if (busy || closing || suspended || !wantConnection) return;
            busy = true;
            cancellation.Cancel();
            cancellation.Dispose();
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            UpdateButtons();
            try
            {
                if (restart)
                {
                    restorePhonePercent = phoneVolume.Enabled ? phoneVolume.Value : (restorePhonePercent ?? 100);
                    LogAudioRoute("Перед восстановлением звука");
                    ReleaseAudio();
                    connectedOutputId = null;
                }
                SetStatus(restart ? "Восстанавливаю звуковой поток телефона…" : "Открываю аудиосоединение с телефоном…", false);
                bool opened = await receiver.Open(wantedId, token, restart);
                token.ThrowIfCancellationRequested();
                if (!opened)
                {
                    int seconds = Math.Max(0, (int)Math.Ceiling((receiver.NextAttempt - DateTime.UtcNow).TotalSeconds));
                    SetStatus("Телефон пока не подключился. " + (reconnect.Checked ? "Повтор через " + seconds + " с." : "Нажми «Повторить подключение».")
                        + "\r\nПроверь «Мультимедиа» для этого ПК на Android.", true);
                    return;
                }
                await CheckOutput();
                UpdateVolumes();
            }
            catch (OperationCanceledException) { if (!closing && !wantConnection) SetStatus("Приём остановлен.", false); }
            catch (Exception ex)
            {
                ReleaseConnection();
                SetStatus(ex.Message, true);
            }
            finally
            {
                FinishOperation();
            }
        }

        void FinishOperation()
        {
            busy = false;
            if (closing) return;
            if (cancellation.IsCancellationRequested)
            {
                cancellation.Dispose();
                cancellation = new CancellationTokenSource();
            }
            UpdateButtons();
        }

        void ReleaseConnection()
        {
            ReleaseAudio();
            receiver.Dispose();
            connectedOutputId = null;
        }

        void ReleaseAudio()
        {
            audioGeneration++;
            lastAudioSummary = null;
            volumePending = false;
            volumeTimer.Stop();
            StopAmplifier();
            if (volumeControl != null) { volumeControl.Dispose(); volumeControl = null; }
            phoneSessionId = null;
            phoneVolume.Enabled = false;
        }

        void Disconnect()
        {
            wantConnection = false;
            restorePhonePercent = null;
            cancellation.Cancel();
            // Also release during a device refresh; stopping must never leave
            // the receiver open merely because another operation was in flight.
            ReleaseConnection();
            phoneSessionId = null;
            phoneVolume.Enabled = false;
            SetStatus("Приём остановлен. Сопряжение телефона сохранено.", false);
            UpdateButtons();
            if (!busy) { cancellation.Dispose(); cancellation = new CancellationTokenSource(); }
        }

        async Task Tick()
        {
            if (busy || closing || suspended || checkingTick) return;
            checkingTick = true;
            try
            {
                receiver.Poll();
                string currentOutputId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
                if (receiver.IsOpen && !String.IsNullOrEmpty(currentOutputId)
                    && !String.IsNullOrEmpty(connectedOutputId) && currentOutputId != connectedOutputId)
                {
                    AddLog("Выход звука Windows изменился; обновляю маршрут телефона.");
                    if (wantConnection && reconnect.Checked) await Connect(true);
                    else
                    {
                        connectedOutputId = currentOutputId;
                        SetStatus("Выход звука Windows изменился. Нажми «Восстановить звук».", true);
                    }
                }
                else if (wantConnection && reconnect.Checked && receiver.RetryDue)
                    await Connect();
                if (DateTime.UtcNow >= nextVolumeCheck)
                {
                    nextVolumeCheck = DateTime.UtcNow.AddSeconds(4);
                    UpdateVolumes();
                }
                UpdateButtons();
            }
            catch (Exception ex) { ReleaseConnection(); SetStatus("Связь прервалась: " + ex.Message, true); }
            finally { checkingTick = false; }
        }

        void UpdateVolumes()
        {
            if (closing || adjustingVolume || volumePending || phoneVolume.Capture
                || (DateTime.UtcNow - lastVolumeEdit).TotalMilliseconds < 300) return;
            updatingVolumes = true;
            try
            {
                phoneSessionId = null;
                phoneVolume.Enabled = false;
                phonePercent.Text = "—";
                var phone = phones.SelectedItem as PhoneItem;
                if (!receiver.IsOpen || phone == null)
                {
                    volumeHint.Text = "Приём должен быть включён; затем запусти звук на телефоне.";
                    return;
                }
                if (amplifier != null)
                {
                    if (amplifier.Error != null || amplifier.OutputDeviceId != MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default))
                    {
                        string reason = amplifier.Error ?? "Аудиовыход изменился. Усиление отключено.";
                        StopAmplifier();
                        SetStatus(reason, true);
                    }
                    else
                    {
                        phoneSessionId = amplifier.SourceSessionId;
                        phoneVolume.Value = amplifier.Percent;
                        phonePercent.Text = amplifier.Percent + "%";
                        phoneVolume.Enabled = true;
                        volumeHint.Text = phoneVolume.Value > 100
                            ? "101–200%: усиление телефона; возможны искажения громких записей."
                            : "Громкость телефона: 0–200%. Уровни Windows и приложений ПК не меняются.";
                        return;
                    }
                }
                var sessions = AudioMixer.Sessions();
                try
                {
                    MixerSession found = null;
                    int matches = 0;
                    foreach (var session in sessions)
                    {
                        // Match the phone's own A2DP sink, never a shared service
                        // process ID or an unrelated desktop application's audio.
                        if (session.State != 2 && AudioMixer.IsPhoneSource(session.Name, phone.Device.Name))
                        { found = session; matches++; }
                    }
                    if (matches == 1)
                    {
                        string summary = "Аудиопоток телефона: state=" + found.State + "; volume=" + Percent(found.Volume)
                            + "%; muted=" + found.Muted + "; session=" + found.Id;
                        if (summary != lastAudioSummary) { AddLog(summary); lastAudioSummary = summary; }
                        phoneSessionId = found.Id;
                        phoneVolume.Value = Percent(found.Volume);
                        phonePercent.Text = phoneVolume.Value + "%";
                        phoneVolume.Enabled = true;
                        volumeHint.Text = found.Muted ? "Звук телефона выключен. Нажми «Восстановить звук»."
                            : "Громкость телефона: 0–200%. Уровни Windows и приложений ПК не меняются.";
                        if (restorePhonePercent.HasValue)
                        {
                            int restored = restorePhonePercent.Value;
                            restorePhonePercent = null;
                            if (restored > 0 && found.Muted) found.Muted = false;
                            phoneVolume.Value = restored;
                            phonePercent.Text = restored + "%";
                            requestedPhonePercent = restored;
                            lastVolumeEdit = DateTime.UtcNow;
                            volumePending = true;
                            volumeTimer.Start();
                        }
                    }
                    else
                    {
                        string summary = "Аудиопоток телефона: найдено сессий=" + matches + ".";
                        if (summary != lastAudioSummary) { AddLog(summary); lastAudioSummary = summary; }
                        volumeHint.Text = matches > 1 ? "Несколько потоков телефона. Нажми «Восстановить звук»." : "Запусти звук на телефоне. При тишине нажми «Восстановить звук».";
                    }
                }
                finally { foreach (var session in sessions) session.Dispose(); }
            }
            catch { volumeHint.Text = "Поток телефона пока недоступен. Проверь воспроизведение и подключение."; }
            finally { updatingVolumes = false; }
        }

        static int Percent(float value) { return Math.Max(0, Math.Min(100, (int)Math.Round(value * 100))); }

        void LogAudioRoute(string reason)
        {
            AddLog(reason + ": Bluetooth=" + (receiver.IsOpen ? "Opened" : "Closed")
                + "; output=" + MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default)
                + "; amplifier=" + (amplifier == null ? "off" : amplifier.Percent + "%") + ".");
            try
            {
                var phone = phones.SelectedItem as PhoneItem;
                var sessions = AudioMixer.Sessions();
                try
                {
                    int count = 0;
                    foreach (var session in sessions)
                    {
                        if (phone == null || !AudioMixer.IsPhoneSource(session.Name, phone.Device.Name)) continue;
                        count++;
                        float? peak = session.Peak;
                        AddLog("Диагностика потока: state=" + session.State + "; volume=" + Percent(session.Volume)
                            + "%; muted=" + session.Muted + "; peak=" + (peak.HasValue ? peak.Value.ToString("F4") : "unavailable")
                            + "; session=" + session.Id);
                    }
                    if (count == 0) AddLog("Диагностика потока: аудиосессия телефона на текущем выходе отсутствует.");
                }
                finally { foreach (var session in sessions) session.Dispose(); }
            }
            catch (Exception ex) { AddLog("Диагностика потока недоступна: " + ex.Message); }
        }

        void StopAmplifier()
        {
            var old = amplifier;
            amplifier = null;
            if (old != null) old.Dispose();
        }

        async Task ChangePhoneVolume()
        {
            if (phoneSessionId == null || closing || adjustingVolume) return;
            int requested = requestedPhonePercent;
            var phone = phones.SelectedItem as PhoneItem;
            var currentConnection = connection;
            int generation = audioGeneration;
            var token = cancellation.Token;
            adjustingVolume = true;
            try
            {
                if (amplifier != null)
                {
                    amplifier.Percent = requested;
                    volumeHint.Text = requested > 100
                        ? "101–200%: усиление телефона; возможны искажения громких записей."
                        : "Громкость телефона: 0–200%. Уровни Windows и приложений ПК не меняются.";
                    return;
                }
                if (requested > 100)
                {
                    volumeHint.Text = "Подключаю усиление звука телефона…";
                    var created = await PhoneAmplifier.Create(phone.Device.Name, phoneSessionId, requested, token);
                    if (closing || token.IsCancellationRequested || generation != audioGeneration || !receiver.IsOpen || !Object.ReferenceEquals(connection, currentConnection))
                    { created.Dispose(); return; }
                    amplifier = created;
                    // The user may have dragged again while the graph opened.
                    amplifier.Percent = requestedPhonePercent;
                    volumeHint.Text = "Громкость телефона: 0–200%. Уровни Windows и приложений ПК не меняются.";
                    return;
                }
                if (volumeControl == null || volumeControl.Id != phoneSessionId)
                {
                    if (volumeControl != null) volumeControl.Dispose();
                    volumeControl = AudioMixer.OpenSession(phoneSessionId);
                }
                volumeControl.Volume = requested / 100F;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                StopAmplifier();
                if (volumeControl != null) { volumeControl.Dispose(); volumeControl = null; }
                SetStatus("Не удалось изменить громкость телефона: " + ex.Message, true);
            }
            finally { adjustingVolume = false; }
        }
    }
}
