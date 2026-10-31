using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LiteIsland;

/// <summary>Starts, stops and restores the live (video) wallpaper. Only one runs at a time.</summary>
public static class WallpaperManager
{
    private static LiveWallpaperWindow? _live;

    public static bool IsLiveRunning => _live != null;

    /// <summary>Raised when the live wallpaper starts or stops.</summary>
    public static event Action? LiveChanged;

    /// <summary>Picture = normal Windows wallpaper (uses no RAM in this app). Video = live wallpaper.</summary>
    public static void Apply(string path, Action<string>? onError)
    {
        if (WallpaperService.IsImage(path))
        {
            StopLive(true);
            WallpaperService.SetStatic(path);
        }
        else if (WallpaperService.IsVideo(path))
        {
            StartLive(path, onError);
        }
        else
        {
            onError?.Invoke("This file type cannot be used as a wallpaper.");
        }
    }

    public static void StartLive(string path, Action<string>? onError)
    {
        StopLive(false);
        try
        {
            var w = new LiveWallpaperWindow(path);
            w.Failed += msg =>
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_live == w) StopLive(true);
                    onError?.Invoke(msg);
                }));
            };
            _live = w;
            w.Show();
            WallpaperService.SaveLivePath(path);
            LiveChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _live = null;
            onError?.Invoke(ex.Message);
        }
    }

    /// <param name="clearState">true = forget it (user stopped it); false = keep it so it comes back after a restart</param>
    public static void StopLive(bool clearState)
    {
        var w = _live;
        _live = null;
        if (w != null)
        {
            try { w.Shutdown(); } catch { }
        }
        if (clearState) WallpaperService.SaveLivePath(null);
        LiveChanged?.Invoke();
    }

    /// <summary>After Windows start: bring back the live wallpaper that was running.</summary>
    public static void RestoreOnStartup()
    {
        string? saved = WallpaperService.LoadLivePath();
        if (string.IsNullOrEmpty(saved) || !System.IO.File.Exists(saved)) return;
        string path = saved;

        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) }; // let the desktop finish loading
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_live == null) StartLive(path, null);
        };
        t.Start();
    }
}

/// <summary>
/// A borderless video window that sits between the desktop wallpaper and the desktop icons.
/// The video loops without a gap, is muted, and pauses while a maximized / full-screen window covers the desktop.
/// </summary>
public sealed class LiveWallpaperWindow : Window
{
    private readonly MediaElement _me = new();
    private readonly MediaClock _clock;
    private DispatcherTimer? _watch;
    private IntPtr _hwnd;
    private bool _paused;

    public event Action<string>? Failed;

    public LiveWallpaperWindow(string path)
    {
        Title = "LiteIsland Live Wallpaper";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.Black;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        _me.Stretch = Stretch.UniformToFill;
        _me.IsMuted = true;
        _me.Volume = 0;
        _me.MediaFailed += (_, e) =>
            Failed?.Invoke(e.ErrorException?.Message ?? "This video cannot be played on this PC.");

        // A repeating timeline loops with no flicker (MediaEnded + restart would blink)
        var timeline = new MediaTimeline(new Uri(path, UriKind.Absolute)) { RepeatBehavior = RepeatBehavior.Forever };
        _clock = (MediaClock)timeline.CreateClock(true);
        _me.Clock = _clock;
        Content = _me;

        Loaded += (_, _) =>
        {
            _clock.Controller?.Begin();
            DesktopHost.Layout(_hwnd);
            StartWatch();
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        if (!DesktopHost.Attach(_hwnd))
            Failed?.Invoke("Could not attach to the Windows desktop.");
    }

    private void StartWatch()
    {
        _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _watch.Tick += (_, _) => SetPaused(DesktopHost.IsDesktopCovered(_hwnd));
        _watch.Start();
    }

    private void SetPaused(bool pause)
    {
        if (pause == _paused) return;
        _paused = pause;
        try
        {
            if (pause) _clock.Controller?.Pause();
            else _clock.Controller?.Resume();
        }
        catch { }
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => DesktopHost.Layout(_hwnd)));
    }

    public void Shutdown()
    {
        _watch?.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        try { _clock.Controller?.Stop(); } catch { }
        _me.Clock = null;
        Content = null;
        Close();
        DesktopHost.RefreshWallpaper();
    }
}

/// <summary>Windows desktop tricks: put a window behind the icons, find out if the desktop is covered.</summary>
internal static class DesktopHost
{
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref POINT pts, uint count);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SystemParametersInfo(int action, int param, string? value, int flags);

    private static bool _underProgman; // Windows 11 24H2 and newer keep the wallpaper layer inside Progman

    public static bool Attach(IntPtr hwnd)
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return false;

        // Ask Explorer to create the WorkerW window that sits behind the icons
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        IntPtr workerw = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                workerw = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            return true;
        }, IntPtr.Zero);

        _underProgman = workerw == IntPtr.Zero;
        IntPtr parent = _underProgman ? progman : workerw;

        // popup window -> child window, never takes focus, not in Alt+Tab
        int style = GetWindowLong(hwnd, -16);
        style = (style & ~unchecked((int)0x80000000)) | 0x40000000;
        SetWindowLong(hwnd, -16, style);
        int ex = GetWindowLong(hwnd, -20);
        SetWindowLong(hwnd, -20, ex | 0x08000000 | 0x00000080);

        SetParent(hwnd, parent);
        Layout(hwnd);
        return true;
    }

    /// <summary>Covers the primary screen (in real pixels).</summary>
    public static void Layout(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        IntPtr parent = GetParent(hwnd);
        if (parent == IntPtr.Zero) return;

        // The main screen always starts at 0,0 (real pixels)
        int sx = 0, sy = 0;
        int w = GetSystemMetrics(0); // SM_CXSCREEN
        int h = GetSystemMetrics(1); // SM_CYSCREEN
        if (w <= 0 || h <= 0) { w = 1920; h = 1080; }

        var pt = new POINT { X = sx, Y = sy };
        MapWindowPoints(IntPtr.Zero, parent, ref pt, 1);

        const uint NoActivate = 0x0010, ShowWindow = 0x0040, NoZOrder = 0x0004, NoSize = 0x0001, NoMove = 0x0002;
        if (_underProgman)
        {
            // lowest layer inside Progman, but above the wallpaper layer, below the icons
            SetWindowPos(hwnd, new IntPtr(1), pt.X, pt.Y, w, h, NoActivate | ShowWindow);
            IntPtr wk = FindWindowEx(parent, IntPtr.Zero, "WorkerW", null);
            if (wk != IntPtr.Zero) SetWindowPos(wk, new IntPtr(1), 0, 0, 0, 0, NoSize | NoMove | NoActivate);
        }
        else
        {
            SetWindowPos(hwnd, IntPtr.Zero, pt.X, pt.Y, w, h, NoActivate | ShowWindow | NoZOrder);
        }
    }

    /// <summary>True when a maximized or full-screen window hides the desktop (video can pause).</summary>
    public static bool IsDesktopCovered(IntPtr self)
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == self || !IsWindowVisible(fg) || IsIconic(fg)) return false;

            var sb = new StringBuilder(64);
            GetClassName(fg, sb, sb.Capacity);
            string cls = sb.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;

            if (!GetWindowRect(fg, out RECT r)) return false;
            var b = new RECT { Left = 0, Top = 0, Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) };

            // window must be on the primary screen
            int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            if (cx < b.Left || cx > b.Right || cy < b.Top || cy > b.Bottom) return false;

            if (IsZoomed(fg)) return true;
            return r.Left <= b.Left + 8 && r.Top <= b.Top + 8 && r.Right >= b.Right - 8 && r.Bottom >= b.Bottom - 8;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Makes Windows repaint the normal wallpaper (after the live one is closed).</summary>
    public static void RefreshWallpaper()
    {
        try { SystemParametersInfo(0x0014, 0, null, 0x01); } catch { }
    }
}
