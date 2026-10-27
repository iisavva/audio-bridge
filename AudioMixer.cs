using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AudioBridge
{
    internal sealed class MixerSession : IDisposable
    {
        internal IAudioSessionControl2 Control;
        internal string Id;
        internal string Name;
        internal uint ProcessId;
        internal int State;
        internal float Volume
        {
            get { float value; AudioMixer.Check(((ISimpleAudioVolume)Control).GetMasterVolume(out value)); return value; }
            set { Guid context = Guid.Empty; AudioMixer.Check(((ISimpleAudioVolume)Control).SetMasterVolume(value, ref context)); }
        }
        internal bool Muted
        {
            get { bool value; AudioMixer.Check(((ISimpleAudioVolume)Control).GetMute(out value)); return value; }
            set { Guid context = Guid.Empty; AudioMixer.Check(((ISimpleAudioVolume)Control).SetMute(value, ref context)); }
        }
        public void Dispose() { AudioMixer.Release(Control); Control = null; }
    }

    internal static class AudioMixer
    {
        internal static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
        internal static void Release(object obj) { if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); }

        static IMMDevice DefaultOutput()
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
            try { IMMDevice device; Check(enumerator.GetDefaultAudioEndpoint(0, 1, out device)); return device; }
            finally { Release(enumerator); }
        }

        internal static List<MixerSession> Sessions()
        {
            IMMDevice device = null;
            object managerObject = null;
            IAudioSessionEnumerator enumerator = null;
            var result = new List<MixerSession>();
            try
            {
                device = DefaultOutput();
                Guid iid = typeof(IAudioSessionManager2).GUID;
                Check(device.Activate(ref iid, 23, IntPtr.Zero, out managerObject));
                var manager = (IAudioSessionManager2)managerObject;
                Check(manager.GetSessionEnumerator(out enumerator));
                int count;
                Check(enumerator.GetCount(out count));
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl2 control = null;
                    try
                    {
                        Check(enumerator.GetSession(i, out control));
                        uint pid;
                        string name, id;
                        int state;
                        Check(control.GetProcessId(out pid));
                        Check(control.GetDisplayName(out name));
                        Check(control.GetSessionInstanceIdentifier(out id));
                        Check(control.GetState(out state));
                        if (String.IsNullOrEmpty(name))
                        {
                            try { using (var process = Process.GetProcessById((int)pid)) name = process.ProcessName; }
                            catch { name = pid == 0 ? "System sounds" : "Audio"; }
                        }
                        result.Add(new MixerSession { Control = control, Id = id, Name = name, ProcessId = pid, State = state });
                        control = null;
                    }
                    catch { }
                    finally { Release(control); }
                }
                return result;
            }
            catch { foreach (var session in result) session.Dispose(); throw; }
            finally { Release(enumerator); Release(managerObject); Release(device); }
        }

        internal static MixerSession OpenSession(string id)
        {
            var sessions = Sessions();
            MixerSession found = null;
            try
            {
                foreach (var session in sessions)
                    if (session.Id == id && session.State != 2) { found = session; break; }
                if (found == null) throw new InvalidOperationException("Аудиопоток телефона больше недоступен.");
                return found;
            }
            finally { foreach (var session in sessions) if (session != found) session.Dispose(); }
        }

        internal static bool IsPhoneSource(string name, string phoneName)
        {
            return !String.IsNullOrEmpty(phoneName) && !String.IsNullOrEmpty(name)
                && name.IndexOf(phoneName, StringComparison.OrdinalIgnoreCase) >= 0
                && name.IndexOf("A2DP SNK", StringComparison.OrdinalIgnoreCase) >= 0;
        }

    }

    // Method order follows mmdeviceapi.h and audiopolicy.h. Unused prefix
    // methods preserve native vtable slots.
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid session, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid session, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr callback);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr callback);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float value, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float value);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }

}
