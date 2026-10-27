using System;
using AudioBridge;

class MixerProbe
{
    [STAThread]
    static int Main()
    {
        try
        {
            var sessions = AudioMixer.Sessions();
            try
            {
                foreach (var session in sessions)
                    Console.WriteLine("PID=" + session.ProcessId + " State=" + session.State + " Volume=" + Math.Round(session.Volume * 100) + "% Muted=" + session.Muted + " Name=" + session.Name);
            }
            finally { foreach (var session in sessions) session.Dispose(); }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
