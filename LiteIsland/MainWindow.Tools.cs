using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LiteIsland;

// Two pages: "Tools" (copy text from the screen, scan a QR code, pick a color, keep awake, hide desktop icons, lock)
// and "Timer" (countdown with presets). Both are built in code when they are shown and cost nothing while hidden.
public partial class MainWindow
{
    // ---------- native ----------
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus s);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte AcLine, Flag, Percent, Saver; public int LifeTime, FullLifeTime; }

    private const uint EsContinuous = 0x80000000, EsSystem = 0x1, EsDisplay = 0x2;

    private bool _keepAwake, _iconsHidden, _picking;
    private byte _lastAc = 255;
    private int _lastLowStep = 100;

    private void InitTools()
    {
        SystemEvents.PowerModeChanged += OnPowerMode;
        _lastAc = ReadPower(out _, out _) ? _lastAc : (byte)255;
        _powerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        _powerTimer.Tick += (_, _) => CheckPower(false);
        _powerTimer.Start();
        Closed += (_, _) => DisposeTools();
    }

    private DispatcherTimer? _powerTimer;

    private void DisposeTools()
    {
        SystemEvents.PowerModeChanged -= OnPowerMode;
        _powerTimer?.Stop();
        _timerTick?.Stop();
        if (_keepAwake) SetThreadExecutionState(EsContinuous);
        if (_iconsHidden) ToggleDesktopIcons();
    }

    // ---------- charging / battery notifications ----------

    private bool ReadPower(out int percent, out bool hasBattery)
    {
        percent = 0; hasBattery = false;
        try
        {
            if (!GetSystemPowerStatus(out PowerStatus s)) return false;
            hasBattery = s.Flag != 128 && s.Percent != 255;
            percent = s.Percent == 255 ? 0 : s.Percent;
            _lastAc = s.AcLine;
            return true;
        }
        catch { return false; }
    }

    private void OnPowerMode(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;
        Dispatcher.BeginInvoke(new Action(() => CheckPower(true)));
    }

    private void CheckPower(bool fromEvent)
    {
        byte before = _lastAc;
        if (!ReadPower(out int pct, out bool has) || !has) return;

        if (fromEvent && before != 255 && before != _lastAc)
        {
            if (_lastAc == 1) { ShowHud("", "Charging", pct / 100.0, pct + "%", 2800); _lastLowStep = 100; }
            else ShowHud("", "On battery", pct / 100.0, pct + "%", 2400);
            return;
        }
        if (_lastAc == 1) { _lastLowStep = 100; return; }
        int step = pct <= 10 ? 10 : pct <= 20 ? 20 : 100;
        if (step < _lastLowStep)
        {
            _lastLowStep = step;
            ShowHud("", "Battery low", pct / 100.0, pct + "%", 4200);
        }
    }

    // ---------- page: Tools ----------

    private Border ToolTile(string icon, string title, string sub, Action click, bool on = false)
    {
        var b = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(3), Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "TileBg");
        b.SetResourceReference(Border.BorderBrushProperty, on ? "AccentBrush" : "TileStroke");
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        FrameworkElement ic = Icons.Make(icon, 26, on ? (Brush)FindResource("AccentBrush") : null, 1.6);
        ic.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(ic);
        var t = Txt(title, 12.5, true);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, 6, 0, 0);
        sp.Children.Add(t);
        var s = Txt(sub, 10.5, false, true);
        s.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(s);
        b.Child = sp;
        b.MouseEnter += (_, _) => b.Background = HoverBrush;
        b.MouseLeave += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "TileBg");
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    private void BuildTools()
    {
        if (ToolsHost == null) return;
        ToolsHost.Children.Clear();
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new Grid { Height = 20, Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(new TextBlock { Text = "Tools", Style = (Style)FindResource("PageTitle") });
        root.Children.Add(head);

        var grid = new UniformGrid { Columns = 3, Rows = 2 };
        grid.Children.Add(ToolTile("text", "Copy text", "from the screen", () => PickFromScreen("text")));
        grid.Children.Add(ToolTile("phone", "Scan QR", "on the screen", () => PickFromScreen("qr")));
        grid.Children.Add(ToolTile("cutout", "Pick color", "click any pixel", () => PickFromScreen("color")));
        grid.Children.Add(ToolTile("audio", "Keep awake", _keepAwake ? "On" : "Off", ToggleKeepAwake, _keepAwake));
        grid.Children.Add(ToolTile("image", "Desktop icons", _iconsHidden ? "Hidden" : "Visible", () => { ToggleDesktopIcons(); BuildTools(); }, _iconsHidden));
        grid.Children.Add(ToolTile("merge", "Lock PC", "Win + L", () => { try { LockWorkStation(); } catch { } }));
        Grid.SetRow(grid, 1);
        root.Children.Add(grid);
        ToolsHost.Children.Add(root);
    }

    private void ToggleKeepAwake()
    {
        _keepAwake = !_keepAwake;
        SetThreadExecutionState(_keepAwake ? EsContinuous | EsSystem | EsDisplay : EsContinuous);
        ShowHud("", _keepAwake ? "Keeping your PC awake" : "Sleep is back to normal", null, null, 1800);
        BuildTools();
    }

    private static IntPtr DesktopListView()
    {
        IntPtr defView = FindWindowEx(FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            IntPtr w = IntPtr.Zero;
            while (defView == IntPtr.Zero)
            {
                w = FindWindowEx(IntPtr.Zero, w, "WorkerW", null);
                if (w == IntPtr.Zero) break;
                defView = FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
            }
        }
        return defView == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    private void ToggleDesktopIcons()
    {
        try
        {
            IntPtr lv = DesktopListView();
            if (lv == IntPtr.Zero) return;
            bool visible = IsWindowVisible(lv);
            ShowWindow(lv, visible ? 0 : 5);
            _iconsHidden = visible;
        }
        catch { }
    }

    // Copy text / scan a QR code / pick a color: the picking runs in a separate helper process (a screenshot of a big
    // screen is tens of MB, so it never touches the island's own RAM)
    private async void PickFromScreen(string mode)
    {
        if (_picking) return;
        _picking = true;
        try
        {
            _holdOpen = false;
            if (_expanded) Animate(false);
            await Task.Delay(420); // let the island close so it is not in the picture
            string r = await Proc.RunWorkerAsync("pick", new[] { mode }, null, CancellationToken.None);
            r = (r ?? "").Trim();
            if (r.Length == 0 || r == "CANCEL") return;
            if (r == "NONE")
            {
                ShowHud("", mode == "qr" ? "No QR code found" : "No text found", null, null, 2200);
                return;
            }
            SetClipboardText(r);
            string label = mode == "color" ? "Color copied" : mode == "qr" ? "QR copied" : "Text copied";
            string right = mode == "color" ? r : r.Length > 24 ? r.Substring(0, 22) + "…" : r;
            ShowHud("", label, null, right.Replace('\n', ' '), 2800);
        }
        catch { ShowHud("", "Could not read the screen", null, null, 2200); }
        finally { _picking = false; }
    }

    // ---------- page: Timer ----------

    private DispatcherTimer? _timerTick;
    private bool _timerRunning;
    private double _timerTotal = 300, _timerLeft = 300; // seconds
    private DateTime _timerEnd;
    private TextBlock? _tmText;
    private Path? _tmArc;
    private TextBlock? _tmGo;
    private Border? _tmGoBtn;

    private void BuildTimer()
    {
        if (TimerHost == null) return;
        TimerHost.Children.Clear();
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new Grid { Height = 20, Margin = new Thickness(0, 0, 0, 2) };
        head.Children.Add(new TextBlock { Text = "Timer", Style = (Style)FindResource("PageTitle") });
        root.Children.Add(head);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // ring + time
        var ring = new Grid { Width = 118, Height = 118, Margin = new Thickness(8, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center };
        var track = new Ellipse { Width = 112, Height = 112, StrokeThickness = 6 };
        track.SetResourceReference(Shape.StrokeProperty, "TrackBrush");
        ring.Children.Add(track);
        _tmArc = new Path { StrokeThickness = 6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 118, Height = 118 };
        _tmArc.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        ring.Children.Add(_tmArc);
        _tmText = Txt("5:00", 26, true);
        _tmText.HorizontalAlignment = HorizontalAlignment.Center;
        _tmText.VerticalAlignment = VerticalAlignment.Center;
        ring.Children.Add(_tmText);
        body.Children.Add(ring);

        // presets + buttons
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (int m in new[] { 1, 5, 10, 25, 45 })
        {
            int mm = m;
            presets.Children.Add(Chip(ChipText(mm + " min", Math.Abs(_timerTotal - mm * 60) < 0.5, 12), () => SetTimer(mm * 60), Math.Abs(_timerTotal - mm * 60) < 0.5));
        }
        right.Children.Add(presets);
        var adj = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        adj.Children.Add(Chip(ChipText("− 1 min", false, 12), () => SetTimer(Math.Max(60, _timerTotal - 60)), false));
        adj.Children.Add(Chip(ChipText("+ 1 min", false, 12), () => SetTimer(Math.Min(99 * 60, _timerTotal + 60)), false));
        right.Children.Add(adj);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _tmGo = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold, Foreground = OnFillBrush, VerticalAlignment = VerticalAlignment.Center };
        _tmGoBtn = PillButton(_tmGo, ToggleTimer);
        _tmGoBtn.MinWidth = 88;
        _tmGo.HorizontalAlignment = HorizontalAlignment.Center;
        row.Children.Add(_tmGoBtn);
        var reset = RoundBtn("plus", "Reset", ResetTimer, false, 30);
        reset.Margin = new Thickness(8, 0, 0, 0);
        reset.RenderTransformOrigin = new Point(0.5, 0.5);
        reset.RenderTransform = new RotateTransform(45);
        row.Children.Add(reset);
        right.Children.Add(row);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        TimerHost.Children.Add(root);
        UpdateTimerUi();
    }

    private void SetTimer(double seconds)
    {
        _timerTotal = seconds;
        _timerLeft = seconds;
        _timerRunning = false;
        _timerTick?.Stop();
        BuildTimer();
    }

    private void ResetTimer()
    {
        _timerRunning = false;
        _timerTick?.Stop();
        _timerLeft = _timerTotal;
        UpdateTimerUi();
    }

    private void ToggleTimer()
    {
        if (_timerRunning)
        {
            _timerLeft = Math.Max(0, (_timerEnd - DateTime.Now).TotalSeconds);
            _timerRunning = false;
            _timerTick?.Stop();
        }
        else
        {
            if (_timerLeft <= 0.5) _timerLeft = _timerTotal;
            _timerEnd = DateTime.Now.AddSeconds(_timerLeft);
            _timerRunning = true;
            _timerTick ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timerTick.Tick -= OnTimerTick;
            _timerTick.Tick += OnTimerTick;
            _timerTick.Start();
        }
        UpdateTimerUi();
    }

    private void OnTimerTick(object? s, EventArgs e)
    {
        double left = (_timerEnd - DateTime.Now).TotalSeconds;
        if (left <= 0)
        {
            _timerRunning = false;
            _timerLeft = 0;
            _timerTick?.Stop();
            ShowHud("", "Timer finished", null, FmtTimer(_timerTotal), 6000);
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            UpdateTimerUi();
            return;
        }
        _timerLeft = left;
        if (_expanded && _view == ViewTimer) UpdateTimerUi();
    }

    private static string FmtTimer(double sec)
    {
        int s = (int)Math.Ceiling(Math.Max(0, sec));
        return (s / 60).ToString(CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    private void UpdateTimerUi()
    {
        if (_tmText == null || _tmArc == null || _tmGo == null) return;
        double left = _timerRunning ? Math.Max(0, (_timerEnd - DateTime.Now).TotalSeconds) : _timerLeft;
        _tmText.Text = FmtTimer(left);
        _tmGo.Text = _timerRunning ? "Pause" : left < _timerTotal - 0.5 && left > 0.5 ? "Resume" : "Start";

        double frac = _timerTotal > 0 ? Math.Clamp(left / _timerTotal, 0, 1) : 0;
        const double c = 59, r = 56;
        if (frac <= 0.002) { _tmArc.Data = Geometry.Empty; return; }
        if (frac >= 0.998)
        {
            _tmArc.Data = Geometry.Parse("M 59,3 A 56,56 0 1 1 58.99,3");
            return;
        }
        double ang = frac * 2 * Math.PI;
        double x = c + r * Math.Sin(ang), y = c - r * Math.Cos(ang);
        var fig = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        fig.Segments.Add(new ArcSegment(new Point(x, y), new Size(r, r), 0, ang > Math.PI, SweepDirection.Clockwise, true));
        _tmArc.Data = new PathGeometry(new[] { fig });
    }
}
