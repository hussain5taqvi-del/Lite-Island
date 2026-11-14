using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace LiteIsland;

/// <summary>
/// Live wallpaper helper (LiteIsland.exe --wallpaper video pauseCovered pauseBattery). No WPF in this process.
/// It puts a plain Win32 window behind the desktop icons and lets mpv (hardware decoding) draw the video loop into it.
/// While the desktop is hidden (a window covers it, a game runs, battery) the video is paused: 0 CPU/GPU.
/// After a while hidden, mpv is closed completely to free its memory; it starts again when the desktop is visible.
/// Talks to the island over stdin/stdout: in = QUIT, out = S|status, E|error.
/// </summary>
internal static class WallpaperWorker
{
    // ---------- Win32 ----------
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SPS { public byte AC, Flag, Pct, Res; public int Life, Full; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr H; public uint M; public IntPtr W, L; public uint T; public POINT P; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? c, string? t);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string? c, string? t);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWL(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWL(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr c, IntPtr p);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int hh, uint f);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint a, uint b, IntPtr c, uint d);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG m, IntPtr h, uint a, uint b, uint r);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjects(uint n, IntPtr[]? h, bool all, uint ms, uint mask);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint ex, string cls, string? name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr par);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int s);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int s);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SPS s);

    private static readonly object OutLock = new();
    private static void Say(string kind, string val)
    {
        lock (OutLock) { try { Console.Out.WriteLine(kind + "|" + val.Replace('\r', ' ').Replace('\n', ' ')); Console.Out.Flush(); } catch { } }
    }

    private static volatile bool _quit;
    private static IntPtr _host;
    private static Process? _mpv;
    private static string _video = "";
    private static string _pipe = "";

    public static int Run(string[] args)
    {
        // args: --wallpaper video pauseCovered pauseBattery
        if (args.Length < 2) return 2;
        _video = args[1];
        bool pauseCovered = args.Length < 3 || args[2] != "0";
        bool pauseBattery = args.Length < 4 || args[3] != "0";
        try { SetProcessDPIAware(); } catch { }
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
        try { using var me = Process.GetCurrentProcess(); me.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        // stdin: QUIT, or the island died (end of input) -> stop
        new Thread(() =>
        {
            try
            {
                string? l;
                while ((l = Console.In.ReadLine()) != null) if (l.Trim() == "QUIT") break;
            }
            catch { }
            _quit = true;
        }) { IsBackground = true, Name = "WpIn" }.Start();

        MemoryGuard.Lean(40, 10);
        _pipe = "liteisland-wp-" + Environment.ProcessId;

        if (!File.Exists(_video)) { Say("E", "Video file was not found"); return 3; }
        if (!File.Exists(Tools.Mpv))
        {
            Say("S", "Setting up wallpaper engine...");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                Tools.EnsureMpvAsync(s => Say("S", s), cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex) { Say("E", "Could not get the wallpaper engine: " + ex.Message); return 4; }
            if (!File.Exists(Tools.Mpv)) { Say("E", "Wallpaper engine is missing"); return 4; }
        }

        if (!CreateHost()) { Say("E", "Could not attach to the desktop"); return 5; }

        int fails = 0;
        DateTime started = DateTime.MinValue, hiddenSince = DateTime.MinValue;
        bool paused = false, wasHidden = false;
        string lastStatus = "";
        void Status(string s) { if (s != lastStatus) { lastStatus = s; Say("S", s); } }

        while (!_quit)
        {
            PumpMessages(1000);
            if (_quit) break;
            TrimMpv();

            if (!IsWindow(GetParent(_host))) Embed();   // explorer restarted: attach again

            bool covered = pauseCovered && IsCovered();
            bool battery = pauseBattery && OnBattery();
            bool hidden = covered || battery;
            bool alive = _mpv is { HasExited: false };

            if (_mpv != null && _mpv.HasExited)
            {
                if ((DateTime.UtcNow - started).TotalSeconds < 6) fails++; else fails = 0;
                _mpv = null; alive = false; paused = false;
            }
            if (fails >= 3) { Say("E", "The video could not be played (unsupported file or graphics driver)"); fails = 0; _quit = true; break; }

            if (hidden)
            {
                if (!wasHidden) hiddenSince = DateTime.UtcNow;
                if (alive && !paused) { SetPause(true); paused = true; }
                if (alive && (DateTime.UtcNow - hiddenSince).TotalSeconds > 120) { KillMpv(); alive = false; paused = false; }
                Status(battery ? "Paused (battery)" : "Paused (desktop is covered)");
            }
            else
            {
                if (!alive) { StartMpv(); started = DateTime.UtcNow; paused = false; }
                else if (paused) { SetPause(false); paused = false; }
                Status("Playing");
            }
            wasHidden = hidden;
        }

        KillMpv();
        try { if (_host != IntPtr.Zero) DestroyWindow(_host); } catch { }
        try { SystemParametersInfo(0x14, 0, IntPtr.Zero, 0); } catch { } // paint the normal wallpaper again
        Say("S", "Stopped");
        return 0;
    }

    private static void PumpMessages(uint waitMs)
    {
        MsgWaitForMultipleObjects(0, null, false, waitMs, 0x04FF);
        while (PeekMessage(out MSG m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); }
    }

    // ---------- desktop host window ----------
    private static bool CreateHost()
    {
        // "Static" is a ready-made window class: no window procedure of our own is needed
        _host = CreateWindowEx(0x08000080 /*NOACTIVATE|TOOLWINDOW*/, "Static", "LiteIslandWallpaper", 0x80000000 /*POPUP*/, 0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return _host != IntPtr.Zero && Embed();
    }

    private static IntPtr FindWorker()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;
        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0, 1000, out _);

        IntPtr worker = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                IntPtr w = FindWindowEx(IntPtr.Zero, h, "WorkerW", null);
                if (w != IntPtr.Zero) { worker = w; return false; }
            }
            return true;
        }, IntPtr.Zero);
        if (worker != IntPtr.Zero) return worker;

        // Windows 11 24H2: the WorkerW lives inside Progman
        worker = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        return worker != IntPtr.Zero ? worker : progman;
    }

    private static bool Embed()
    {
        IntPtr parent = FindWorker();
        if (parent == IntPtr.Zero) return false;
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);   // primary monitor
        long style = GetWL(_host, -16).ToInt64();
        style = (style & ~0x80000000L) | 0x40000000L | 0x10000000L; // child + visible
        SetWL(_host, -16, new IntPtr(style));
        SetParent(_host, parent);
        var pt = new POINT { X = 0, Y = 0 };
        ScreenToClient(parent, ref pt);
        // HWND_BOTTOM so that the icons (a sibling in the Progman case) stay on top
        SetWindowPos(_host, new IntPtr(1), pt.X, pt.Y, w, h, 0x0010 | 0x0040);
        return true;
    }

    // ---------- mpv ----------
    private static void StartMpv()
    {
        try
        {
            var psi = new ProcessStartInfo(Tools.Mpv) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Tools.Mpv)! };
            string[] a =
            {
                "--no-config", "--no-terminal", "--aid=no", "--sid=no", "--sub-auto=no", "--audio-file-auto=no", "--cache-pause=no", "--keep-open=no", "--stop-screensaver=no", "--no-input-vo-keyboard", "--media-controls=no", "--msg-level=all=no", "--no-audio", "--no-osc", "--no-osd-bar", "--osd-level=0",
                "--no-input-default-bindings", "--no-input-cursor", "--no-border", "--idle=no", "--loop-file=inf",
                "--panscan=1.0", "--hwdec=auto-safe", "--vo=gpu", "--gpu-api=d3d11", "--gpu-context=d3d11",
                "--scale=spline36", "--cscale=bilinear", "--dscale=mitchell", "--correct-downscaling=yes", "--interpolation=no", "--deband=no", "--dither-depth=no",
                "--cache=no", "--demuxer-max-bytes=4MiB", "--demuxer-max-back-bytes=0", "--demuxer-readahead-secs=1", "--swapchain-depth=1", "--gpu-shader-cache=no", "--icc-cache=no", "--no-audio-display", "--vd-lavc-dr=yes", "--hr-seek=no",
                "--vd-lavc-threads=1", "--ytdl=no", "--load-scripts=no", "--load-osd-console=no", "--load-stats-overlay=no",
                "--screenshot-directory=nul", "--wid=" + _host.ToInt64(),
                "--input-ipc-server=\\\\.\\pipe\\" + _pipe
            };
            foreach (string s in a) psi.ArgumentList.Add(s);
            psi.ArgumentList.Add(_video);
            var p = Process.Start(psi);
            if (p == null) return;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            _mpv = p;
        }
        catch (Exception ex) { Say("E", "Could not start the player: " + ex.Message); }
    }

    private static long _lastTrim;

    /// <summary>The player's pages that are not needed this second go back to Windows (keeps the engine near 100 MB).</summary>
    private static void TrimMpv()
    {
        long now = Environment.TickCount64;
        if (now - _lastTrim < 8000) return;
        _lastTrim = now;
        try { if (_mpv is { HasExited: false }) EmptyWorkingSet(_mpv.Handle); } catch { }
    }

    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);

    private static void KillMpv()
    {
        try { if (_mpv is { HasExited: false }) { _mpv.Kill(true); _mpv.WaitForExit(1500); } } catch { }
        _mpv = null;
    }

    private static void SetPause(bool on)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", _pipe, PipeDirection.Out);
            c.Connect(300);
            byte[] b = Encoding.UTF8.GetBytes("{\"command\":[\"set_property\",\"pause\"," + (on ? "true" : "false") + "]}\n");
            c.Write(b, 0, b.Length);
            c.Flush();
        }
        catch { }
    }

    // ---------- is the desktop visible? ----------
    private static bool OnBattery()
    {
        try { return GetSystemPowerStatus(out SPS s) && s.AC == 0; } catch { return false; }
    }

    private static readonly HashSet<string> SkipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "SHELLDLL_DefView", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow"
    };

    private static bool IsCovered()
    {
        try
        {
            if (SHQueryUserNotificationState(out int q) == 0 && (q == 2 || q == 3 || q == 4)) return true;
        }
        catch { }

        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        if (sw <= 0 || sh <= 0) return false;
        double screen = (double)sw * sh;
        uint me = (uint)Environment.ProcessId;
        bool found = false;
        var sb = new StringBuilder(64);
        try
        {
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || IsIconic(h) || GetWindowTextLength(h) == 0) return true;
                long ex = GetWL(h, -20).ToInt64();
                if ((ex & 0x80) != 0 || (ex & 0x20) != 0) return true;           // tool window, click-through overlay
                if (DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == me) return true;
                sb.Clear();
                GetClassName(h, sb, 64);
                if (SkipClasses.Contains(sb.ToString())) return true;
                if (!GetWindowRect(h, out RECT r)) return true;
                long iw = Math.Max(0, Math.Min(r.R, sw) - Math.Max(r.L, 0));
                long ih = Math.Max(0, Math.Min(r.B, sh) - Math.Max(r.T, 0));
                if (iw * ih >= screen * 0.9) { found = true; return false; }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }
}
