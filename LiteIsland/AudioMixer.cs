using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiteIsland;

/// <summary>One program that is making (or can make) sound, with its own volume.</summary>
public sealed class MixApp
{
    public string Name = "";
    public string? Path;
    public uint Pid;
    public bool Active;
    public float Volume;
    public bool Muted;
    internal List<AudioMixer.ISimpleVolume> Volumes = new();

    public void SetVolume(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        Guid ctx = Guid.Empty;
        foreach (var s in Volumes) { try { s.SetMasterVolume(v, ref ctx); } catch { } }
        Volume = v;
        if (v > 0.001f && Muted) SetMute(false);
    }

    public void SetMute(bool m)
    {
        Guid ctx = Guid.Empty;
        foreach (var s in Volumes) { try { s.SetMute(m, ref ctx); } catch { } }
        Muted = m;
    }

    public void Release()
    {
        foreach (var s in Volumes) { try { Marshal.ReleaseComObject(s); } catch { } }
        Volumes.Clear();
    }
}

/// <summary>Per-app volume (Windows Core Audio sessions). Used only while the Mixer page is open.</summary>
public static class AudioMixer
{
    public static List<MixApp> Query()
    {
        var byPid = new Dictionary<uint, MixApp>();
        object? enumObj = null, devObj = null;
        try
        {
            enumObj = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!);
            var en = (IMxEnumerator)enumObj!;
            if (en.GetDefaultAudioEndpoint(0, 1, out IMxDevice dev) != 0 || dev == null) return new List<MixApp>();
            devObj = dev;
            Guid iid = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
            dev.Activate(ref iid, 23, IntPtr.Zero, out object mgrObj);
            var mgr = (IMxSessionManager2)mgrObj;
            mgr.GetSessionEnumerator(out IMxSessionEnumerator list);
            list.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    list.GetSession(i, out IMxSessionControl ctl);
                    var c2 = (IMxSessionControl2)ctl;
                    c2.GetProcessId(out uint pid);
                    bool system = c2.IsSystemSoundsSession() == 0;
                    ctl.GetState(out int state);
                    var vol = (ISimpleVolume)ctl;
                    if (system || pid == 0 || state == 2) { Marshal.ReleaseComObject(ctl); continue; }

                    if (!byPid.TryGetValue(pid, out MixApp? app))
                    {
                        app = new MixApp { Pid = pid };
                        Describe(app);
                        vol.GetMasterVolume(out app.Volume);
                        vol.GetMute(out bool m);
                        app.Muted = m;
                        byPid[pid] = app;
                    }
                    app.Active |= state == 1;
                    app.Volumes.Add(vol);
                }
                catch { }
            }
            Marshal.ReleaseComObject(list);
            Marshal.ReleaseComObject(mgr);
        }
        catch { }
        finally
        {
            try { if (devObj != null) Marshal.ReleaseComObject(devObj); } catch { }
            try { if (enumObj != null) Marshal.ReleaseComObject(enumObj); } catch { }
        }

        var res = new List<MixApp>(byPid.Values);
        res.Sort((a, b) => a.Active != b.Active ? (a.Active ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return res;
    }

    private static void Describe(MixApp app)
    {
        try
        {
            using var p = Process.GetProcessById((int)app.Pid);
            string name = p.ProcessName;
            try
            {
                string? path = p.MainModule?.FileName;
                app.Path = path;
                if (path != null)
                {
                    string d = FileVersionInfo.GetVersionInfo(path).FileDescription ?? "";
                    if (d.Trim().Length > 0) name = d.Trim();
                }
            }
            catch { }
            if (name.Length > 0) name = char.ToUpperInvariant(name[0]) + name.Substring(1);
            app.Name = name;
        }
        catch { app.Name = "App " + app.Pid; }
    }

    // ---------- Core Audio interfaces (only what is needed, in the exact vtable order) ----------

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMxEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMxDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMxDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMxSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr g, uint flags, out IntPtr ctl);   // IAudioSessionManager
        [PreserveSig] int GetSimpleAudioVolume(IntPtr g, uint flags, out IntPtr vol);     // IAudioSessionManager
        [PreserveSig] int GetSessionEnumerator(out IMxSessionEnumerator list);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMxSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IMxSessionControl ctl);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMxSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMxSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
