using System;
using System.Threading.Tasks;
using Windows.Media.Audio;
using Windows.Devices.Enumeration;

class Probe
{
    [STAThread]
    static int Main()
    {
        try { return Run().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static async Task<int> Run()
    {
        Console.WriteLine("AudioPlaybackConnection: available");
        var operation = DeviceInformation.FindAllAsync(AudioPlaybackConnection.GetDeviceSelector());
        while (operation.Status == Windows.Foundation.AsyncStatus.Started) await Task.Delay(100);
        var devices = operation.GetResults();
        Console.WriteLine("Compatible paired devices: " + devices.Count);
        foreach (var device in devices) Console.WriteLine(device.Name);
        return 0;
    }
}
