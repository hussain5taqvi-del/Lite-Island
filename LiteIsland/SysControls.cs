using System;
using System.Management;
using System.Runtime.InteropServices;

namespace LiteIsland;

/// <summary>Reads and sets the PC volume, the screen brightness and the Windows notification switch. Small and short-lived calls.</summary>
internal static class SysControls
{
    // ---------- volume (Core Audio) ----------
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MmEnumCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMmEnum
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMmDev device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMmDev
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointVol
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr n);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr n);
        [PreserveSig] int GetChannelCount(out uint c);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    private static IEndpointVol? Open()
    {
        try
        {
            var en = (IMmEnum)new MmEnumCom();
            if (en.GetDefaultAudioEndpoint(0, 1, out IMmDev dev) != 0 || dev == null) return null;
            Guid iid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out object o) != 0) return null;
            return (IEndpointVol)o;
        }
        catch { return null; }
    }

    /// <summary>0..1, or -1 when there is no audio device.</summary>
    public static float GetVolume()
    {
        var v = Open();
        if (v == null) return -1;
        try { return v.GetMasterVolumeLevelScalar(out float f) == 0 ? f : -1; }
        catch { return -1; }
        finally { try { Marshal.ReleaseComObject(v); } catch { } }
    }

    public static void SetVolume(float level)
    {
        var v = Open();
        if (v == null) return;
        try
        {
            Guid g = Guid.Empty;
            v.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref g);
            if (level > 0.001f) v.SetMute(false, ref g);
        }
        catch { }
        finally { try { Marshal.ReleaseComObject(v); } catch { } }
    }

    // ---------- brightness (WMI, laptop screens) ----------
    /// <summary>0..100, or -1 when the screen can not be controlled.</summary>
    public static int GetBrightness()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementBaseObject o in s.Get())
            {
                int v = Convert.ToInt32(o["CurrentBrightness"]);
                o.Dispose();
                return v;
            }
        }
        catch { }
        return -1;
    }

    private static System.Management.ManagementObject? _brObj;
    private static int _brPending = -1, _brBusy;

    /// <summary>Latest value wins: while a slider is dragged only the newest value is sent (one WMI call at a time).</summary>
    public static void RequestBrightness(int percent)
    {
        System.Threading.Interlocked.Exchange(ref _brPending, Math.Clamp(percent, 0, 100));
        if (System.Threading.Interlocked.Exchange(ref _brBusy, 1) == 1) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    int p = System.Threading.Interlocked.Exchange(ref _brPending, -1);
                    if (p < 0) break;
                    SetBrightness(p);
                }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _brBusy, 0);
                if (System.Threading.Volatile.Read(ref _brPending) >= 0) RequestBrightness(System.Threading.Volatile.Read(ref _brPending));
            }
        });
    }

    public static void SetBrightness(int percent)
    {
        byte v = (byte)Math.Clamp(percent, 0, 100);
        try
        {
            if (_brObj != null)
            {
                try { _brObj.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, v }); return; }
                catch { _brObj.Dispose(); _brObj = null; }
            }
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in s.Get())
            {
                o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, v });
                if (_brObj == null) _brObj = o; else o.Dispose();
            }
        }
        catch { }
    }

    // ---------- notifications (Windows "Get notifications from apps") ----------
    private const string ToastKey = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";

    public static bool GetToasts()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ToastKey);
            return !(k?.GetValue("ToastEnabled") is int i && i == 0);
        }
        catch { return true; }
    }

    public static void SetToasts(bool on)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ToastKey);
            k?.SetValue("ToastEnabled", on ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch { }
    }
}
