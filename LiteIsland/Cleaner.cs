using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace LiteIsland;

/// <summary>
/// Clears temporary files and caches directly from code: no cmd window, no Run dialog, no PowerShell.
/// Files that are in use (or that need administrator rights) are skipped silently.
/// Cleans: user Temp, Windows Temp, Prefetch, browser/system INetCache, crash dumps,
/// error reports, Recycle Bin and the DNS cache.
/// </summary>
public static class Cleaner
{
    public sealed record Result(long Bytes, int Files);

    public static Task<Result> RunAsync() => Task.Run(Run);

    private static Result Run()
    {
        long bytes = 0;
        int files = 0;

        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string[] folders =
        {
            Path.GetTempPath(),                                      // %temp%
            Path.Combine(win, "Temp"),                               // C:\Windows\Temp   (admin)
            Path.Combine(win, "Prefetch"),                           // prefetch          (admin)
            Path.Combine(local, "Microsoft", "Windows", "INetCache"),
            Path.Combine(local, "CrashDumps"),
            Path.Combine(local, "Microsoft", "Windows", "WER"),      // error reports
        };

        foreach (string f in folders)
        {
            try { CleanFolder(f, ref bytes, ref files); }
            catch { /* skip */ }
        }

        bytes += EmptyRecycleBin();
        FlushDns();

        return new Result(bytes, files);
    }

    private static void CleanFolder(string path, ref long bytes, ref int files)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint // never follow links out of the folder
        };

        // Files first
        foreach (string file in Directory.EnumerateFiles(path, "*", opts))
        {
            try
            {
                var fi = new FileInfo(file);
                long len = fi.Length;
                if (fi.IsReadOnly) fi.IsReadOnly = false;
                fi.Delete();
                bytes += len;
                files++;
            }
            catch { /* in use / no permission: skip */ }
        }

        // Then the empty folders (deepest first)
        string[] dirs;
        try { dirs = Directory.GetDirectories(path, "*", opts); }
        catch { return; }
        Array.Sort(dirs, (a, b) => b.Length.CompareTo(a.Length));
        foreach (string d in dirs)
        {
            try { Directory.Delete(d, false); } // only works when empty
            catch { }
        }
    }

    // ---------- Recycle Bin (no dialog, no sound) ----------

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    private static long EmptyRecycleBin()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            long size = SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
            if (SHEmptyRecycleBin(IntPtr.Zero, null,
                    SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND) == 0)
                return size;
        }
        catch { }
        return 0;
    }

    // ---------- DNS cache (same as "ipconfig /flushdns", without cmd) ----------

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static extern bool DnsFlushResolverCache();

    private static void FlushDns()
    {
        try { DnsFlushResolverCache(); } catch { }
    }
}
