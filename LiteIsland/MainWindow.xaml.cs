using System;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LiteIsland;

public partial class MainWindow : Window
{
    private static readonly Brush Placeholder = MakeFrozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)));

    private readonly AppSettings _s = AppSettings.Load();
    private readonly MediaService _media = new();
    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _trimTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly AudioMeter _meter = new();
    private readonly DispatcherTimer _barTimer = new() { Interval = TimeSpan.FromMilliseconds(55) };
    private readonly double[] _barHist = new double[4]; // recent loudness, gives the bars a wave look
    private double _barLevel;
    private int _animToken, _previewToken;
    private long _holdUntil; // the island stays open until this time (Environment.TickCount64)

    private SettingsWindow? _settingsWindow;
    private WallpaperWindow? _wallpaperWindow;
    private TrayIcon? _tray;
    private byte[]? _lastArt;
    private double _duration;
    private bool _barsRunning, _expanded, _playing, _seeking, _holdOpen;

    // Animated corner roundness of the bottom corners (lets WPF animate a CornerRadius)
    public static readonly DependencyProperty BottomRadiusProperty =
        DependencyProperty.Register(nameof(BottomRadius), typeof(double), typeof(MainWindow),
            new PropertyMetadata(22.0, (d, _) => ((MainWindow)d).ApplyCorners()));

    public double BottomRadius
    {
        get => (double)GetValue(BottomRadiusProperty);
        set => SetValue(BottomRadiusProperty, value);
    }

    public MainWindow()
    {
        InitializeComponent();

        // Apply saved settings
        _s.Clamp();
        Island.Width = _s.CollapsedWidth;
        Island.Height = _s.CollapsedHeight;
        BottomRadius = CollapsedR;
        ApplyShape();
        ApplyCorners();
        ApplyBackground();
        ApplyTextColor();
        ApplyWindowSize();
        InitViews();
        InitPower();
        ApplyEars();
        ApplyTheme();
        ApplyGlass(false);
        InitClock();
        InitHud();
        InitExtras();
        InitTasks();
        ApplyCompactLayout();
        Island.SizeChanged += (_, _) => ApplyCorners();
        SeekHost.SizeChanged += (_, _) => { if (_expanded && !_seeking) RefreshProgress(); };

        // Tray icon: click = Settings, right-click = menu
        _tray = new TrayIcon(OpenSettings, OpenWallpapers, () => Application.Current.Shutdown());

        // Media events arrive on a background thread: hand them to the UI thread
        _media.Changed += (title, artist, playing, art) =>
            Dispatcher.InvokeAsync(() => { UpdateUi(title, artist, playing, art); });

        _media.ModesChanged += () => Dispatcher.InvokeAsync(UpdateModes);

        _media.TimelineChanged += () =>
            Dispatcher.InvokeAsync(() =>
            {
                if (_expanded) RefreshProgress();
            });

        // After the cursor leaves, the island closes by itself (small delay avoids flicker)
        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            if (Island.IsMouseOver || _holdOpen || _seeking) return;

            // Just opened an app from the island: wait a moment before closing
            long wait = _holdUntil - Environment.TickCount64;
            if (wait > 0)
            {
                _leaveTimer.Interval = TimeSpan.FromMilliseconds(wait + 50);
                _leaveTimer.Start();
                return;
            }
            Animate(false);
        };

        // Memory is trimmed only after the island has been closed for a while
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer.Stop();
            if (!_expanded) { MemoryGuard.Apply(MemoryGuard.LimitMb); TrimMemory(); }
        };

        // Equalizer: follows the real loudness of the music (runs only while playing and collapsed)
        _barTimer.Tick += (_, _) => BarTick();

        // Seek bar progress (runs only while expanded and playing)
        _progressTimer.Tick += (_, _) => RefreshProgress();

        SizeChanged += (_, _) => CenterOnScreen();

        Loaded += async (_, _) =>
        {
            CenterOnScreen();

            // Expanded content is drawn once into a bitmap while it is animated (sharp: cached at the screen DPI)
            try { _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX; }
            catch { }

            // Startup animation: slides down from above
            IslandShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-44, 0, TimeSpan.FromMilliseconds(620))
                {
                    EasingFunction = new SpringEase { Damping = 0.9 }
                });

            await _media.InitAsync();

            await Task.Delay(4000);
            TrimMemory();
        };
    }

    // ---------- Size, corners, settings ----------

    // Animation time, scaled by the "Animation speed" setting
    private TimeSpan Dur(double ms) => TimeSpan.FromMilliseconds(ms / Math.Max(0.1, _s.AnimSpeed));

    private void ApplyBackground()
    {
        Color c = Colors.Black;
        try { c = (Color)ColorConverter.ConvertFromString(_s.IslandColor); } catch { }
        if (_s.Glass)
        {
            // Liquid Glass: see-through fill, a little lighter at the top than at the bottom
            // Clear glass: a little lighter and more see-through at the top, deeper at the bottom (thickness)
            double a0 = Math.Clamp(_s.GlassOpacity, 10, 100) / 100.0;
            byte top = (byte)Math.Round(255 * a0 * 0.85);
            byte bottom = (byte)Math.Round(255 * Math.Min(1.0, a0 * 1.2));
            byte tr = (byte)(c.R + (255 - c.R) * 0.06), tg = (byte)(c.G + (255 - c.G) * 0.06), tb = (byte)(c.B + (255 - c.B) * 0.06);
            var glass = new LinearGradientBrush(Color.FromArgb(top, tr, tg, tb),
                                                Color.FromArgb(bottom, c.R, c.G, c.B), 90);
            glass.Freeze();
            Island.Background = glass;

            var ear = new SolidColorBrush(Color.FromArgb(top, tr, tg, tb));
            ear.Freeze();
            EarL.Fill = ear;
            EarR.Fill = ear;
            return;
        }

        byte a = (byte)Math.Round(255 * Math.Clamp(_s.BgOpacity, 20, 100) / 100.0);
        var brush = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        brush.Freeze();
        Island.Background = brush;
        EarL.Fill = brush;
        EarR.Fill = brush;
    }

    // Text color: the island's texts use two brushes that are swapped here
    private void ApplyTextColor()
    {
        Color c = Colors.White;
        try { c = (Color)ColorConverter.ConvertFromString(_s.TextColor); } catch { }

        var main = new SolidColorBrush(c);
        main.Freeze();
        var dim = new SolidColorBrush(Color.FromArgb(0x99, c.R, c.G, c.B));
        dim.Freeze();

        Resources["TextBrush"] = main;
        Resources["TextDimBrush"] = dim;
    }

    // The window is only as big as it needs to be: small while the island is collapsed (Windows has to
    // redraw the whole transparent window on every animation frame, so a small window = smoother and lighter),
    // big while it is open or animating open.
    private double _dpi = 1.0;

    private void SetExpandedCache(bool on)
    {
        try
        {
            if (on) { if (Expanded.CacheMode == null) Expanded.CacheMode = new BitmapCache(_dpi) { SnapsToDevicePixels = true }; }
            else Expanded.CacheMode = null;
        }
        catch { }
    }

    private void ApplyWindowSize()
    {
        // The window always has the full size (big enough for the open island). It is transparent, so nothing
        // changes visually, but it is NEVER resized while the island opens or closes. Resizing a transparent
        // window in the middle of an animation was the cause of cut-off / flickering frames.
        double gap = TopGapPx;
        double w = Math.Max(_s.ExpandedWidth + 40, Math.Max(_s.CollapsedWidth, HudMinWidth) + 70);
        double h = Math.Max(_s.ExpandedHeight + 30, _s.CollapsedHeight + 34) + gap;
        if (NotchHost != null) NotchHost.Margin = new Thickness(0, IsIsland ? gap : -1, 0, 0);

        if (Math.Abs(Width - w) > 0.5) Width = w;
        if (Math.Abs(Height - h) > 0.5) Height = h;

        CenterOnScreen();
        ApplyContentSize();
    }

    // Puts the window exactly in the middle of the primary screen, top edge at the top of the screen.
    // Done in real screen pixels (not in WPF units), so Windows display scaling (125% / 150% / 4K) can never
    // push the island to one side. Runs again when the window is created, loaded, or the display changes.
    private void CenterOnScreen()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                IntPtr mon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1 /* MONITOR_DEFAULTTOPRIMARY */);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi) && GetWindowRect(hwnd, out RECT wr))
                {
                    int ww = wr.Right - wr.Left;
                    int x = mi.rcMonitor.Left + ((mi.rcMonitor.Right - mi.rcMonitor.Left) - ww) / 2;
                    int y = mi.rcMonitor.Top;
                    if (x != wr.Left || y != wr.Top)
                        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
                    return;
                }
            }
        }
        catch
        {
            // fall through to the simple method below
        }

        // Before the window exists (or if Windows refuses): plain WPF-unit math
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.InvokeAsync(CenterOnScreen);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() => { ApplyWindowSize(); CenterOnScreen(); ApplyReserve(); });

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // Content has a fixed size, so it is NOT measured again on every animation frame while the island
    // grows or shrinks (the island just reveals it). This is what makes the animation smooth.
    private void ApplyContentSize()
    {
        if (Expanded == null) return;
        double ew = Math.Max(120, _s.ExpandedWidth - 44);
        double eh = Math.Max(80, _s.ExpandedHeight - 26);
        Expanded.Width = ew;
        Expanded.Height = eh;

        // The pages are laid out at a design size of 396 x 174 and scaled to the island: a smaller island
        // gets a smaller album cover, smaller text and buttons (and a bigger one gets bigger ones)
        double sc = Math.Clamp(Math.Min(ew / 396.0, eh / 174.0), 0.7, 1.4);
        ExpandedInner.Width = ew / sc;
        ExpandedInner.Height = eh / sc;
        TitleBox.MaxWidth = Math.Max(80, ExpandedInner.Width - 54 - 14 - 12);
        Compact.Width = _s.CollapsedWidth;
    }

    private void ApplyCorners()
    {
        if (Island == null) return;
        double b = Math.Max(0, BottomRadius);
        double ih = Island.ActualHeight, iw = Island.ActualWidth;
        // A corner can never be rounder than the island itself allows (keeps every size / radius combination clean)
        if (ih > 1 && iw > 1) b = Math.Min(b, IsIsland ? Math.Min(ih, iw) / 2 : Math.Min(ih * 0.92, iw / 2));
        double t = IsIsland ? b : 0; // notch: flat top. island: a full pill
        Island.CornerRadius = new CornerRadius(t, t, b, b);
    }

    // The two small curves on each side of the top edge (the "notch ears")
    private void ApplyEars()
    {
        if (EarL == null) return;

        double r = Math.Clamp(_s.TopRadius, 0, 24);
        // The ears sit on the straight part of the island side, so they can not be taller than that
        double bEff = Math.Min(CollapsedR, _s.CollapsedHeight * 0.92);
        r = Math.Min(r, Math.Max(0, _s.CollapsedHeight - bEff - 1));
        bool on = r >= 1 && !IsIsland;
        EarL.Visibility = EarR.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        var gv = Visibility.Collapsed; // the ears are plain extensions of the island (rims made them look like separate pieces)
        EarLGloss.Visibility = EarRGloss.Visibility = EarLRim.Visibility = EarRRim.Visibility = gv;
        if (!on) return;

        string f = r.ToString("0.##", CultureInfo.InvariantCulture);
        EarL.Width = EarL.Height = EarR.Width = EarR.Height = r;
        // Curve with long handles (continuous, "squircle" like) instead of a plain circle arc
        string a = (r * 0.38).ToString("0.###", CultureInfo.InvariantCulture); // f * (1 - 0.62)
        string b = (r * 0.62).ToString("0.###", CultureInfo.InvariantCulture);
        EarL.Data = Geometry.Parse($"M0,0 L{f},0 L{f},{f} C{f},{a} {b},0 0,0 Z");
        EarR.Data = Geometry.Parse($"M{f},0 L0,0 L0,{f} C0,{a} {a},0 {f},0 Z");
        EarL.Margin = new Thickness(-r + 0.5, 0, 0, 0); // overlaps the island by half a pixel (no seam)
        EarR.Margin = new Thickness(0, 0, -r + 0.5, 0);
        foreach (var p in new[] { EarLGloss, EarLRim }) { p.Width = p.Height = r; p.Margin = EarL.Margin; }
        foreach (var p in new[] { EarRGloss, EarRRim }) { p.Width = p.Height = r; p.Margin = EarR.Margin; }
        EarLGloss.Data = EarL.Data;
        EarRGloss.Data = EarR.Data;
        EarLRim.Data = Geometry.Parse($"M{f},{f} C{f},{a} {b},0 0,0");
        EarRRim.Data = Geometry.Parse($"M0,{f} C0,{a} {a},0 {f},0");
    }

    /// <summary>Called by the settings window. previewExpanded = true shows the big island.</summary>
    public void ApplySettings(bool previewExpanded)
    {
        _s.Clamp();
        ApplyWindowSize();
        ApplyShape();
        ApplyCorners();
        ApplyEars();
        ApplyCompactLayout();
        ApplyBackground();
        ApplyTextColor();
        ApplyTheme();
        ApplyGlass(true);
        ApplySnap();
        ApplyMilo();
        ApplyWallpaper();
        ApplyReserve();
        ApplyExtraSettings();
        _previewToken++; // cancels a running animation preview
        _holdOpen = previewExpanded;

        // Dragging a slider moves the island 1:1 with the cursor (no spring on every tick = no stutter).
        // Only a change between small and big plays the normal animation.
        bool settled = Environment.TickCount64 - _lastAnimTick > 900;
        if (_expanded == previewExpanded && settled && !_hudActive) SnapIsland(previewExpanded);
        else Animate(previewExpanded);
    }

    private void OpenWallpapers()
    {
        if (_wallpaperWindow != null)
        {
            _wallpaperWindow.Activate();
            return;
        }

        WallpaperManager.StaticApplied = () => Dispatcher.InvokeAsync(() =>
        {
            if (!_s.WallpaperOn) return;
            _s.WallpaperOn = false; _s.Save();
            ApplyWallpaper();
        });
        WallpaperWindow.LiveChosen = p => Dispatcher.InvokeAsync(() =>
        {
            _s.WallpaperPath = p; _s.WallpaperOn = true; _s.Save();
            ApplyWallpaper();
        });
        _wallpaperWindow = new WallpaperWindow();
        _wallpaperWindow.Closed += (_, _) =>
        {
            _wallpaperWindow = null;
            TrimMemory(); // thumbnails are gone with the window: give the RAM back
        };
        _wallpaperWindow.Show();
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(this, _s);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            _s.Save();
            _holdOpen = false;
            if (!Island.IsMouseOver) Animate(false);
            _trimTimer.Stop();
            _trimTimer.Start(); // settings are gone: give the RAM back
        };
        _settingsWindow.Show();
    }


    // ---------- Open on click, close when the cursor leaves (0% CPU when idle) ----------

    // Moving the cursor over the island does nothing, so it never opens by accident
    private void Island_MouseEnter(object sender, MouseEventArgs e) => _leaveTimer.Stop();

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_holdOpen || _seeking) return;
        _leaveTimer.Stop();
        _leaveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _leaveTimer.Start();
    }

    // Only a click opens the island. (While it is collapsed the buttons inside are not clickable.)
    private void Island_Click(object sender, MouseButtonEventArgs e)
    {
        if (_expanded) return;
        if (TryRevealToast()) { e.Handled = true; return; } // "Done - click to show"
        _leaveTimer.Stop();
        Animate(true);
        e.Handled = true;
    }

    // Small helper: a DoubleAnimation whose time follows the "Animation speed" setting
    private DoubleAnimation Da(double to, double ms, IEasingFunction ease, double delayMs = 0)
    {
        var a = new DoubleAnimation(to, Dur(ms)) { EasingFunction = ease };
        if (delayMs > 0) a.BeginTime = Dur(delayMs);
        return a;
    }

    private static CubicEase CubicOut() => new() { EasingMode = EasingMode.EaseOut };
    private static CubicEase CubicInOut() => new() { EasingMode = EasingMode.EaseInOut };
    private static QuarticEase QuarticOut() => new() { EasingMode = EasingMode.EaseOut };
    private static QuinticEase QuinticOut() => new() { EasingMode = EasingMode.EaseOut };

    private long _lastAnimTick;

    // ---------- Size springs ----------

    private readonly Spring _sw = new(0), _sh = new(0), _sr = new(0);
    private FrameLoop? _sizeLoop;
    private Action? _sizeDone;
    private double _sizeWDelay, _sizeHDelay;

    private void StopSizeSprings()
    {
        _sizeLoop?.Stop();
        _sizeDone = null;
    }

    private void StartSizeSprings(double tw, double th, double tr, double kw, double rw, double kh, double rh,
                                  double wDelayMs, double hDelayMs, Action? done)
    {
        // start from what is on the screen right now (also when a notification bar was animating the size)
        double w0 = Island.ActualWidth > 1 ? Island.ActualWidth : Island.Width;
        double h0 = Island.ActualHeight > 1 ? Island.ActualHeight : Island.Height;
        double r0 = BottomRadius;
        bool moving = _sizeLoop is { Running: true };
        Island.BeginAnimation(WidthProperty, null);
        Island.BeginAnimation(HeightProperty, null);
        BeginAnimation(BottomRadiusProperty, null);
        if (!moving) { _sw.Snap(w0); _sh.Snap(h0); _sr.Snap(r0); }
        _sw.Tune(kw, rw); _sh.Tune(kh, rh); _sr.Tune(Math.Max(kw, kh), 0.95);
        _sizeWDelay = wDelayMs / 1000.0;
        _sizeHDelay = hDelayMs / 1000.0;
        _sw.Target = tw; _sh.Target = th; _sr.Target = tr;
        _sizeDone = done;
        _sizeLoop ??= new FrameLoop(AdvanceSize);
        _sizeLoop.Start();
    }

    private bool AdvanceSize(double dt)
    {
        // a delayed spring keeps its old target until its delay is over
        bool more = false;
        if (_sizeWDelay > 0) { _sizeWDelay -= dt; more = true; } else more |= _sw.Advance(dt);
        if (_sizeHDelay > 0) { _sizeHDelay -= dt; more = true; } else more |= _sh.Advance(dt);
        more |= _sr.Advance(dt);

        Island.Width = Math.Max(1, _sw.Value);
        Island.Height = Math.Max(1, _sh.Value);
        BottomRadius = Math.Max(0, _sr.Value);

        if (!more)
        {
            var d = _sizeDone;
            _sizeDone = null;
            d?.Invoke();
        }
        return more;
    }

    private void SnapIsland(bool expand)
    {
        StopSizeSprings();
        Island.BeginAnimation(WidthProperty, null);
        Island.BeginAnimation(HeightProperty, null);
        BeginAnimation(BottomRadiusProperty, null);
        Island.Width = expand ? _s.ExpandedWidth : _s.CollapsedWidth;
        Island.Height = expand ? _s.ExpandedHeight : _s.CollapsedHeight;
        BottomRadius = expand ? _s.ExpandedRadius : CollapsedR;
        ApplyContentSize();
    }

    private void Animate(bool expand)
    {
        _lastAnimTick = Environment.TickCount64;
        // The activity bar controls the width while it is shown; opening the island drops it
        if (!expand && _hudActive) return;
        if (expand && _hudActive) CancelHud();

        bool wasExpanded = _expanded;
        _expanded = expand;
        if (expand) HideEyes();
        else if (wasExpanded) { var et = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) }; et.Tick += (_, _) => { et.Stop(); UpdateEyes(); }; et.Start(); }
        UpdateBarTimer();
        UpdateStatsTimer();
        UpdateProgressTimer();
        UpdateWeatherAnim();
        int token = ++_animToken;

        double tw = expand ? _s.ExpandedWidth : _s.CollapsedWidth;
        double th = expand ? _s.ExpandedHeight : _s.CollapsedHeight;
        double tr = expand ? _s.ExpandedRadius : CollapsedR;

        MemoryGuard.Burst(); // headroom while it moves (the cap is back a few seconds after closing)
        if (expand)
        {
            _trimTimer.Stop();
        }

        // ----- Size: real springs (keep their speed if the island is opened / closed again in the middle) -----
        double zoom = 0.97; // start / end scale of the content
        double sp = Math.Clamp(_s.AnimSpeed, 0.3, 3.0);
        double kw, rw, kh, rh;          // stiffness and damping ratio of the width and of the height
        double wDelay = 0, hDelay = 0;  // ms
        switch (_s.AnimStyle)
        {
            case "Bounce":  (kw, rw, kh, rh) = expand ? (330, 0.62, 330, 0.62) : (420, 0.8, 420, 0.8); break;
            case "Snappy":  (kw, rw, kh, rh) = expand ? (900, 1.0, 900, 1.0) : (1000, 1.0, 1000, 1.0); break;
            case "Scale":   (kw, rw, kh, rh) = expand ? (420, 0.95, 420, 0.95) : (520, 1.0, 520, 1.0); zoom = 0.88; break;
            case "Genie":
                zoom = 0.94;
                if (expand) { (kw, rw, kh, rh) = (200, 0.8, 340, 0.72); }
                else { (kw, rw, kh, rh) = (460, 0.95, 300, 0.95); wDelay = 40; }
                break;
            case "Notch":   (kw, rw, kh, rh) = expand ? (380, 0.78, 380, 0.78) : (460, 0.9, 460, 0.9); zoom = 0.96; break;
            case "Minimal": (kw, rw, kh, rh) = expand ? (460, 1.0, 460, 1.0) : (560, 1.0, 560, 1.0); zoom = 1.0; break;
            case "Fluid":
                if (expand) { (kw, rw, kh, rh) = (250, 0.88, 360, 0.78); wDelay = 30; }
                else { (kw, rw, kh, rh) = (480, 0.92, 400, 0.92); hDelay = 30; }
                break;
            default:        (kw, rw, kh, rh) = expand ? (420, 0.9, 420, 0.9) : (520, 0.95, 520, 0.95); break; // Smooth
        }
        double kk = sp * sp; // faster setting = stiffer spring
        StartSizeSprings(tw, th, tr, kw * kk, rw, kh * kk, rh, wDelay / sp, hDelay / sp, () =>
        {
            if (token != _animToken) return;
            if (_expanded) SetExpandedCache(false); // settled: drop the bitmap so small updates only redraw what changed
            else
            {
                // Free memory a few seconds later (not right away: that would make the next open stutter)
                _trimTimer.Stop();
                _trimTimer.Start();
            }
        });
        if (expand) SetExpandedCache(true); // content is drawn once into a bitmap while the island opens

        // ----- Content animation (no bounce on text, it looks cheap) -----
        if (expand)
        {
            Expanded.Visibility = Visibility.Visible;
            Expanded.IsHitTestVisible = true;

            Compact.BeginAnimation(OpacityProperty, Da(0, 120, CubicOut()));
            Expanded.BeginAnimation(OpacityProperty, Da(1, 300, CubicOut(), 110));
            ExpandedShift.BeginAnimation(TranslateTransform.YProperty, Da(0, 420, CubicOut(), 70));
            var zx = new DoubleAnimation(zoom, 1, Dur(420)) { BeginTime = Dur(70), EasingFunction = CubicOut() };
            var zy = new DoubleAnimation(zoom, 1, Dur(420)) { BeginTime = Dur(70), EasingFunction = CubicOut() };
            ExpandedScale.BeginAnimation(ScaleTransform.ScaleXProperty, zx);
            ExpandedScale.BeginAnimation(ScaleTransform.ScaleYProperty, zy);

            if (!wasExpanded) PlayGlass(); // Liquid Glass: shine + rim flash (does nothing when Glass is off)

            // Everything that only fills the content runs AFTER the first frames of the animation,
            // so tapping the island never waits for it
            int openToken = token;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (openToken != _animToken || !_expanded) return;
                if (!wasExpanded) PrimeStats();
                RefreshProgress();
                if (_view != ViewMedia) OnViewShown(_view);
            }));
        }
        else
        {
            Expanded.IsHitTestVisible = false;
            SetExpandedCache(true); // the shrinking content is one bitmap: no layout of every label on every frame

            var fade = Da(0, 140, CubicOut());
            fade.Completed += (_, _) =>
            {
                // Hidden content costs nothing (no layout, no drawing) while the island is collapsed
                if (token == _animToken && !_expanded)
                {
                    SetExpandedCache(false);
                    Expanded.Visibility = Visibility.Collapsed;
                    ResetToMediaPage(); // next time it opens on the music page
                }
            };
            Expanded.BeginAnimation(OpacityProperty, fade);
            ExpandedShift.BeginAnimation(TranslateTransform.YProperty, Da(8, 220, CubicOut()));
            ExpandedScale.BeginAnimation(ScaleTransform.ScaleXProperty, Da(zoom, 220, CubicOut()));
            ExpandedScale.BeginAnimation(ScaleTransform.ScaleYProperty, Da(zoom, 220, CubicOut()));

            Compact.BeginAnimation(OpacityProperty, Da(1, 260, CubicOut(), 150));
        }
    }

    /// <summary>Plays open, then close, so the user can see the chosen animation (used by the settings).</summary>
    public async void PreviewAnimation()
    {
        int t = ++_previewToken;
        await Task.Delay(200); // wait until a slider stops moving
        if (t != _previewToken) return;

        if (_expanded)
        {
            // Already open: close first so the new style can be seen from the start
            _holdOpen = false;
            Animate(false);
            await Task.Delay((int)(600 / Math.Max(0.1, _s.AnimSpeed)));
            if (t != _previewToken) return;
        }

        _holdOpen = true;
        Animate(true);
        await Task.Delay(1400);
        if (t != _previewToken) return;

        _holdOpen = false;
        if (!Island.IsMouseOver) Animate(false);
    }

    // ---------- Seek bar ----------

    private void UpdateProgressTimer()
    {
        bool need = _expanded && _playing;
        if (need && !_progressTimer.IsEnabled) _progressTimer.Start();
        else if (!need && _progressTimer.IsEnabled) _progressTimer.Stop();
    }

    private void RefreshProgress()
    {
        if (_seeking) return;
        var (pos, dur) = _media.GetProgress(_playing);
        _duration = dur;
        ShowProgress(pos, dur, true);
    }

    private double SeekWidth()
    {
        double w = SeekHost.ActualWidth;
        if (w <= 0) w = Math.Max(60, ExpandedInner.Width - 96); // first frame, before layout
        return w;
    }

    private void StopSeekAnim() => SeekFill.BeginAnimation(WidthProperty, null);

    // The bar moves with ONE long linear animation (from now to the end of the song), so it glides at the
    // full frame rate instead of stepping twice a second. The timer only corrects it when it drifts.
    private void ShowProgress(double pos, double dur, bool animate)
    {
        SeekHost.Opacity = dur > 0 ? 1 : 0.3;
        ElapsedText.Text = Fmt(pos);
        TotalText.Text = dur > 0 ? "-" + Fmt(Math.Max(0, dur - pos)) : "0:00";

        double w = SeekWidth();
        double frac = dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0;

        if (animate && _playing && dur > 0 && frac < 1)
        {
            // Keep the running animation while it is still right (within about 0.6 s)
            double now = SeekFill.Width;
            bool running = _seekAnimOn && !double.IsNaN(now)
                           && Math.Abs(now - frac * w) < w * (0.6 / dur) + 0.5
                           && Math.Abs(_seekAnimW - w) < 0.5;
            if (!running) StartSeekAnim(frac, dur - pos, w);
        }
        else
        {
            _seekAnimOn = false;
            StopSeekAnim();
            SeekFill.Width = frac * w;
        }
    }

    private bool _seekAnimOn;
    private double _seekAnimW;

    private void StartSeekAnim(double frac, double remainSeconds, double w)
    {
        _seekAnimOn = true;
        _seekAnimW = w;
        var a = new DoubleAnimation(frac * w, w, TimeSpan.FromSeconds(Math.Max(0.2, remainSeconds)))
        {
            FillBehavior = FillBehavior.HoldEnd
        };
        SeekFill.BeginAnimation(WidthProperty, a);
    }

    private double SeekFraction(MouseEventArgs e)
    {
        double w = SeekHost.ActualWidth;
        return w <= 0 ? 0 : Math.Clamp(e.GetPosition(SeekHost).X / w, 0, 1);
    }

    // The bar grows a little taller while the cursor is on it (like Apple Music)
    private void GrowSeek(bool grow)
    {
        var a = new DoubleAnimation(grow ? 1.9 : 1.0, Dur(grow ? 160 : 260)) { EasingFunction = CubicOut() };
        SeekGrow.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private void Seek_Enter(object sender, MouseEventArgs e) => GrowSeek(true);
    private void Seek_Leave(object sender, MouseEventArgs e) { if (!_seeking) GrowSeek(false); }

    private void DragTo(MouseEventArgs e)
    {
        double f = SeekFraction(e);
        _seekAnimOn = false;
        StopSeekAnim();
        SeekFill.Width = f * SeekHost.ActualWidth; // follows the cursor 1:1, no animation = no lag
        ElapsedText.Text = Fmt(f * _duration);
        TotalText.Text = "-" + Fmt(Math.Max(0, _duration - f * _duration));
    }

    private void Seek_Down(object sender, MouseButtonEventArgs e)
    {
        if (_duration <= 0) return;
        _seeking = true;
        SeekHost.CaptureMouse();
        GrowSeek(true);
        DragTo(e);
        e.Handled = true;
    }

    private void Seek_Move(object sender, MouseEventArgs e)
    {
        if (_seeking) DragTo(e);
    }

    private async void Seek_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) return;

        double target = SeekFraction(e) * _duration;
        _seeking = false;
        SeekHost.ReleaseMouseCapture();
        if (!SeekHost.IsMouseOver) GrowSeek(false);

        // Continue from the new spot right away (the player confirms a moment later)
        double w = SeekWidth();
        if (_playing && _duration > 0) StartSeekAnim(target / _duration, _duration - target, w);
        await _media.SeekAsync(target);
        await Task.Delay(700);
        if (!_seeking) RefreshProgress();

        if (!Island.IsMouseOver && !_holdOpen) Animate(false);
    }

    private static string Fmt(double sec)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, sec));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    // ---------- Equalizer bars (run only while a song is playing) ----------

    private void SetBars(bool playing)
    {
        Bars.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;

        if (playing == _barsRunning) return;
        _barsRunning = playing;

        if (!playing)
        {
            _barLevel = 0;
            Array.Clear(_barHist);
            foreach (ScaleTransform b in new[] { B1, B2, B3, B4 }) b.ScaleY = BarMin;
        }
        UpdateBarTimer();
    }

    private const double BarMin = 0.18;

    // The bars are only visible while the island is collapsed, so the timer sleeps while it is open
    private void UpdateBarTimer()
    {
        bool run = _barsRunning && !_expanded;
        if (run && !_barTimer.IsEnabled) _barTimer.Start();
        else if (!run && _barTimer.IsEnabled) _barTimer.Stop();
    }

    private void BarTick()
    {
        // Real loudness of the sound going to the speakers (0..1).
        // Music is usually quiet on this scale, so stretch it a little.
        double target = Math.Min(1.0, Math.Sqrt(_meter.GetPeak()) * 1.3);

        // Rises fast, falls slowly (like a real equalizer)
        _barLevel += (target - _barLevel) * (target > _barLevel ? 0.75 : 0.22);

        // Each bar shows the loudness a moment ago, so a wave runs through the four bars
        _barHist[3] = _barHist[2];
        _barHist[2] = _barHist[1];
        _barHist[1] = _barHist[0];
        _barHist[0] = _barLevel;

        double[] weight = { 1.0, 0.8, 0.95, 0.65 };
        ScaleTransform[] bars = { B1, B2, B3, B4 };
        for (int i = 0; i < bars.Length; i++)
            bars[i].ScaleY = Math.Clamp(BarMin + (1 - BarMin) * _barHist[i] * weight[i], BarMin, 1.0);
    }

    // ---------- Media UI update ----------

    private void UpdateUi(string title, string artist, bool playing, byte[]? art)
    {
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "Nothing playing" : title;
        ArtistText.Text = artist;
        SetPlayIcon(playing);
        SetBars(playing);

        _playing = playing;
        _curTitle = title;
        UpdateCenter();
        UpdateProgressTimer();
        if (_expanded) RefreshProgress();

        // Album art is decoded again only when the song changes
        if (!ReferenceEquals(art, _lastArt))
        {
            _lastArt = art;
            Brush brush = MakeArtBrush(art) ?? Placeholder;
            ArtSmall.Background = brush;
            ArtBig.Background = brush;
        }

        // Small art (left) only while a song is playing
        ArtSmall.Visibility = playing && !string.IsNullOrWhiteSpace(title) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ImageBrush? MakeArtBrush(byte[]? data)
    {
        if (data == null || data.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(data);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 96; // small decode = less RAM
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();

            var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
    }

    private static Brush MakeFrozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    // ---------- Media buttons ----------

    private async void Prev_Click(object sender, RoutedEventArgs e)
    {
        await _media.PreviousAsync();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        await _media.TogglePlayPauseAsync();
    }

    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        Pop(sender);
        await _media.ShuffleAsync();
    }

    private async void Repeat_Click(object sender, RoutedEventArgs e)
    {
        Pop(sender);
        await _media.RepeatAsync();
    }

    // Shuffle / repeat buttons show the real state of the player; a dim one means the player does not offer it
    private void UpdateModes()
    {
        try
        {
            Brush on = new SolidColorBrush(AccentValue());
            ShuffleBtn.IsEnabled = _media.ShuffleSupported;
            ShuffleBtn.Opacity = !_media.ShuffleSupported ? 0.18 : _media.ShuffleOn ? 1.0 : 0.5;
            ShuffleIcon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextBrush");
            if (_media.ShuffleOn) ShuffleIcon.Fill = on;

            RepeatBtn.IsEnabled = _media.RepeatSupported;
            RepeatBtn.Opacity = !_media.RepeatSupported ? 0.18 : _media.RepeatMode != 0 ? 1.0 : 0.5;
            RepeatIcon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextBrush");
            RepeatOne.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextBrush");
            if (_media.RepeatMode != 0) { RepeatIcon.Fill = on; RepeatOne.Fill = on; }
            RepeatOne.Visibility = _media.RepeatMode == 1 ? Visibility.Visible : Visibility.Collapsed;
            ShuffleBtn.ToolTip = _media.ShuffleSupported ? (_media.ShuffleOn ? "Shuffle: on" : "Shuffle: off") : "This player has no shuffle";
            RepeatBtn.ToolTip = !_media.RepeatSupported ? "This player has no repeat" : _media.RepeatMode == 0 ? "Repeat: off" : _media.RepeatMode == 1 ? "Repeat: this song" : "Repeat: all";
        }
        catch { }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        await _media.NextAsync();
    }

    // Small "pop" bounce when a button is pressed
    private static void Pop(object sender)
    {
        if (sender is FrameworkElement el && el.RenderTransform is ScaleTransform st)
        {
            var a = new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _barTimer.Stop();
        DisposePower();
        DisposeSnap();
        try { _wall.Stop(); } catch { }
        DisposeMilo();
        RemoveReserve();
        _meter.Dispose();
        DisposeHud();
        DisposeExtras();
        DisposeTasks();
        _tray?.Dispose(); // remove the tray icon
        _tray = null;
        base.OnClosed(e);
    }

    // ---------- Keep RAM low ----------

    private static void TrimMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        using var self = Process.GetCurrentProcess();
        EmptyWorkingSet(self.Handle);
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    // ---------- Hide from Alt+Tab (tool window style) ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);

        // The window now exists: center it for real, and keep it centered if the screen setup changes
        CenterOnScreen();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        ApplySnap();
        ApplyMilo();
        ApplyWallpaper();
        ApplyReserve();
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
