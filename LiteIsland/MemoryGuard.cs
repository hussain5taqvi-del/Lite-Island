using System;
using System.Runtime;
using System.Runtime.InteropServices;

namespace LiteIsland;

/// <summary>
/// Hard RAM limit for the island process (default 30 MB).
/// Windows itself enforces it: the working set of LiteIsland can never go above the limit
/// (pages above it are pushed out of RAM). Heavy work (ffmpeg, yt-dlp, OCR, background removal)
/// always runs in separate short-lived processes, so it never counts against this limit.
/// </summary>
internal static class MemoryGuard
{
    public static int LimitMb { get; private set; } = 30;

    public static void Apply(int limitMb)
    {
        LimitMb = Math.Clamp(limitMb, 20, 400);
        try
        {
            ulong max = (ulong)LimitMb * 1024UL * 1024UL;
            // 0x2 = QUOTA_LIMITS_HARDWS_MIN_DISABLE, 0x4 = QUOTA_LIMITS_HARDWS_MAX_ENABLE (hard maximum)
            SetProcessWorkingSetSizeEx(GetCurrentProcess(), new UIntPtr(256UL * 1024UL), new UIntPtr(max), 0x2 | 0x4);
        }
        catch
        {
            // not fatal: the periodic trim below still keeps the memory low
        }
    }

    /// <summary>
    /// While something animates the working set gets some headroom (a hard cap makes Windows page the animation's own
    /// memory out and in again = stutter). The cap is put back by Apply() a few seconds later, when the island rests.
    /// </summary>
    public static void Burst()
    {
        try
        {
            if (LimitMb >= 96) return;
            ulong max = (ulong)(LimitMb + 40) * 1024UL * 1024UL;
            SetProcessWorkingSetSizeEx(GetCurrentProcess(), new UIntPtr(256UL * 1024UL), new UIntPtr(max), 0x2 | 0x4);
        }
        catch { }
    }

    /// <summary>Collects garbage and gives the memory back to Windows.</summary>
    public static void Trim()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            EmptyWorkingSet(GetCurrentProcess());
        }
        catch
        {
        }
    }

    public static double WorkingSetMb()
    {
        try { return Environment.WorkingSet / 1048576.0; }
        catch { return 0; }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr process, UIntPtr min, UIntPtr max, uint flags);

    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);
}
