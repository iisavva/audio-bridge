using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;
using Windows.Media;
using Windows.Media.Devices;
using Windows.Media.Render;

namespace AudioBridge
{
    // Only the explicitly selected A2DP sink is captured. Never fall back to
    // the default microphone or loop back the complete Windows audio mix.
    internal sealed class PhoneAmplifier : IDisposable
    {
        AudioGraph graph;
        AudioFrameInputNode input;
        NativePhoneCapture capture;
        volatile bool stopping;
        MixerSession source;
        bool previousMute;
        bool muteApplied;
        volatile string error;
        int percent = -1;
        internal string OutputDeviceId { get; private set; }
        internal string SourceSessionId { get; private set; }
        internal string Error { get { return error ?? (capture == null ? null : capture.Error); } }
        internal int Percent
        {
            get { return percent > 100 ? percent : Math.Max(0, Math.Min(100, (int)Math.Round(source.Volume * 100))); }
            set
            {
                if (value < 0 || value > 200) throw new ArgumentOutOfRangeException("value");
                if (value > 100)
                {
                    if (!muteApplied)
                    {
                        previousMute = source.Muted;
                        source.Muted = true;
                        muteApplied = true;
                    }
                    input.OutgoingGain = previousMute ? 0 : value / 100.0;
                }
                else
                {
                    input.OutgoingGain = 0;
                    if (muteApplied) { source.Muted = previousMute; muteApplied = false; }
                    source.Volume = value / 100F;
                }
                percent = value;
            }
        }

        internal static async Task<PhoneAmplifier> Create(string phoneName, string sessionId, int percent, CancellationToken token)
        {
            var amplifier = new PhoneAmplifier();
            try
            {
                await amplifier.Initialize(phoneName, sessionId, percent, token);
                return amplifier;
            }
            catch { amplifier.Dispose(); throw; }
        }

        async Task Initialize(string phoneName, string sessionId, int requestedPercent, CancellationToken token)
        {
            var devices = await NativeAsync.Result(DeviceInformation.FindAllAsync("", null, DeviceInformationKind.Device), token);
            string captureId = null;
            int matches = 0;
            foreach (var device in devices)
                if (device.Id.StartsWith("SWD\\MMDEVAPI\\{0.0.1.", StringComparison.OrdinalIgnoreCase)
                    && AudioMixer.IsPhoneSource(device.Name, phoneName))
                { captureId = device.Id.Substring("SWD\\MMDEVAPI\\".Length); matches++; }
            if (matches != 1)
                throw new InvalidOperationException(matches == 0
                    ? "Windows не предоставила отдельный A2DP-вход телефона для усиления. Доступна громкость до 100%."
                    : "Найдено несколько A2DP-входов телефона. Нельзя однозначно выбрать поток для усиления.");

            OutputDeviceId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            if (String.IsNullOrEmpty(OutputDeviceId)) throw new InvalidOperationException("Не выбран аудиовыход Windows.");
            var renderDevice = await NativeAsync.Result(DeviceInformation.CreateFromIdAsync(OutputDeviceId), token);
            capture = await Task.Run(delegate { return NativePhoneCapture.Open(captureId); });
            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                PrimaryRenderDevice = renderDevice,
                EncodingProperties = capture.Encoding,
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency
            };
            var created = await NativeAsync.Result(AudioGraph.CreateAsync(settings), token);
            if (created.Status != AudioGraphCreationStatus.Success)
                throw new InvalidOperationException("Не удалось создать усилитель: " + created.Status);
            graph = created.Graph;
            graph.UnrecoverableErrorOccurred += delegate(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args)
            { error = "Усилитель остановлен: " + args.Error; };

            var output = await NativeAsync.Result(graph.CreateDeviceOutputNodeAsync(), token);
            if (output.Status != AudioDeviceNodeCreationStatus.Success)
                throw new InvalidOperationException("Не удалось открыть выход усилителя: " + output.Status);
            input = graph.CreateFrameInputNode(capture.Encoding);
            input.OutgoingGain = 0;
            input.AddOutgoingConnection(output.DeviceOutputNode);

            token.ThrowIfCancellationRequested();
            source = AudioMixer.OpenSession(sessionId);
            SourceSessionId = sessionId;
            Percent = requestedPercent;
            graph.Start();
            capture.Start(Receive);
        }

        void Receive(byte[] pcm)
        {
            if (stopping || pcm.Length == 0) return;
            if (input.QueuedSampleCount > capture.Encoding.SampleRate / 5) input.DiscardQueuedFrames();
            using (var frame = new AudioFrame((uint)pcm.Length))
            {
                using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
                using (var reference = buffer.CreateReference())
                {
                    IntPtr data; uint capacity;
                    ((IMemoryBufferByteAccess)reference).GetBuffer(out data, out capacity);
                    if (capacity < pcm.Length) throw new InvalidOperationException("Недостаточный аудиобуфер.");
                    Marshal.Copy(pcm, 0, data, pcm.Length);
                    buffer.Length = (uint)pcm.Length;
                }
                input.AddFrame(frame);
            }
        }

        public void Dispose()
        {
            // Restore the original route on every exit path, including a failed
            // initialization, disconnect, device change and application exit.
            stopping = true;
            if (capture != null) { capture.Dispose(); capture = null; }
            if (graph != null)
            {
                try { graph.Stop(); } catch { }
                try { graph.Dispose(); } catch { }
                graph = null;
            }
            if (source != null)
            {
                try { if (muteApplied) source.Muted = previousMute; }
                catch { } // The session may already have expired after Bluetooth disconnect.
                finally { source.Dispose(); source = null; }
            }
            input = null;
            muteApplied = false;
        }
    }
}
