using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Devices;
using Windows.Media.Render;

namespace AudioBridge.Tests
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && args[0] == "--inputs") PrintInputs().GetAwaiter().GetResult();
                else if (args.Length > 1 && args[0] == "--capture-id") ProbeCapture(args[1]).GetAwaiter().GetResult();
                else Run().GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        static async Task PrintInputs()
        {
            var devices = await NativeAsync.Result(DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector()), CancellationToken.None);
            foreach (var device in devices) Console.WriteLine("Enabled=" + device.IsEnabled + " " + device.Name);
            var pnp = await NativeAsync.Result(DeviceInformation.FindAllAsync("", null, DeviceInformationKind.Device), CancellationToken.None);
            foreach (var device in pnp) if (device.Name.IndexOf("A2DP SNK", StringComparison.OrdinalIgnoreCase) >= 0) Console.WriteLine("PnP=" + device.Name + " ID=" + device.Id);
        }

        static async Task ProbeCapture(string id)
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
            IMMDevice device = null;
            try
            {
                AudioMixer.Check(enumerator.GetDevice(id, out device));
                uint state; AudioMixer.Check(device.GetState(out state));
                Console.WriteLine("Direct capture device state=" + state);
                using (var capture = await Task.Run(delegate { return NativePhoneCapture.Open(id); }))
                {
                    Console.WriteLine("Native format=" + capture.Encoding.Subtype + " rate=" + capture.Encoding.SampleRate + " channels=" + capture.Encoding.ChannelCount);
                }
            }
            finally { AudioMixer.Release(device); AudioMixer.Release(enumerator); }
        }

        static async Task Run()
        {
            if (!AudioMixer.IsPhoneSource("Microphone (Test Phone A2DP SNK)", "Test Phone")
                || AudioMixer.IsPhoneSource("Microphone (Test Phone)", "Test Phone")
                || AudioMixer.IsPhoneSource("Microphone (Other Phone A2DP SNK)", "Test Phone")
                || AudioMixer.IsPhoneSource("Microphone", "")
                || AudioMixer.IsPhoneSource(null, "Test Phone"))
                throw new Exception("A2DP source selection must never select an unrelated microphone.");
            Console.WriteLine("PASS: only the selected A2DP source matches.");

            // No device output node is created: these tests process synthetic
            // samples entirely inside AudioGraph and never play test audio.
            foreach (double gain in new[] { 0.0, 0.5, 1.0, 1.5, 2.0 })
            {
                double measured = await MeasureGain(gain);
                double expected = 0.125 * gain;
                if (Math.Abs(measured - expected) > 0.0001)
                    throw new Exception("Incorrect gain " + gain + ": expected " + expected + ", got " + measured);
                Console.WriteLine("PASS: " + (gain * 100) + "%; sample peak=" + measured.ToString("F4"));
            }
        }

        static async Task<double> MeasureGain(double gain)
        {
            var result = await NativeAsync.Result(AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Media)), CancellationToken.None);
            if (result.Status != AudioGraphCreationStatus.Success) throw new Exception("AudioGraph: " + result.Status);
            using (var graph = result.Graph)
            {
                if (!String.Equals(graph.EncodingProperties.Subtype, "Float", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Float audio expected; actual format: " + graph.EncodingProperties.Subtype);
                var input = graph.CreateFrameInputNode(graph.EncodingProperties);
                var output = graph.CreateFrameOutputNode(graph.EncodingProperties);
                input.OutgoingGain = gain;
                input.AddOutgoingConnection(output);
                int sampleCount = checked((int)(graph.EncodingProperties.SampleRate * graph.EncodingProperties.ChannelCount / 2));
                var samples = new float[sampleCount];
                for (int i = 0; i < samples.Length; i++) samples[i] = 0.125F;
                using (var frame = new AudioFrame(checked((uint)(samples.Length * 4))))
                {
                    using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
                    using (var reference = buffer.CreateReference())
                    {
                        IntPtr memory; uint capacity;
                        ((IMemoryBufferByteAccess)reference).GetBuffer(out memory, out capacity);
                        if (capacity < samples.Length * 4) throw new Exception("Input buffer is too small.");
                        Marshal.Copy(samples, 0, memory, samples.Length);
                        buffer.Length = checked((uint)(samples.Length * 4));
                    }
                    input.AddFrame(frame);
                    graph.Start();
                    await Task.Delay(180);
                    using (var captured = output.GetFrame())
                    using (var buffer = captured.LockBuffer(AudioBufferAccessMode.Read))
                    using (var reference = buffer.CreateReference())
                    {
                        if (buffer.Length == 0) throw new Exception("AudioGraph produced no samples.");
                        IntPtr memory; uint capacity;
                        ((IMemoryBufferByteAccess)reference).GetBuffer(out memory, out capacity);
                        var processed = new float[buffer.Length / 4];
                        Marshal.Copy(memory, processed, 0, processed.Length);
                        double peak = 0;
                        foreach (float sample in processed) peak = Math.Max(peak, Math.Abs(sample));
                        graph.Stop();
                        return peak;
                    }
                }
            }
        }
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }
}
