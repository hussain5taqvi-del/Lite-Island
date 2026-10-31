using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace LiteIsland;

public sealed class SysSample
{
    public double Cpu;
    public double RamPct, RamUsedGb, RamTotalGb;
    public double DiskPct, DiskUsedGb, DiskTotalGb;
    public string DiskName = "C:";
    public double DownBps, UpBps;
    public double? Gpu;
    public bool Primed; // false for the very first sample (CPU and network need two samples)
}

/// <summary>
/// Real numbers only. CPU / RAM / network / disk come from Windows directly, GPU load from the Windows
/// "GPU Engine" counters (null when this PC does not report them).
/// Used only while the System page is open, so it costs nothing the rest of the time.
/// </summary>
public sealed class SystemStats : IDisposable
{
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus s);

    private readonly object _fastLock = new();
    private readonly object _gpuLock = new();
    private long _pIdle, _pKernel, _pUser;
    private long _pRx, _pTx;
    private DateTime _pTime = DateTime.MinValue;

    private PerformanceCounter[]? _gpu;
    private DateTime _gpuListTime = DateTime.MinValue;
    private bool _gpuDead;

    private static long Ticks(FILETIME f) => ((long)(uint)f.dwHighDateTime << 32) | (uint)f.dwLowDateTime;

    /// <summary>Quick part (CPU, RAM, storage, network): a few milliseconds.</summary>
    public SysSample SampleFast()
    {
        lock (_fastLock) return SampleFastCore();
    }

    private SysSample SampleFastCore()
    {
        var r = new SysSample();
        DateTime now = DateTime.UtcNow;
        r.Primed = _pTime != DateTime.MinValue;

        // CPU
        try
        {
            if (GetSystemTimes(out FILETIME i, out FILETIME k, out FILETIME u))
            {
                long idle = Ticks(i), kernel = Ticks(k), user = Ticks(u);
                if (_pTime != DateTime.MinValue)
                {
                    long total = (kernel - _pKernel) + (user - _pUser);
                    long busy = total - (idle - _pIdle);
                    if (total > 0) r.Cpu = Math.Clamp(100.0 * busy / total, 0, 100);
                }
                _pIdle = idle; _pKernel = kernel; _pUser = user;
            }
        }
        catch { }

        // RAM
        try
        {
            var m = new MemoryStatus { dwLength = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref m))
            {
                r.RamTotalGb = m.ullTotalPhys / 1073741824.0;
                r.RamUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
                r.RamPct = m.dwMemoryLoad;
            }
        }
        catch { }

        // Storage (the drive Windows runs from)
        try
        {
            string root = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var d = new System.IO.DriveInfo(root);
            if (d.IsReady)
            {
                r.DiskName = root.TrimEnd('\\');
                r.DiskTotalGb = d.TotalSize / 1073741824.0;
                r.DiskUsedGb = (d.TotalSize - d.TotalFreeSpace) / 1073741824.0;
                r.DiskPct = r.DiskTotalGb > 0 ? 100.0 * r.DiskUsedGb / r.DiskTotalGb : 0;
            }
        }
        catch { }

        // Network speed (all active adapters together)
        try
        {
            long rx = 0, tx = 0;
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                IPv4InterfaceStatistics st = n.GetIPv4Statistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }
            if (_pTime != DateTime.MinValue)
            {
                double sec = (now - _pTime).TotalSeconds;
                if (sec > 0.2)
                {
                    r.DownBps = Math.Max(0, (rx - _pRx) / sec);
                    r.UpBps = Math.Max(0, (tx - _pTx) / sec);
                }
            }
            _pRx = rx; _pTx = tx;
        }
        catch { }

        _pTime = now;

        return r;
    }

    /// <summary>Forget the previous sample (used when the System page is opened again).</summary>
    public void Reset() { lock (_fastLock) _pTime = DateTime.MinValue; }

    /// <summary>GPU load. The first call builds the counter list (slow), so call it from a background thread.</summary>
    public double? SampleGpu()
    {
        lock (_gpuLock) return SampleGpuCore();
    }

    private double? SampleGpuCore()
    {
        if (_gpuDead) return null;
        try
        {
            if (_gpu == null || (DateTime.UtcNow - _gpuListTime).TotalSeconds > 15)
            {
                DisposeGpu();
                var cat = new PerformanceCounterCategory("GPU Engine");
                string[] names = cat.GetInstanceNames()
                    .Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var list = new List<PerformanceCounter>();
                foreach (string n in names)
                {
                    try
                    {
                        var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true);
                        pc.NextValue();
                        list.Add(pc);
                    }
                    catch { }
                }
                _gpu = list.ToArray();
                _gpuListTime = DateTime.UtcNow;
            }

            double sum = 0;
            foreach (PerformanceCounter pc in _gpu)
            {
                try { sum += pc.NextValue(); } catch { }
            }
            return _gpu.Length == 0 ? null : Math.Clamp(sum, 0, 100);
        }
        catch
        {
            _gpuDead = true; // counters not available on this PC
            return null;
        }
    }

    private void DisposeGpu()
    {
        if (_gpu == null) return;
        foreach (PerformanceCounter pc in _gpu)
        {
            try { pc.Dispose(); } catch { }
        }
        _gpu = null;
    }

    public void Dispose() => DisposeGpu();
}
