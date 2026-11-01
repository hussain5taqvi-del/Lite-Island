using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LiteIsland;

// Timer page: a thin progress ring with the time inside, six presets, Start / Pause and Reset.
// Built in code when the page is shown; it costs nothing while the page is hidden. The countdown itself keeps
// running (one light timer) so the notification still comes when you are on another page.
public partial class MainWindow
{
    private const double RingBox = 116, RingStroke = 5;

    private DispatcherTimer? _timerTick;
    private bool _timerRunning;
    private double _timerTotal = 300, _timerLeft = 300; // seconds
    private DateTime _timerEnd;
    private TextBlock? _tmText, _tmGo, _tmCaption;
    private Path? _tmArc;
    private Border? _tmGoBtn;

    private Border TimerChip(string text, bool selected, Action click)
    {
        var b = new Border { Height = 28, CornerRadius = new CornerRadius(14), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 0) };
        b.SetResourceReference(Border.BackgroundProperty, selected ? "FillBrush" : "TileBg");
        var t = Txt(text, 12.5, true);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.VerticalAlignment = VerticalAlignment.Center;
        if (selected) t.Foreground = OnFillBrush;
        b.Child = t;
        AddPress(b);
        if (!selected)
        {
            b.MouseEnter += (_, _) => b.Background = HoverBrush;
            b.MouseLeave += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "TileBg");
        }
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    private void BuildTimer()
    {
        if (TimerHost == null) return;
        TimerHost.Children.Clear();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new Grid { Height = 20, Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(new TextBlock { Text = "Timer", Style = (Style)FindResource("PageTitle") });
        root.Children.Add(head);

        var body = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // ---- ring with the time inside ----
        var ring = new Grid { Width = RingBox, Height = RingBox, Margin = new Thickness(2, 0, 22, 0), VerticalAlignment = VerticalAlignment.Center };
        var track = new Ellipse { Width = RingBox - RingStroke - 2, Height = RingBox - RingStroke - 2, StrokeThickness = RingStroke, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        track.SetResourceReference(Shape.StrokeProperty, "TrackBrush");
        ring.Children.Add(track);
        _tmArc = new Path
        {
            Width = RingBox, Height = RingBox, StrokeThickness = RingStroke,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false
        };
        _tmArc.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        ring.Children.Add(_tmArc);
        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _tmText = Txt("5:00", 28, true);
        _tmText.HorizontalAlignment = HorizontalAlignment.Center;
        mid.Children.Add(_tmText);
        _tmCaption = Txt("", 10.5, false, true);
        _tmCaption.HorizontalAlignment = HorizontalAlignment.Center;
        mid.Children.Add(_tmCaption);
        ring.Children.Add(mid);
        body.Children.Add(ring);

        // ---- presets + buttons ----
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var presets = new UniformGrid { Columns = 3, Rows = 2 };
        foreach (int m in new[] { 1, 5, 10, 15, 25, 45 })
        {
            int mm = m;
            bool sel = Math.Abs(_timerTotal - mm * 60) < 0.5;
            Border chip = TimerChip(mm + " min", sel, () => SetTimer(mm * 60));
            chip.Margin = new Thickness(0, 0, 6, 6);
            presets.Children.Add(chip);
        }
        right.Children.Add(presets);

        var actions = new Grid { Margin = new Thickness(0, 6, 6, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _tmGo = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _tmGoBtn = new Border { Height = 36, CornerRadius = new CornerRadius(18), Cursor = Cursors.Hand, Child = _tmGo };
        AddPress(_tmGoBtn);
        _tmGoBtn.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleTimer(); };
        actions.Children.Add(_tmGoBtn);

        Border reset = RoundBtn("plus", "Reset", ResetTimer, false, 36);
        reset.Margin = new Thickness(8, 0, 0, 0);
        reset.RenderTransformOrigin = new Point(0.5, 0.5);
        ((FrameworkElement)reset.Child).RenderTransform = new RotateTransform(45);
        ((FrameworkElement)reset.Child).RenderTransformOrigin = new Point(0.5, 0.5);
        Grid.SetColumn(reset, 1);
        actions.Children.Add(reset);
        right.Children.Add(actions);

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
        if (_tmText == null || _tmArc == null || _tmGo == null || _tmGoBtn == null || _tmCaption == null) return;
        double left = _timerRunning ? Math.Max(0, (_timerEnd - DateTime.Now).TotalSeconds) : _timerLeft;
        bool started = left < _timerTotal - 0.5 && left > 0.5;
        _tmText.Text = FmtTimer(left);
        _tmCaption.Text = _timerRunning ? "running" : started ? "paused" : "";
        _tmGo.Text = _timerRunning ? "Pause" : started ? "Resume" : "Start";
        if (_timerRunning)
        {
            _tmGoBtn.SetResourceReference(Border.BackgroundProperty, "TileBg");
            _tmGo.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        }
        else
        {
            _tmGoBtn.SetResourceReference(Border.BackgroundProperty, "FillBrush");
            _tmGo.Foreground = OnFillBrush;
        }

        double frac = _timerTotal > 0 ? Math.Clamp(left / _timerTotal, 0, 1) : 0;
        double c = RingBox / 2, r = (RingBox - RingStroke - 2) / 2;
        if (frac <= 0.002) { _tmArc.Data = Geometry.Empty; return; }
        if (frac >= 0.998)
        {
            string f = c.ToString("0.##", CultureInfo.InvariantCulture), top = (c - r).ToString("0.##", CultureInfo.InvariantCulture), rr = r.ToString("0.##", CultureInfo.InvariantCulture);
            _tmArc.Data = Geometry.Parse("M " + f + "," + top + " A " + rr + "," + rr + " 0 1 1 " + (c - 0.01).ToString("0.##", CultureInfo.InvariantCulture) + "," + top);
            return;
        }
        double ang = frac * 2 * Math.PI;
        double x = c + r * Math.Sin(ang), y = c - r * Math.Cos(ang);
        var fig = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        fig.Segments.Add(new ArcSegment(new Point(x, y), new Size(r, r), 0, ang > Math.PI, SweepDirection.Clockwise, true));
        _tmArc.Data = new PathGeometry(new[] { fig });
    }
}
