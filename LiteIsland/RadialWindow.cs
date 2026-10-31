using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LiteIsland;

/// <summary>What the ring shows after a pick: progress, then a check (or a cross) when the work is over.</summary>
internal sealed class RingResult
{
    public Task<bool> Done = Task.FromResult(true);
    public Func<double> Progress = () => -1;
}

/// <summary>
/// The donut that appears under the cursor while the ring key is held over a selected file; it goes when the key is let go. Eight slices per page; Tab,
/// the mouse wheel or the middle of the ring turns to the next page. Hover pops a slice out, a click starts the work
/// right inside the ring (progress, then a check). Right-click pins a format to the front page. Esc closes it.
/// Created for one use and closed again, so it costs no RAM while idle.
/// </summary>
internal sealed class RadialWindow : Window
{
    private const double Outer = 124, Inner = 50, Pad = 22;

    private readonly List<List<ActionDef>> _pages;
    private readonly Func<ActionDef, RingResult?> _pick;
    private readonly Func<ActionDef, bool>? _togglePin, _isPinned;
    private readonly Color _accent, _onAccent, _fillC, _gapC, _textC;
    private readonly Brush _text, _muted;
    private readonly string _title;
    private readonly double _c = Outer + Pad;
    private readonly Canvas _host = new();
    private readonly ScaleTransform _hostScale = new(1, 1);
    private readonly TextBlock _titleTx = new(), _subTx = new(), _pageTx = new();
    private readonly StackPanel _hubText = new();
    private readonly Grid _hubFx = new();
    private readonly List<Canvas> _groups = new();
    private readonly DispatcherTimer _safety = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly bool _releasePicks;
    private readonly string _idleText;
    private readonly Func<ActionDef, string?>? _remove;
    private readonly Func<bool>? _stillHeld;
    private readonly DispatcherTimer _heldWatch = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private int _hovered = -1;
    private int _page;
    private bool _closing, _busy, _flipping;

    public RadialWindow(List<List<ActionDef>> pages, string title, ImageSource? fileIcon, bool light, Color accent,
        Func<ActionDef, RingResult?> pick, Func<ActionDef, bool>? togglePin, Func<ActionDef, bool>? isPinned,
        bool releasePicks = false, string idleText = "Let go to close", Func<ActionDef, string?>? remove = null,
        bool glass = false, double glassOpacity = 60, Func<bool>? stillHeld = null)
    {
        _stillHeld = stillHeld;
        _releasePicks = releasePicks;
        _idleText = idleText;
        _remove = remove;
        _pages = pages;
        _pick = pick;
        _togglePin = togglePin;
        _isPinned = isPinned;
        _title = title;
        _accent = accent;
        double lum = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255.0;
        _onAccent = lum > 0.62 ? Color.FromRgb(0x10, 0x10, 0x12) : Colors.White;

        // Liquid Glass: see-through slices with a light rim. The alpha never goes low enough to hurt the text.
        double ga = Math.Clamp(0.5 + glassOpacity / 100.0 * 0.4, 0.58, 0.9);
        byte fa = (byte)(255 * ga * 0.8);
        _fillC = glass
            ? (light ? Color.FromArgb(fa, 0xFF, 0xFF, 0xFF) : Color.FromArgb(fa, 0x34, 0x37, 0x44))
            : (light ? Color.FromRgb(0xF7, 0xF5, 0xF3) : Color.FromRgb(0x1B, 0x1B, 0x1F));
        _gapC = glass
            ? (light ? Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF))
            : (light ? Color.FromRgb(0xDD, 0xD9, 0xD5) : Color.FromRgb(0x0A, 0x0A, 0x0C));
        Color baseC = glass
            ? (light ? Color.FromArgb((byte)(255 * ga * 0.75), 0xFF, 0xFF, 0xFF) : Color.FromArgb((byte)(255 * ga * 0.8), 0x0A, 0x0B, 0x10))
            : _gapC;
        _textC = light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Colors.White;
        _text = Frozen(_textC);
        _muted = Frozen(glass ? (light ? Color.FromRgb(0x55, 0x55, 0x5C) : Color.FromRgb(0xC4, 0xC4, 0xCC)) : Color.FromRgb(0x8E, 0x8E, 0x93));

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        UseLayoutRounding = true;
        Width = Height = 2 * _c;
        WindowStartupLocation = WindowStartupLocation.Manual;
        PlaceUnderCursor();

        var root = new Grid
        {
            Width = 2 * _c,
            Height = 2 * _c,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };

        // The shadow lives on its own still element that is drawn once as a picture. A live shadow effect on the
        // animated ring would be recalculated (in software) on every frame.
        var shadow = new Ellipse
        {
            Width = 2 * Outer,
            Height = 2 * Outer,
            Fill = Frozen(baseC),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 4, Opacity = light ? 0.28 : 0.55, Color = Colors.Black },
            CacheMode = new BitmapCache(1.5)
        };
        root.Children.Add(shadow);
        var scale = new ScaleTransform(0.86, 0.86);
        root.RenderTransform = scale;
        root.Opacity = 0;

        _host.Width = _host.Height = 2 * _c;
        _host.RenderTransformOrigin = new Point(0.5, 0.5);
        _host.RenderTransform = _hostScale;
        root.Children.Add(_host);

        // the middle of the ring
        double hr = Inner - 5;
        var hub = new Ellipse
        {
            Width = 2 * hr,
            Height = 2 * hr,
            Fill = Frozen(glass ? (light ? Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB4, 0x0B, 0x0C, 0x10)) : (light ? Colors.White : Color.FromRgb(0x0B, 0x0B, 0x0D))),
            Stroke = Frozen(glass ? Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF) : (light ? Color.FromRgb(0xDD, 0xD9, 0xD5) : Color.FromRgb(0x2A, 0x2A, 0x30))),
            StrokeThickness = 2,
            Cursor = pages.Count > 1 ? Cursors.Hand : Cursors.Arrow,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        hub.MouseLeftButtonUp += (_, e) => { e.Handled = true; Flip(1); };
        root.Children.Add(hub);

        _titleTx.Text = title;
        _titleTx.FontSize = title.Length > 6 ? 13 : 18;
        _titleTx.FontWeight = FontWeights.Bold;
        _titleTx.Foreground = _text;
        _titleTx.HorizontalAlignment = HorizontalAlignment.Center;
        _subTx.Text = _idleText;
        _subTx.FontSize = 9.5;
        _subTx.Foreground = _muted;
        _subTx.HorizontalAlignment = HorizontalAlignment.Center;
        _pageTx.FontSize = 9.5;
        _pageTx.FontWeight = FontWeights.SemiBold;
        _pageTx.Foreground = Frozen(accent);
        _pageTx.HorizontalAlignment = HorizontalAlignment.Center;
        _hubText.IsHitTestVisible = false;
        _hubText.VerticalAlignment = VerticalAlignment.Center;
        _hubText.HorizontalAlignment = HorizontalAlignment.Center;
        if (fileIcon != null)
            _hubText.Children.Add(new Image { Source = fileIcon, Width = 20, Height = 20, Margin = new Thickness(0, 0, 0, 1), HorizontalAlignment = HorizontalAlignment.Center });
        _hubText.Children.Add(_titleTx);
        _hubText.Children.Add(_subTx);
        _hubText.Children.Add(_pageTx);
        root.Children.Add(_hubText);

        _hubFx.IsHitTestVisible = false;
        _hubFx.Width = _hubFx.Height = 2 * hr;
        _hubFx.Opacity = 0;
        root.Children.Add(_hubFx);

        Content = root;
        BuildPage(0, true);

        ContentRendered += (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease });
            root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        };

        Loaded += (_, _) => GlassWindow.ForceForeground(this);
        // A ring opened by holding a key belongs to that key: it closes when the key is no longer held (this is also the
        // way out when Windows never gave the ring focus, e.g. over Chrome). Other rings close when they lose focus.
        if (_stillHeld != null)
        {
            _heldWatch.Tick += (_, _) =>
            {
                if (_busy || _closing) return;
                bool held = false;
                try { held = _stillHeld(); } catch { }
                if (!held) ReleaseClose();
            };
            _heldWatch.Start();
        }
        else
        {
            Deactivated += (_, _) => { if (!_busy) CloseOnce(); };
        }
        PreviewKeyDown += OnKey;
        PreviewMouseWheel += (_, e) => { e.Handled = true; Flip(e.Delta < 0 ? 1 : -1); };

        // if Windows refused to give us focus, the ring still goes away by itself
        _safety.Tick += (_, _) =>
        {
            _safety.Stop();
            if (_busy) return;
            if (!IsMouseOver) CloseOnce();
            else _safety.Start();
        };
        _safety.Start();
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ---------- Slices ----------

    private void BuildPage(int page, bool first)
    {
        _page = page;
        _host.Children.Clear();
        _groups.Clear();
        List<ActionDef> acts = _pages[page];
        int n = acts.Count;
        double span = 360.0 / PerRing(n);
        _pageTx.Text = _pages.Count > 1 ? (page + 1) + " / " + _pages.Count : "";
        _pageTx.Visibility = _pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _subTx.Text = _pages.Count > 1 ? "Tab for more" : _idleText;

        for (int i = 0; i < n; i++)
        {
            double a0 = i * span, a1 = (i + 1) * span;
            double mid = (a0 + a1) / 2 * Math.PI / 180.0;
            double dx = Math.Sin(mid), dy = -Math.Cos(mid);
            ActionDef def = acts[i];

            var group = new Canvas { Width = 2 * _c, Height = 2 * _c, Opacity = 0 };
            var open = new TranslateTransform(-dx * 26, -dy * 26);
            var hover = new TranslateTransform();
            var tg = new TransformGroup();
            tg.Children.Add(open);
            tg.Children.Add(hover);
            group.RenderTransform = tg;

            var fill = new SolidColorBrush(_fillC);
            var shape = new Path
            {
                Data = Sector(_c, _c, Inner, Outer, a0, a1),
                Fill = fill,
                Stroke = Frozen(_gapC),
                StrokeThickness = 3.5,
                StrokeLineJoin = PenLineJoin.Round,
                Cursor = Cursors.Hand
            };
            group.Children.Add(shape);

            double rm = (Inner + Outer) / 2 + 1;
            double x = _c + rm * Math.Sin(mid), y = _c - rm * Math.Cos(mid);
            var iconBrush = new SolidColorBrush(_textC);
            FrameworkElement iconEl = def.Img != null
                ? new Image { Source = def.Img, Width = 24, Height = 24, IsHitTestVisible = false }
                : Icons.Make(def.Icon, 20, iconBrush, 1.8);
            iconEl.HorizontalAlignment = HorizontalAlignment.Center;
            var labelBrush = new SolidColorBrush(_textC);
            var label = new TextBlock
            {
                Text = def.Label,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = labelBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0)
            };
            var stack = new StackPanel { Width = 62, IsHitTestVisible = false };
            stack.Children.Add(iconEl);
            stack.Children.Add(label);
            stack.Measure(new Size(62, 80));
            Canvas.SetLeft(stack, x - 31);
            Canvas.SetTop(stack, y - stack.DesiredSize.Height / 2);
            group.Children.Add(stack);

            // small dot on slices the user pinned to the front page
            var dot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = Frozen(_accent),
                IsHitTestVisible = false,
                Visibility = _isPinned != null && def.Fmt != null && _isPinned(def) ? Visibility.Visible : Visibility.Collapsed
            };
            Canvas.SetLeft(dot, _c + (Outer - 9) * Math.Sin(mid) - 2.5);
            Canvas.SetTop(dot, _c - (Outer - 9) * Math.Cos(mid) - 2.5);
            group.Children.Add(dot);

            int idx = i;
            shape.MouseEnter += (_, _) =>
            {
                if (_busy) return;
                _hovered = idx;
                fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(_accent, TimeSpan.FromMilliseconds(90)));
                iconBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(_onAccent, TimeSpan.FromMilliseconds(90)));
                labelBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(_onAccent, TimeSpan.FromMilliseconds(90)));
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                hover.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx * 7, TimeSpan.FromMilliseconds(110)) { EasingFunction = ease });
                hover.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy * 7, TimeSpan.FromMilliseconds(110)) { EasingFunction = ease });
                Panel.SetZIndex(group, 5);
            };
            shape.MouseLeave += (_, _) =>
            {
                if (_hovered == idx) _hovered = -1;
                if (_busy) return;
                fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(_fillC, TimeSpan.FromMilliseconds(130)));
                iconBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(_textC, TimeSpan.FromMilliseconds(130)));
                labelBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(_textC, TimeSpan.FromMilliseconds(130)));
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                hover.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease });
                hover.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease });
                Panel.SetZIndex(group, 0);
            };
            shape.MouseLeftButtonUp += (_, e) => { e.Handled = true; Pick(idx); };
            shape.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (_busy) return;
                if (_remove != null && def.Id.StartsWith("app:", StringComparison.Ordinal))
                {
                    string? done = _remove(def);
                    if (done != null)
                    {
                        Say(done);
                        shape.IsHitTestVisible = false;
                        group.Opacity = 0.2;
                        _hovered = -1;
                    }
                    return;
                }
                if (_togglePin == null || def.Fmt == null) return;
                bool now = _togglePin(def);
                dot.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
                Say(now ? "Pinned " + def.Label : "Unpinned " + def.Label);
            };

            _host.Children.Add(group);
            _groups.Add(group);

            // flies in from the middle, one slice after the other
            var begin = TimeSpan.FromMilliseconds((first ? 40 : 15) * i + (first ? 30 : 0));
            var dur = TimeSpan.FromMilliseconds(first ? 230 : 170);
            var ease2 = new CubicEase { EasingMode = EasingMode.EaseOut };
            group.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { BeginTime = begin });
            open.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-dx * 26, 0, dur) { BeginTime = begin, EasingFunction = ease2 });
            open.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-dy * 26, 0, dur) { BeginTime = begin, EasingFunction = ease2 });
        }
    }

    // A ring with fewer slices than a full page still closes into a full circle
    private static int PerRing(int n) => Math.Max(n, 3);

    private DispatcherTimer? _sayTimer;
    private void Say(string text)
    {
        _subTx.Text = text;
        _sayTimer?.Stop();
        _sayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _sayTimer.Tick += (_, _) =>
        {
            _sayTimer!.Stop();
            if (!_busy) _subTx.Text = _pages.Count > 1 ? "Tab for more" : _idleText;
        };
        _sayTimer.Start();
    }

    // ---------- Pages ----------

    private void Flip(int dir)
    {
        if (_busy || _closing || _flipping || _pages.Count < 2) return;
        _flipping = true;
        _hovered = -1;
        int next = ((_page + dir) % _pages.Count + _pages.Count) % _pages.Count;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var a = new DoubleAnimation(1, 0.04, TimeSpan.FromMilliseconds(95)) { EasingFunction = ease };
        a.Completed += (_, _) =>
        {
            if (_closing) return;
            BuildPage(next, false);
            var b = new DoubleAnimation(0.04, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            b.Completed += (_, _) => _flipping = false;
            _hostScale.BeginAnimation(ScaleTransform.ScaleXProperty, b);
        };
        _hostScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
    }

    // ---------- Picking and progress ----------

    private void Pick(int i)
    {
        if (_closing || _busy || _flipping || i < 0 || i >= _pages[_page].Count) return;
        ActionDef def = _pages[_page][i];
        RingResult? res;
        try { res = _pick(def); } catch { res = null; }
        if (res == null) { CloseOnce(); return; }
        _ = RunAsync(i, res);
    }

    private async Task RunAsync(int picked, RingResult res)
    {
        _busy = true;
        _safety.Stop();
        _sayTimer?.Stop();

        // the other slices fade away, the chosen one stays
        for (int i = 0; i < _groups.Count; i++)
        {
            if (i == picked) continue;
            _groups[i].IsHitTestVisible = false;
            _groups[i].BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(170)));
        }
        _hubText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));

        // the middle becomes a progress ring
        double hr = Inner - 5, r = hr - 9;
        var track = new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = Frozen(Color.FromArgb(0x30, _textC.R, _textC.G, _textC.B)), StrokeThickness = 4 };
        var arc = new Path
        {
            Stroke = Frozen(_accent),
            StrokeThickness = 4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 2 * hr,
            Height = 2 * hr
        };
        var rot = new RotateTransform(0, hr, hr);
        arc.RenderTransform = rot;
        arc.Data = Arc(hr, hr, r, 0, 110);
        rot.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(950)) { RepeatBehavior = RepeatBehavior.Forever });
        _hubFx.Children.Clear();
        _hubFx.Children.Add(track);
        _hubFx.Children.Add(arc);
        _hubFx.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)) { BeginTime = TimeSpan.FromMilliseconds(100) });

        bool determinate = false;
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        poll.Tick += (_, _) =>
        {
            double p;
            try { p = res.Progress(); } catch { p = -1; }
            if (p <= 0 || p >= 1) return;
            if (!determinate)
            {
                determinate = true;
                rot.BeginAnimation(RotateTransform.AngleProperty, null);
                rot.Angle = 0;
            }
            arc.Data = Arc(hr, hr, r, 0, Math.Max(10, 360 * p));
        };
        poll.Start();

        bool ok;
        try { ok = await res.Done; } catch { ok = false; }
        poll.Stop();
        if (_closing) return;

        // result: a green check (or a red cross) pops
        rot.BeginAnimation(RotateTransform.AngleProperty, null);
        _hubFx.Children.Clear();
        var badge = new Grid { Width = 2 * (hr - 6), Height = 2 * (hr - 6), RenderTransformOrigin = new Point(0.5, 0.5) };
        var sc = new ScaleTransform(0.2, 0.2);
        badge.RenderTransform = sc;
        badge.Children.Add(new Ellipse { Fill = Frozen(ok ? Color.FromRgb(0x34, 0xC7, 0x59) : Color.FromRgb(0xE5, 0x48, 0x4D)) });
        FrameworkElement mark = Icons.Make(ok ? "check" : "close", 26, Brushes.White, 3);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        mark.VerticalAlignment = VerticalAlignment.Center;
        badge.Children.Add(mark);
        _hubFx.Children.Add(badge);
        var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = pop });
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = pop });

        await Task.Delay(ok ? 750 : 1400);
        if (_closing) return;
        CloseOnce();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseOnce();
            return;
        }
        if (_busy) return;

        if (e.Key == Key.Tab)
        {
            e.Handled = true;
            Flip(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            return;
        }
        if (e.Key == Key.Right || e.Key == Key.Down) { e.Handled = true; Flip(1); return; }
        if (e.Key == Key.Left || e.Key == Key.Up) { e.Handled = true; Flip(-1); return; }

        int digit = e.Key >= Key.D1 && e.Key <= Key.D9 ? e.Key - Key.D1
                  : e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;
        if (digit >= 0 && digit < _pages[_page].Count)
        {
            e.Handled = true;
            Pick(digit);
        }
    }

    /// <summary>The ring key was let go: the ring disappears (unless it is working; then it closes when done).</summary>
    public bool IsClosing => _closing;
    public bool IsBusy => _busy;

    public void ReleaseClose()
    {
        if (_busy || _closing) return;
        if (_releasePicks && _hovered >= 0 && _hovered < _pages[_page].Count) { Pick(_hovered); return; }
        CloseOnce();
    }

    public void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        _safety.Stop();
        _heldWatch.Stop();
        _sayTimer?.Stop();
        try { Close(); } catch { }
    }

    // Ring segment between two radii. Angles are in degrees, clockwise, 0 = 12 o'clock.
    private static Geometry Sector(double cx, double cy, double r0, double r1, double a0, double a1)
    {
        static Point P(double cx, double cy, double r, double deg)
        {
            double rad = deg * Math.PI / 180.0;
            return new Point(cx + r * Math.Sin(rad), cy - r * Math.Cos(rad));
        }

        bool big = a1 - a0 > 180;
        var fig = new PathFigure { StartPoint = P(cx, cy, r1, a0), IsClosed = true };
        fig.Segments.Add(new ArcSegment(P(cx, cy, r1, a1), new Size(r1, r1), 0, big, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(P(cx, cy, r0, a1), true));
        fig.Segments.Add(new ArcSegment(P(cx, cy, r0, a0), new Size(r0, r0), 0, big, SweepDirection.Counterclockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        g.Freeze();
        return g;
    }

    // An open arc (the progress ring)
    private static Geometry Arc(double cx, double cy, double r, double a0, double sweep)
    {
        sweep = Math.Min(sweep, 359.5);
        double s = a0 * Math.PI / 180.0, e = (a0 + sweep) * Math.PI / 180.0;
        var fig = new PathFigure { StartPoint = new Point(cx + r * Math.Sin(s), cy - r * Math.Cos(s)), IsClosed = false };
        fig.Segments.Add(new ArcSegment(new Point(cx + r * Math.Sin(e), cy - r * Math.Cos(e)), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }

    // ---------- Position: centered on the cursor, kept on the screen ----------

    private void PlaceUnderCursor()
    {
        try
        {
            GetCursorPos(out POINT pt);
            IntPtr mon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            double scale = GetDpiForSystem() / 96.0;
            if (scale < 0.5) scale = 1;
            double size = Width * scale;
            double x = pt.X - size / 2, y = pt.Y - size / 2;
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
            {
                x = Math.Clamp(x, mi.rcWork.Left, Math.Max(mi.rcWork.Left, mi.rcWork.Right - size));
                y = Math.Clamp(y, mi.rcWork.Top, Math.Max(mi.rcWork.Top, mi.rcWork.Bottom - size));
            }
            Left = x / scale;
            Top = y / scale;
        }
        catch
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
}
