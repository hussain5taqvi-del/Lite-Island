using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LiteIsland;

// Weather (collapsed island + Weather page), camera / microphone dots, System page, Calendar page,
// and the equalizer / accent colors. Everything here shows real data only.
public partial class MainWindow
{
    private static readonly CultureInfo InvC = CultureInfo.InvariantCulture;
    private static readonly Brush CalTodayText = MakeFrozen(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)));

    private readonly WeatherService _weather = new();
    private readonly PrivacyWatcher _privacy = new();
    private readonly SystemStats _stats = new();
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _statsBusy, _camOn, _micOn;

    private void InitExtras()
    {
        ApplyColors();

        // These services raise their events on background threads: hand them to the UI thread
        _weather.Changed += w => Dispatcher.InvokeAsync(() => ApplyWeather(w));
        _privacy.Changed += (mic, cam) => Dispatcher.InvokeAsync(() => ApplyPrivacy(mic, cam));
        _statsTimer.Tick += (_, _) => StatsTick();

        ApplyWeather(null); // nothing is shown until real weather arrives
        _weather.Start(_s.WeatherCity);
        _privacy.SetEnabled(_s.HudPrivacy);
    }

    private void DisposeExtras()
    {
        _statsTimer.Stop();
        _weather.Dispose();
        _privacy.Dispose();
        _stats.Dispose();
    }

    /// <summary>Called by the settings window (city, units, switches, colors).</summary>
    public void ApplyExtraSettings()
    {
        ApplyColors();
        _privacy.SetEnabled(_s.HudPrivacy);
        if (!_s.HudPrivacy) ApplyPrivacy(false, false);
        _weather.SetCity(_s.WeatherCity);
        ApplyWeather(_weather.Current);
    }

    // ---------- Colors ----------

    private static Brush ColorBrush(string hex, Color fallback)
    {
        Color c = fallback;
        try { c = (Color)ColorConverter.ConvertFromString(hex); } catch { }
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void ApplyColors()
    {
        Color acc = Color.FromRgb(0xFF, 0x9F, 0x0A);
        try { acc = (Color)ColorConverter.ConvertFromString(_s.AccentColor); } catch { }
        Resources["AccentBrush"] = ColorBrush(_s.AccentColor, Color.FromRgb(0xFF, 0x9F, 0x0A));
        Resources["EqBrush"] = Resources["AccentBrush"]; // one main color for the equalizer, rings, timer and buttons
        var soft = new SolidColorBrush(Color.FromArgb(0x3A, acc.R, acc.G, acc.B)); soft.Freeze();
        Resources["AccentSoftBrush"] = soft;
        double lum = 0.299 * acc.R + 0.587 * acc.G + 0.114 * acc.B;
        var on = new SolidColorBrush(lum > 150 ? Color.FromRgb(0x10, 0x10, 0x12) : Colors.White); on.Freeze();
        Resources["OnAccentBrush"] = on;
    }

    // ---------- Weather ----------

    private string Temp(double celsius)
    {
        double v = _s.UseFahrenheit ? celsius * 9.0 / 5.0 + 32.0 : celsius;
        return ((int)Math.Round(v)).ToString(InvC) + "°";
    }

    // The weather animation only runs while the clock / weather page is on screen
    private void UpdateWeatherAnim()
    {
        bool on = _expanded && _view == ViewClock && IsVisible;
        WeatherArt.Activate(WxArtBox.Child as FrameworkElement, on);
        WeatherArt.Activate(WxSceneBox.Child as FrameworkElement, on);
    }

    private DispatcherTimer? _wxPreviewTimer;

    /// <summary>Settings: shows one kind of weather on the card for a while (code -1 = back to the real weather).</summary>
    public void PreviewWeather(int code, bool day)
    {
        _wxPreviewTimer?.Stop();
        if (code < 0) { ApplyWeather(_weather.Current); return; }
        var cur = _weather.Current;
        WeatherInfo w = cur != null
            ? cur with { Code = code, IsDay = day }
            : new WeatherInfo("Preview", 28, 28, 50, 10, 0, code, day, DateTime.Now, 32, 22);
        ApplyWeather(w);
        _wxPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        _wxPreviewTimer.Tick += (_, _) => { _wxPreviewTimer?.Stop(); ApplyWeather(_weather.Current); };
        _wxPreviewTimer.Start();
        if (!_expanded || _view != ViewClock) { try { OpenTransient(ViewClock, 20000); } catch { } }
    }

    private bool _wxClipWired;

    // The scene is bigger than the card (it fills it): it must never paint outside the card's rounded corners
    private void UpdateSceneClip()
    {
        double w = WxSceneBox.ActualWidth, h = WxSceneBox.ActualHeight;
        if (w < 1 || h < 1) return;
        // inner radius of the card = 22 (corner) - 1 (border). An opacity mask rounds the scene's corners exactly (a plain Clip left square corners)
        var g = new RectangleGeometry(new Rect(0, 0, w, h), 21, 21);
        g.Freeze();
        var mask = new DrawingBrush(new GeometryDrawing(Brushes.Black, null, g));
        mask.Freeze();
        WxSceneBox.OpacityMask = mask;
        WxSceneBox.Clip = g;
    }

    private void ApplyWeather(WeatherInfo? w)
    {
        bool has = w != null;
        if (w != null)
        {
            Geometry icon = WeatherIcons.Get(w.Code, w.IsDay);
            WxIconSmall.Data = icon;
            WeatherArt.Activate(WxArtBox.Child as FrameworkElement, false);
            WeatherArt.Activate(WxSceneBox.Child as FrameworkElement, false);
            WxArtBox.Child = WeatherArt.Build(w.Code, w.IsDay);
            WxSceneBox.Child = WeatherArt.Scene(w.Code, w.IsDay);
            if (!_wxClipWired)
            {
                _wxClipWired = true;
                WxSceneBox.SizeChanged += (_, _) => UpdateSceneClip();
                WxSceneBox.Loaded += (_, _) => UpdateSceneClip();
            }
            UpdateSceneClip();
            WxContent.Background = WeatherArt.Sky(w.Code, w.IsDay);
            UpdateWeatherAnim();

            string t = Temp(w.TempC);
            WxTemp.Text = t;
            WxBigTemp.Text = t;
            WxCity.Text = w.City;
            WxCond.Text = WeatherService.Describe(w.Code);
            WxHL.Text = w.HighC.HasValue && w.LowC.HasValue ? "H:" + Temp(w.HighC.Value) + "  L:" + Temp(w.LowC.Value) : "";
        }

        // The card always shows the time; without weather it is a plain tile with theme colors
        WxParts.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        WxArtBox.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        WxSceneBox.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has)
        {
            WxContent.SetResourceReference(Border.BackgroundProperty, "TileBg");
            ClockBig.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            ClockDate.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        }
        else
        {
            ClockBig.Foreground = Brushes.White;
            ClockDate.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255));
        }
        WxHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;

        // Collapsed island: with weather the time and the weather get two equal halves (divider in the middle);
        // without weather only the time is shown, centered
        _hasWx = has;
        ApplyCompactLayout();
    }

    // ---------- Camera / microphone dots ----------

    private void ApplyPrivacy(bool mic, bool cam)
    {
        SetDot(CamDot, CamScale, ref _camOn, cam);
        SetDot(MicDot, MicScale, ref _micOn, mic);
        bool any = _camOn || _micOn;
        PrivBox.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ApplyCompactLayout(); // battery / equalizer make room for the dots
    }

    private void SetDot(Ellipse dot, ScaleTransform scale, ref bool state, bool on)
    {
        if (on == state) return;
        state = on;
        if (on)
        {
            dot.Visibility = Visibility.Visible;
            var a = new DoubleAnimation(0.2, 1, Dur(420)) { EasingFunction = new SpringEase { Damping = 0.6 } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        else
        {
            dot.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- System page ----------

    private SysSample? _lastSample;
    private double? _lastGpu;
    private bool _gpuReady, _gpuBusy, _primeBusy, _speedBusy;
    private long _spdTick;
    private readonly SpeedTest _speed = new();

    // When the island opens: build the CPU / network baseline and the GPU counter list in the
    // background, so the System page already has numbers the moment it appears
    private void PrimeStats()
    {
        if (_primeBusy) return;
        _primeBusy = true;
        Task.Run(() =>
        {
            double? g = null;
            try
            {
                _stats.SampleFast();
                g = _stats.SampleGpu();
            }
            catch { }
            Dispatcher.InvokeAsync(() =>
            {
                _lastGpu = g;
                _gpuReady = true;
                _primeBusy = false;
            });
        });
    }

    // Measured only while the System page is visible (the timer sleeps the rest of the time)
    private void UpdateStatsTimer()
    {
        bool need = _expanded && _view == ViewSys;
        if (need && !_statsTimer.IsEnabled)
        {
            if (_lastSample != null) ShowStats(_lastSample); // never an empty page
            StatsTick();
            _statsTimer.Start();
        }
        else if (!need && _statsTimer.IsEnabled)
        {
            _statsTimer.Stop();
        }
    }

    private void StatsTick()
    {
        // WMI / counters can take a moment: never on the UI thread
        if (!_statsBusy)
        {
            _statsBusy = true;
            Task.Run(() =>
            {
                SysSample? r = null;
                try { r = _stats.SampleFast(); } catch { }
                Dispatcher.InvokeAsync(() =>
                {
                    _statsBusy = false;
                    if (r != null)
                    {
                        _lastSample = r;
                        ShowStats(r);
                    }
                });
            });
        }

        if (!_gpuBusy)
        {
            _gpuBusy = true;
            Task.Run(() =>
            {
                double? g = null;
                try { g = _stats.SampleGpu(); } catch { }
                Dispatcher.InvokeAsync(() =>
                {
                    _gpuBusy = false;
                    _lastGpu = g;
                    _gpuReady = true;
                    ShowGpu();
                });
            });
        }
    }

    private static string Pct(double v) => ((int)Math.Round(Math.Clamp(v, 0, 100))).ToString(InvC) + "%";

    private static string Rate(double bytesPerSec)
    {
        if (bytesPerSec < 1024) return "0 KB/s";
        if (bytesPerSec < 1024 * 1024) return ((int)Math.Round(bytesPerSec / 1024)).ToString(InvC) + " KB/s";
        return (bytesPerSec / (1024 * 1024)).ToString("0.0", InvC) + " MB/s";
    }

    private void SetBar(System.Windows.Shapes.Ellipse ring, double fraction)
    {
        var a = new DoubleAnimation(Math.Clamp(fraction, 0, 1), Dur(450)) { EasingFunction = CubicOut() };
        ring.BeginAnimation(Ring.ValueProperty, a);
    }

    private void ShowGpu()
    {
        if (!_gpuReady)
        {
            SysGpuV.Text = "\u2026";
            SysGpuS.Text = " ";
        }
        else if (_lastGpu != null)
        {
            SysGpuV.Text = Pct(_lastGpu.Value);
            SysGpuS.Text = "3D load";
            SetBar(SysGpuRing, _lastGpu.Value / 100.0);
        }
        else
        {
            SysGpuV.Text = "N/A";
            SysGpuS.Text = "Not reported";
            SetBar(SysGpuRing, 0);
        }
    }

    private void ShowStats(SysSample r)
    {
        SysCpuV.Text = r.Primed ? Pct(r.Cpu) : "\u2026";
        SysCpuS.Text = "Processor";
        SetBar(SysCpuRing, r.Cpu / 100.0);

        SysRamV.Text = Pct(r.RamPct);
        SysRamS.Text = r.RamUsedGb.ToString("0.0", InvC) + " / " + r.RamTotalGb.ToString("0.0", InvC) + " GB";
        SetBar(SysRamRing, r.RamPct / 100.0);

        SysDskV.Text = Pct(r.DiskPct);
        SysDskS.Text = r.DiskName + " " + r.DiskUsedGb.ToString("0", InvC) + " / " + r.DiskTotalGb.ToString("0", InvC) + " GB";
        SetBar(SysDskRing, r.DiskPct / 100.0);

        ShowGpu();
    }

    // ---------- Speed test ----------

    private static string Mbps(double v) =>
        (v >= 100 ? ((int)Math.Round(v)).ToString(InvC) : v.ToString("0.0", InvC)) + " Mbps";

    private async void Speed_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_speedBusy) return;
        _speedBusy = true;

        SpdBtnText.Text = "Testing\u2026";
        SpdBtn.Opacity = 0.6;
        SpdV.Text = "\u2026";
        SpdS.Text = "Ping\u2026";

        try
        {
            await _speed.RunAsync(p =>
            {
                // Progress comes very often: update the screen at most ~6 times a second
                long now = Environment.TickCount64;
                bool final = p.Phase == SpeedTest.Phase.Done || p.Phase == SpeedTest.Phase.Failed;
                if (!final && now - _spdTick < 160) return;
                _spdTick = now;
                Dispatcher.InvokeAsync(() => ShowSpeed(p));
            }, CancellationToken.None);
        }
        catch
        {
            SpdV.Text = "Failed";
            SpdS.Text = "No internet?";
        }
        finally
        {
            _speedBusy = false;
            SpdBtnText.Text = "Retest";
            SpdBtn.Opacity = 1;
        }
    }

    private void ShowSpeed(SpeedTest.Progress p)
    {
        switch (p.Phase)
        {
            case SpeedTest.Phase.Ping:
                SpdV.Text = "\u2026";
                SpdS.Text = "Ping\u2026";
                break;
            case SpeedTest.Phase.Download:
                SpdV.Text = p.DownMbps > 0 ? Mbps(p.DownMbps) : "\u2026";
                SpdS.Text = "Download\u2026";
                break;
            case SpeedTest.Phase.Upload:
                SpdV.Text = Mbps(p.DownMbps);
                SpdS.Text = p.UpMbps > 0 ? "\u2191 " + Mbps(p.UpMbps) : "Upload\u2026";
                break;
            case SpeedTest.Phase.Done:
                SpdV.Text = Mbps(p.DownMbps);
                SpdS.Text = "\u2191 " + Mbps(p.UpMbps) + " \u00B7 " + ((int)Math.Round(p.PingMs)).ToString(InvC) + " ms";
                break;
            default:
                SpdV.Text = "Failed";
                SpdS.Text = "No internet?";
                break;
        }
    }

    // ---------- Clean (temp files, caches, recycle bin, DNS) ----------

    private bool _cleanBusy, _cleanArmed;
    private int _cleanToken;

    private static string Size(long b)
    {
        if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.0", InvC) + " GB";
        if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0", InvC) + " MB";
        if (b >= 1L << 10) return (b / (double)(1L << 10)).ToString("0", InvC) + " KB";
        return b.ToString(InvC) + " B";
    }

    private async void Clean_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_cleanBusy) return;

        // First click only asks (the Recycle Bin is emptied for good); a second click inside 4 s cleans
        if (!_cleanArmed)
        {
            _cleanArmed = true;
            int t = ++_cleanToken;
            CleanBtnText.Text = "Sure?";
            CleanS.Text = "Empties recycle bin";
            await Task.Delay(4000);
            if (t == _cleanToken && _cleanArmed && !_cleanBusy)
            {
                _cleanArmed = false;
                CleanBtnText.Text = "Clean";
                CleanS.Text = "Temp, cache, bin";
            }
            return;
        }

        _cleanArmed = false;
        _cleanToken++;
        _cleanBusy = true;
        CleanBtnText.Text = "Cleaning\u2026";
        CleanBtn.Opacity = 0.6;
        CleanV.Text = "\u2026";
        CleanS.Text = "Please wait";

        try
        {
            var r = await Cleaner.RunAsync();
            CleanV.Text = Size(r.Bytes);
            CleanS.Text = r.Files.ToString(InvC) + " files removed";
        }
        catch
        {
            CleanV.Text = "Failed";
            CleanS.Text = "Try again";
        }
        finally
        {
            _cleanBusy = false;
            CleanBtnText.Text = "Clean";
            CleanBtn.Opacity = 1;
            _trimTimer.Stop();
            _trimTimer.Start(); // give the memory back afterwards
        }
    }

    // ---------- Calendar page ----------

    private void BuildCalendar()
    {
        DateTime now = DateTime.Now;
        CalMonth.Text = now.ToString("MMMM", InvC);
        CalYear.Text = now.Year.ToString(InvC);
        CalDayBig.Text = now.Day.ToString(InvC);
        CalWeekday.Text = now.ToString("dddd", InvC);

        var first = new DateTime(now.Year, now.Month, 1);
        int offset = (int)first.DayOfWeek; // Sunday = 0, like the picture
        int days = DateTime.DaysInMonth(now.Year, now.Month);
        int weeks = (offset + days + 6) / 7;

        var grid = new UniformGrid { Columns = 7, Rows = weeks + 1 };
        string[] head = { "S", "M", "T", "W", "T", "F", "S" };
        for (int i = 0; i < 7; i++)
            grid.Children.Add(CalCell(head[i], true, i == 0 || i == 6, false));

        for (int i = 0; i < weeks * 7; i++)
        {
            int d = i - offset + 1;
            if (d < 1 || d > days)
                grid.Children.Add(new Border());
            else
                grid.Children.Add(CalCell(d.ToString(InvC), false, i % 7 == 0 || i % 7 == 6, d == now.Day));
        }
        CalHost.Child = grid;
    }

    private static UIElement CalCell(string text, bool header, bool dim, bool today)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = header ? 11 : 12.5,
            FontWeight = header || !today ? FontWeights.SemiBold : FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (today)
        {
            tb.Foreground = CalTodayText;
            var g = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var circle = new Ellipse { Width = 21, Height = 21 };
            circle.SetResourceReference(Shape.FillProperty, "AccentBrush");
            g.Children.Add(circle);
            g.Children.Add(tb);
            return g;
        }

        tb.SetResourceReference(TextBlock.ForegroundProperty, dim ? "TextDimBrush" : "TextBrush");
        if (header) tb.Opacity = 0.8;
        return tb;
    }
}
