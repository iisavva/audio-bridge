using System;
using System.Collections.Generic;
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
                else if (args.Length > 0 && args[0] == "--connection") TestConnections().GetAwaiter().GetResult();
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
            await TestConnections();
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

        static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        static async Task TestConnections()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var links = new List<FakeLink>();
            var notifications = new Queue<Action>();
            Func<string, IPlaybackLink> factory = delegate(string id) { var link = new FakeLink(); links.Add(link); return link; };
            Action<string> silentLog = delegate { };
            Action<Action> inline = action => action();

            using (var receiver = new PhoneReceiver(factory, inline, silentLog, () => clock))
            {
                // First failure must not unregister the sink or restart StartAsync.
                Require(!await receiver.Open("phone", CancellationToken.None), "Expected an initial timeout.");
                var link = links[links.Count - 1];
                Require(!link.Disposed && link.Starts == 1 && !receiver.RetryDue, "Timeout disabled the incoming receiver.");
                clock = clock.AddSeconds(2);
                Require(receiver.RetryDue, "First retry must be due after two seconds.");
                link.NextOpen = delegate { link.SetOpen(true); return Task.FromResult<string>(null); };
                Require(await receiver.Open("phone", CancellationToken.None), "Second attempt did not connect.");
                Require(link.Starts == 1 && links.Count == 1, "Retry unnecessarily recreated the receiver.");

                // Android may initiate connection without waiting for our next Open.
                link.SetOpen(false);
                Require(!receiver.IsOpen && !link.Disposed, "A disconnect destroyed the listening sink.");
                link.SetOpen(true);
                Require(receiver.IsOpen && !receiver.RetryDue, "Incoming reconnection was not recognized.");
                link.SetOpen(false);
                clock = clock.AddSeconds(2);
                Require(receiver.RetryDue, "A new dropout did not reset retry delay.");
            }
            Console.WriteLine("PASS: timeout retry, retained sink, incoming reconnection and dropout recovery.");

            links.Clear();
            using (var receiver = new PhoneReceiver(factory, inline, silentLog, () => clock))
            {
                foreach (int delay in new[] { 2, 4, 8, 15, 15 })
                {
                    Require(!await receiver.Open("phone", CancellationToken.None), "Expected a timeout.");
                    Require(receiver.NextAttempt == clock.AddSeconds(delay), "Incorrect retry backoff.");
                    Require(receiver.Current != null && !links[links.Count - 1].Disposed, "A retry interval left no incoming receiver.");
                    clock = clock.AddSeconds(delay);
                }
                Require(links.Count == 2 && links[0].Disposed && !links[1].Disposed, "A repeatedly failing stale receiver was not renewed.");
            }
            Console.WriteLine("PASS: bounded retry delays and renewal after repeated failures.");

            links.Clear();
            using (var receiver = new PhoneReceiver(factory, action => notifications.Enqueue(action), silentLog, () => clock))
            {
                var pending = new TaskCompletionSource<string>();
                // Enable the link with one failed attempt, then hold Open in flight.
                await receiver.Open("phone", CancellationToken.None);
                var link = links[0];
                link.NextOpen = () => pending.Task;
                Task<bool> first = receiver.Open("phone", CancellationToken.None);
                int opens = link.Opens;
                Require(!await receiver.Open("phone", CancellationToken.None) && opens == link.Opens, "Concurrent Open calls overlapped.");
                link.SetOpen(true); // Queued notification from the soon-to-be-stopped object.
                receiver.Dispose();
                pending.SetResult(null);
                Require(!await first, "A completed stale Open resurrected a stopped receiver.");
                while (notifications.Count > 0) notifications.Dequeue()();
                Require(receiver.Current == null && !receiver.IsOpen && link.Disposed, "Stale state notification resurrected the receiver.");
            }
            Console.WriteLine("PASS: stop during Open, stale notifications and no overlapping attempts.");

            links.Clear();
            using (var receiver = new PhoneReceiver(factory, inline, silentLog, () => clock))
            {
                await receiver.Open("phone", CancellationToken.None);
                var broken = links[0];
                broken.NextOpen = delegate { throw new TimeoutException("OS operation timed out."); };
                Require(!await receiver.Open("phone", CancellationToken.None) && broken.Disposed, "Native timeout did not release the failed operation's receiver.");
                await receiver.Open("phone", CancellationToken.None);
                Require(links.Count == 2, "Native failure did not allow a fresh receiver.");
                var replacement = links[1];
                replacement.SetOpen(true);
                replacement.ThrowOnState = true;
                receiver.Poll();
                Require(!receiver.IsOpen && replacement.Disposed, "Adapter removal did not invalidate the receiver.");
            }
            Console.WriteLine("PASS: native timeout and adapter state errors recover with a fresh receiver.");

            links.Clear();
            using (var receiver = new PhoneReceiver(factory, inline, silentLog, () => clock))
            using (var cancellation = new CancellationTokenSource())
            {
                await receiver.Open("phone", CancellationToken.None);
                var pending = new TaskCompletionSource<string>();
                var link = links[0];
                link.NextOpen = () => pending.Task;
                Task<bool> attempt = receiver.Open("phone", cancellation.Token);
                cancellation.Cancel();
                pending.SetResult(null);
                bool canceled = false;
                try { await attempt; } catch (OperationCanceledException) { canceled = true; }
                Require(canceled && receiver.Current == null && link.Disposed, "Cancellation left an active receiver.");
                await receiver.Open("phone", CancellationToken.None);
                Require(links.Count == 2, "Reconnection after cancellation failed.");
            }
            Console.WriteLine("PASS: cancellation releases resources and permits reconnection.");
        }

        sealed class FakeLink : IPlaybackLink
        {
            bool open;
            internal bool Disposed;
            internal bool ThrowOnState;
            internal int Starts, Opens;
            internal Func<Task<string>> NextOpen = () => Task.FromResult("RequestTimedOut");
            public bool IsOpen { get { if (ThrowOnState) throw new InvalidOperationException("Adapter unavailable."); return open; } }
            public event EventHandler StateChanged;
            internal void SetOpen(bool value)
            {
                open = value;
                var handler = StateChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            public Task Start(CancellationToken token) { Starts++; return Task.FromResult(0); }
            public Task<string> Open(CancellationToken token) { Opens++; return NextOpen(); }
            public void Dispose() { Disposed = true; }
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
