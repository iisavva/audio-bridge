using System;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Media.MediaProperties;

namespace AudioBridge
{
    // A2DP Sink endpoints can be hidden from normal capture enumeration.
    // Open only a discovered SWD\MMDEVAPI capture ID belonging to the phone.
    internal sealed class NativePhoneCapture : IDisposable
    {
        IAudioClient client;
        IAudioCaptureClient capture;
        Thread thread;
        readonly object gate = new object();
        readonly ManualResetEvent initialized = new ManualResetEvent(false);
        readonly ManualResetEvent requested = new ManualResetEvent(false);
        readonly ManualResetEvent stopped = new ManualResetEvent(false);
        Action<byte[]> receive;
        Exception initializationError;
        volatile bool stopping;
        volatile bool workerStopped;
        bool disposed;
        bool handlesDisposed;
        volatile string error;
        int bytesPerFrame;
        internal AudioEncodingProperties Encoding { get; private set; }
        internal string Error { get { return error; } }
        internal bool WorkerStopped { get { return workerStopped; } }

        internal static NativePhoneCapture Open(string id)
        {
            if (String.IsNullOrEmpty(id) || !id.StartsWith("{0.0.1.", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Недопустимый идентификатор A2DP-входа.");
            var result = new NativePhoneCapture();
            // WASAPI requires GetService and the corresponding Release on the
            // same thread. Keep initialization, capture and cleanup on one MTA
            // worker, including disposal before Start and failed initialization.
            result.thread = new Thread(() => result.Run(id)) { IsBackground = true, Name = "Audio Bridge phone capture" };
            result.thread.SetApartmentState(ApartmentState.MTA);
            result.thread.Start();
            if (!result.initialized.WaitOne(10000))
            {
                result.Dispose();
                throw new TimeoutException("Windows не подготовила A2DP-вход за 10 секунд.");
            }
            if (result.initializationError != null)
            {
                result.Dispose();
                throw new InvalidOperationException("Не удалось открыть A2DP-вход: " + result.initializationError.Message, result.initializationError);
            }
            return result;
        }

        void Initialize(string id)
        {
            IMMDevice device = null;
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
            IntPtr format = IntPtr.Zero;
            try
            {
                AudioMixer.Check(enumerator.GetDevice(id, out device));
                uint state; AudioMixer.Check(device.GetState(out state));
                if (state != 1) throw new InvalidOperationException("A2DP-вход телефона неактивен.");
                object activated;
                Guid iid = typeof(IAudioClient).GUID;
                AudioMixer.Check(device.Activate(ref iid, 23, IntPtr.Zero, out activated));
                client = (IAudioClient)activated;
                AudioMixer.Check(client.GetMixFormat(out format));
                ushort tag = (ushort)Marshal.ReadInt16(format, 0);
                ushort channels = (ushort)Marshal.ReadInt16(format, 2);
                uint rate = unchecked((uint)Marshal.ReadInt32(format, 4));
                ushort bits = (ushort)Marshal.ReadInt16(format, 14);
                bytesPerFrame = (ushort)Marshal.ReadInt16(format, 12);
                if (tag == 65534) tag = (ushort)Marshal.ReadInt16(format, 24);
                if ((tag != 1 && tag != 3) || (bits != 16 && bits != 32) || (tag == 3 && bits != 32))
                    throw new NotSupportedException("Неподдерживаемый формат A2DP-входа: " + tag + "/" + bits);
                Encoding = AudioEncodingProperties.CreatePcm(rate, channels, bits);
                if (tag == 3) Encoding.Subtype = MediaEncodingSubtypes.Float;
                AudioMixer.Check(client.Initialize(0, 0, 1000000, 0, format, IntPtr.Zero));
                iid = typeof(IAudioCaptureClient).GUID;
                AudioMixer.Check(client.GetService(ref iid, out activated));
                capture = (IAudioCaptureClient)activated;
            }
            finally { if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format); AudioMixer.Release(device); AudioMixer.Release(enumerator); }
        }

        internal void Start(Action<byte[]> receive)
        {
            if (receive == null) throw new ArgumentNullException("receive");
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("NativePhoneCapture");
                if (this.receive != null) throw new InvalidOperationException("A2DP-захват уже запущен.");
                this.receive = receive;
                requested.Set();
            }
        }

        void Run(string id)
        {
            bool ready = false;
            bool running = false;
            try
            {
                Initialize(id);
                ready = true;
                initialized.Set();
                if (WaitHandle.WaitAny(new WaitHandle[] { stopped, requested }) == 0 || stopping) return;
                AudioMixer.Check(client.Start());
                running = true;
                while (!stopping)
                {
                    uint frames;
                    AudioMixer.Check(capture.GetNextPacketSize(out frames));
                    if (frames == 0) { stopped.WaitOne(5); continue; }
                    IntPtr data; uint flags; ulong position, timestamp;
                    AudioMixer.Check(capture.GetBuffer(out data, out frames, out flags, out position, out timestamp));
                    try
                    {
                        var pcm = new byte[checked((int)frames * bytesPerFrame)];
                        if ((flags & 2) == 0 && data != IntPtr.Zero) Marshal.Copy(data, pcm, 0, pcm.Length);
                        if (!stopping) receive(pcm);
                    }
                    finally { AudioMixer.Check(capture.ReleaseBuffer(frames)); }
                }
            }
            catch (Exception ex)
            {
                if (!ready) initializationError = ex;
                else if (!stopping) error = "A2DP-захват остановлен: " + ex.Message;
            }
            finally
            {
                if (running) { try { client.Stop(); } catch { } }
                Release();
                initialized.Set();
                lock (gate)
                {
                    workerStopped = true;
                    if (disposed) DisposeHandles();
                }
            }
        }

        void Release()
        {
            AudioMixer.Release(capture); capture = null;
            AudioMixer.Release(client); client = null;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                stopping = true;
                stopped.Set();
                if (workerStopped) DisposeHandles();
            }
            if (thread != Thread.CurrentThread) thread.Join(2000);
        }

        void DisposeHandles()
        {
            if (handlesDisposed) return;
            handlesDisposed = true;
            initialized.Dispose();
            requested.Dispose();
            stopped.Dispose();
        }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong timestamp);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }
}
