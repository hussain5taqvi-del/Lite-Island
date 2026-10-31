using System;
using System.Runtime.InteropServices;

namespace LiteIsland;

/// <summary>
/// Reads how loud the sound going to the speakers is right now (0 to 1), using the Windows
/// audio meter. It does not record anything, needs no extra library and uses almost no CPU.
/// </summary>
public sealed class AudioMeter : IDisposable
{
    private readonly System.Collections.Generic.List<IAudioMeterInformation> _meters = new();
    private long _retryAt;
    private long _openedAt;

    // Every active sound output has its own meter (speakers, wired / Bluetooth headphones, hands-free...).
    // The music can go to any of them, and Windows moves it around when a headset is plugged in or out,
    // so the loudest of ALL active outputs is used and the list is made again every second and a half.
    private const long ReopenMs = 1500;

    /// <summary>Current output loudness, 0 (silence) to 1 (full scale).</summary>
    public float GetPeak()
    {
        try
        {
            long now = Environment.TickCount64;
            if (now - _openedAt >= ReopenMs || (_meters.Count == 0 && now >= _retryAt))
            {
                Release();
                OpenAll(_meters);
                _openedAt = now;
                if (_meters.Count == 0) { _retryAt = now + 1000; return 0; }
            }

            float max = 0;
            bool bad = false;
            foreach (var m in _meters)
            {
                if (m.GetPeakValue(out float p) != 0) { bad = true; continue; }
                if (p > max) max = p;
            }
            if (bad) _openedAt = 0; // a device went away: look again right now
            return Math.Clamp(max, 0f, 1f);
        }
        catch
        {
            Release();
            return 0;
        }
    }

    private static void OpenAll(System.Collections.Generic.List<IAudioMeterInformation> list)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            // 0 = render (outputs), 1 = only devices that are active right now
            if (enumerator.EnumAudioEndpoints(0, 1, out IMMDeviceCollection devices) != 0) return;
            try
            {
                if (devices.GetCount(out uint count) != 0) return;
                for (uint i = 0; i < count && i < 12; i++)
                {
                    if (devices.Item(i, out IMMDevice device) != 0) continue;
                    try
                    {
                        Guid iid = typeof(IAudioMeterInformation).GUID;
                        if (device.Activate(ref iid, 23, IntPtr.Zero, out object meter) == 0)
                            list.Add((IAudioMeterInformation)meter);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(devices);
            }
        }
        catch
        {
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private void Release()
    {
        foreach (var m in _meters) { try { Marshal.ReleaseComObject(m); } catch { } }
        _meters.Clear();
    }

    public void Dispose() => Release();

    // ---------- Windows Core Audio COM interfaces (only the methods we use) ----------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float peak);
    }
}
