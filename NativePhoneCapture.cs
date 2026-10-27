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
        readonly ManualResetEvent stopped = new ManualResetEvent(false);
        volatile bool stopping;
        bool disposed;
        volatile string error;
        int bytesPerFrame;
        internal AudioEncodingProperties Encoding { get; private set; }
        internal string Error { get { return error; } }

        internal static NativePhoneCapture Open(string id)
        {
            if (String.IsNullOrEmpty(id) || !id.StartsWith("{0.0.1.", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Недопустимый идентификатор A2DP-входа.");
            var result = new NativePhoneCapture();
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
                result.client = (IAudioClient)activated;
                AudioMixer.Check(result.client.GetMixFormat(out format));
                ushort tag = (ushort)Marshal.ReadInt16(format, 0);
                ushort channels = (ushort)Marshal.ReadInt16(format, 2);
                uint rate = unchecked((uint)Marshal.ReadInt32(format, 4));
                ushort bits = (ushort)Marshal.ReadInt16(format, 14);
                result.bytesPerFrame = (ushort)Marshal.ReadInt16(format, 12);
                if (tag == 65534) tag = (ushort)Marshal.ReadInt16(format, 24);
                if ((tag != 1 && tag != 3) || (bits != 16 && bits != 32) || (tag == 3 && bits != 32))
                    throw new NotSupportedException("Неподдерживаемый формат A2DP-входа: " + tag + "/" + bits);
                result.Encoding = AudioEncodingProperties.CreatePcm(rate, channels, bits);
                if (tag == 3) result.Encoding.Subtype = MediaEncodingSubtypes.Float;
                AudioMixer.Check(result.client.Initialize(0, 0, 1000000, 0, format, IntPtr.Zero));
                iid = typeof(IAudioCaptureClient).GUID;
                AudioMixer.Check(result.client.GetService(ref iid, out activated));
                result.capture = (IAudioCaptureClient)activated;
                return result;
            }
            catch { result.Dispose(); throw; }
            finally { if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format); AudioMixer.Release(device); AudioMixer.Release(enumerator); }
        }

        internal void Start(Action<byte[]> receive)
        {
            thread = new Thread(delegate()
            {
                try
                {
                    AudioMixer.Check(client.Start());
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
                catch (Exception ex) { if (!stopping) error = "A2DP-захват остановлен: " + ex.Message; }
                finally { try { client.Stop(); } catch { } Release(); }
            }) { IsBackground = true, Name = "Audio Bridge phone capture" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        void Release()
        {
            AudioMixer.Release(capture); capture = null;
            AudioMixer.Release(client); client = null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            stopping = true;
            stopped.Set();
            if (thread == null) { Release(); stopped.Dispose(); }
            else if (thread.Join(2000)) stopped.Dispose();
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
