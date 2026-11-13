using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LiteIsland;

// Tools page (tiles that start the screen tools, Calculator, Voice memo, QR and Coffee), the Wallpaper strip page
// and the Stopwatch (second tab of the Timer page). Everything is built when its page is shown.
// The heavy tools run in their own helper process (--tool), so the island itself stays small.
public partial class MainWindow
{
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);

    private bool _coffee;
    private Border? _coffeeTile;
    private TextBlock? _coffeeLabel;

    // ---------- Tools page ----------

    private Border ToolTile(string icon, string label, Action click, out TextBlock labelBlock, out FrameworkElement iconEl)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        iconEl = Icons.Make(icon, 21, null, 1.6);
        iconEl.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(iconEl);
        labelBlock = Txt(label, 10.5, true, true);
        labelBlock.HorizontalAlignment = HorizontalAlignment.Center;
        labelBlock.Margin = new Thickness(0, 5, 0, 0);
        sp.Children.Add(labelBlock);

        var b = new Border { CornerRadius = new CornerRadius(17), Margin = new Thickness(3), Cursor = Cursors.Hand, Child = sp, BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "TileBg");
        b.SetResourceReference(Border.BorderBrushProperty, "TileStroke");
        Border self = b;
        b.MouseEnter += (_, _) => { if (!ReferenceEquals(self, _coffeeTile) || !_coffee) self.Background = HoverBrush; };
        b.MouseLeave += (_, _) =>
        {
            if (ReferenceEquals(self, _coffeeTile) && _coffee) self.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            else self.SetResourceReference(Border.BackgroundProperty, "TileBg");
        };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        AddPress(b, 0.92);
        return b;
    }

    private void BuildTools()
    {
        if (ToolsHost == null) return;
        ToolsHost.Children.Clear();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new Grid { Height = 20, Margin = new Thickness(0, 0, 0, 3) };
        head.Children.Add(new TextBlock { Text = "Tools", Style = (Style)FindResource("PageTitle") });
        root.Children.Add(head);

        var grid = new UniformGrid { Columns = 5, Rows = 2 };
        Grid.SetRow(grid, 1);
        root.Children.Add(grid);

        void Add(string icon, string label, Action click) => grid.Children.Add(ToolTile(icon, label, click, out _, out _));

        Add("text", "Text", () => LaunchTool("ocr"));
        Add("eyedropper", "Color", () => LaunchTool("color"));
        Add("pencil", "Draw", () => LaunchTool("draw"));
        Add("lens", "Lens", () => LaunchTool("lens"));
        Add("font", "Font", () => LaunchTool("font"));
        Add("calc", "Calculator", () => LaunchTool("calc"));
        Add("mic", "Memo", () => LaunchTool("memo"));
        Add("qr", "QR", () => LaunchTool("qr"));

        Border coffee = ToolTile("coffee", _coffee ? "Awake" : "Coffee", ToggleCoffee, out TextBlock cl, out _);
        _coffeeTile = coffee;
        _coffeeLabel = cl;
        grid.Children.Add(coffee);
        StyleCoffee();

        Add("image", "Wallpaper", () => GoToView(ViewWall, 1));
        ToolsHost.Children.Add(root);
    }

    private void ToggleCoffee()
    {
        _coffee = !_coffee;
        // ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED keeps the PC (and the screen) awake until switched off
        SetThreadExecutionState(_coffee ? 0x80000000u | 0x1u | 0x2u : 0x80000000u);
        StyleCoffee();
    }

    private void StyleCoffee()
    {
        if (_coffeeTile == null || _coffeeLabel == null) return;
        _coffeeLabel.Text = _coffee ? "Awake" : "Coffee";
        if (_coffee)
        {
            _coffeeTile.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            _coffeeLabel.Foreground = OnFillBrushAccent();
            if (((StackPanel)_coffeeTile.Child).Children[0] is Viewbox vb && vb.Child is System.Windows.Shapes.Path p) p.Stroke = OnFillBrushAccent();
        }
        else
        {
            _coffeeTile.SetResourceReference(Border.BackgroundProperty, "TileBg");
            _coffeeLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
            if (((StackPanel)_coffeeTile.Child).Children[0] is Viewbox vb && vb.Child is System.Windows.Shapes.Path p) p.SetResourceReference(Shape.StrokeProperty, "TextBrush");
        }
    }

    private Brush OnFillBrushAccent() => (Brush)(TryFindResource("OnAccentBrush") ?? InkDark);

    // ---------- Wallpaper strip ----------

    private int _wallToken;

    private void BuildWall()
    {
        if (WallHost == null) return;
        WallHost.Children.Clear();
        int token = ++_wallToken;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new Grid { Height = 26, Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(new TextBlock { Text = "Wallpapers", Style = (Style)FindResource("PageTitle"), VerticalAlignment = VerticalAlignment.Center });
        var more = Pill("Browse", OpenWallpapers);
        more.HorizontalAlignment = HorizontalAlignment.Right;
        more.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(more);
        root.Children.Add(head);

        List<string> files = WallpaperService.LibraryFiles().Take(24).ToList();
        if (files.Count == 0)
        {
            var hint = new TextBlock { Text = "Pictures you set as a wallpaper\nwill show up here.", Style = (Style)FindResource("Hint"), TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetRow(hint, 1);
            root.Children.Add(hint);
            WallHost.Children.Add(root);
            return;
        }

        var strip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var sv = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.HorizontalOnly,
            Tag = "h",
            Content = strip
        };
        sv.PreviewMouseWheel += (_, e) =>
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta * 0.8);
            e.Handled = true;
        };
        Grid.SetRow(sv, 1);
        root.Children.Add(sv);
        WallHost.Children.Add(root);

        // thumbnails are decoded small, one by one, off the UI thread
        Task.Run(() =>
        {
            foreach (string f in files)
            {
                if (token != _wallToken) return;
                BitmapImage? bi = null;
                try
                {
                    bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(f);
                    bi.DecodePixelWidth = 220;
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze();
                }
                catch { bi = null; }
                if (bi == null) continue;
                string path = f;
                BitmapImage img = bi;
                Dispatcher.InvokeAsync(() =>
                {
                    if (token != _wallToken) return;
                    strip.Children.Add(WallThumb(path, img));
                });
            }
        });
    }

    private Border WallThumb(string path, BitmapImage img)
    {
        var inner = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill } };
        var b = new Border
        {
            Width = 114, Height = 72, Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(16), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Padding = new Thickness(0), Child = inner
        };
        b.MouseEnter += (_, _) => b.Opacity = 0.88;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            WallpaperManager.Apply(path, null);
            foreach (UIElement c in ((Panel)b.Parent).Children)
                if (c is Border bb) bb.BorderBrush = Brushes.Transparent;
            b.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        };
        AddPress(b, 0.95);
        Kit_FadeIn(b);
        return b;
    }

    private static void Kit_FadeIn(UIElement e)
    {
        e.Opacity = 0;
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ---------- Stopwatch (second tab of the Timer page) ----------

    private bool _swMode;
    private bool _swRunning;
    private readonly Stopwatch _sw = new();
    private TimeSpan _swBase = TimeSpan.Zero;
    private readonly List<TimeSpan> _swLaps = new();
    private DispatcherTimer? _swTick;
    private TextBlock? _swText, _swTenth, _swGo, _swLapText;
    private Ellipse? _swArc;
    private Border? _swGoBtn;

    private TimeSpan SwElapsed => _swBase + (_swRunning ? _sw.Elapsed : TimeSpan.Zero);

    /// <summary>The "Timer | Stopwatch" switch shown in the header of the Timer page.</summary>
    private UIElement TimerTabs()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Border a = TimerChip("Timer", !_swMode, () => { if (_swMode) { _swMode = false; BuildTimer(); } });
        Border b = TimerChip("Stopwatch", _swMode, () => { if (!_swMode) { _swMode = true; BuildTimer(); } });
        a.Height = 24; b.Height = 24;
        a.CornerRadius = new CornerRadius(12); b.CornerRadius = new CornerRadius(12);
        a.Margin = new Thickness(0, 0, 4, 0);
        sp.Children.Add(a);
        sp.Children.Add(b);
        return sp;
    }

    private void BuildStopwatch(Grid root)
    {
        var body = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var ring = new Grid { Width = RingBox, Height = RingBox, Margin = new Thickness(2, 0, 22, 0), VerticalAlignment = VerticalAlignment.Center };
        var track = new Ellipse { Width = RingBox, Height = RingBox, StrokeThickness = RingStroke };
        track.SetResourceReference(Shape.StrokeProperty, "TrackBrush");
        ring.Children.Add(track);
        _swArc = new Ellipse
        {
            Width = RingBox, Height = RingBox, StrokeThickness = RingStroke, StrokeDashCap = PenLineCap.Round,
            IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(-90)
        };
        _swArc.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        ring.Children.Add(_swArc);
        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _swText = Txt("0:00", 28, true);
        _swText.HorizontalAlignment = HorizontalAlignment.Center;
        mid.Children.Add(_swText);
        _swTenth = Txt(".0", 12, false, true);
        _swTenth.HorizontalAlignment = HorizontalAlignment.Center;
        mid.Children.Add(_swTenth);
        ring.Children.Add(mid);
        body.Children.Add(ring);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _swGo = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _swGoBtn = new Border { Height = 36, CornerRadius = new CornerRadius(18), Cursor = Cursors.Hand, Child = _swGo, Margin = new Thickness(0, 0, 6, 0) };
        AddPress(_swGoBtn);
        _swGoBtn.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleStopwatch(); };
        right.Children.Add(_swGoBtn);

        var row = new Grid { Margin = new Thickness(0, 8, 6, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Border lap = TimerChip("Lap", false, AddLap);
        Border reset = TimerChip("Reset", false, ResetStopwatch);
        Grid.SetColumn(reset, 2);
        row.Children.Add(lap);
        row.Children.Add(reset);
        right.Children.Add(row);

        _swLapText = Txt("", 11.5, false, true);
        _swLapText.Margin = new Thickness(2, 8, 0, 0);
        _swLapText.TextTrimming = TextTrimming.CharacterEllipsis;
        right.Children.Add(_swLapText);

        Grid.SetColumn(right, 1);
        body.Children.Add(right);
        TimerHost.Children.Add(root);
        UpdateSwUi();
    }

    private void ToggleStopwatch()
    {
        if (_swRunning)
        {
            _swBase += _sw.Elapsed;
            _sw.Reset();
            _swRunning = false;
            _swTick?.Stop();
        }
        else
        {
            _sw.Restart();
            _swRunning = true;
            _swTick ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
            _swTick.Tick -= OnSwTick;
            _swTick.Tick += OnSwTick;
            _swTick.Start();
        }
        UpdateSwUi();
    }

    private void OnSwTick(object? s, EventArgs e)
    {
        if (!_expanded || _view != ViewTimer || !_swMode) return; // nothing to draw: the clock keeps counting by itself
        UpdateSwUi();
    }

    private void AddLap()
    {
        if (SwElapsed.TotalMilliseconds < 50) return;
        _swLaps.Insert(0, SwElapsed);
        if (_swLaps.Count > 30) _swLaps.RemoveAt(_swLaps.Count - 1);
        UpdateSwUi();
    }

    private void ResetStopwatch()
    {
        _sw.Reset();
        _swBase = TimeSpan.Zero;
        _swRunning = false;
        _swTick?.Stop();
        _swLaps.Clear();
        UpdateSwUi();
    }

    private static string FmtSw(TimeSpan t, bool tenths)
    {
        int total = (int)t.TotalSeconds;
        string s = t.TotalHours >= 1
            ? (total / 3600).ToString(CultureInfo.InvariantCulture) + ":" + (total % 3600 / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (total % 60).ToString("00", CultureInfo.InvariantCulture)
            : (total / 60).ToString(CultureInfo.InvariantCulture) + ":" + (total % 60).ToString("00", CultureInfo.InvariantCulture);
        return tenths ? s + "." + (t.Milliseconds / 100).ToString(CultureInfo.InvariantCulture) : s;
    }

    private void UpdateSwUi()
    {
        if (_swText == null || _swTenth == null || _swArc == null || _swGo == null || _swGoBtn == null || _swLapText == null) return;
        TimeSpan el = SwElapsed;
        _swText.Text = FmtSw(new TimeSpan(el.Ticks - el.Ticks % TimeSpan.TicksPerSecond), false);
        _swTenth.Text = "." + (el.Milliseconds / 100).ToString(CultureInfo.InvariantCulture);
        bool started = el.TotalMilliseconds > 0;
        _swGo.Text = _swRunning ? "Pause" : started ? "Resume" : "Start";
        if (_swRunning) { _swGoBtn.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush"); _swGo.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); }
        else { _swGoBtn.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); _swGo.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush"); }
        _swText.SetResourceReference(TextBlock.ForegroundProperty, (_swRunning || started) ? "AccentBrush" : "TextBrush");
        Ring.SetValue(_swArc, Math.Clamp((el.TotalSeconds % 60) / 60.0, 0, 1));

        if (_swLaps.Count == 0) _swLapText.Text = "";
        else
        {
            var lines = new List<string>();
            for (int i = 0; i < Math.Min(2, _swLaps.Count); i++)
            {
                int num = _swLaps.Count - i;
                TimeSpan prev = i + 1 < _swLaps.Count ? _swLaps[i + 1] : TimeSpan.Zero;
                lines.Add("Lap " + num + "   " + FmtSw(_swLaps[i] - prev, true));
            }
            _swLapText.Text = string.Join("\n", lines);
        }
    }
}
