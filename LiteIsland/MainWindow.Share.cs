using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LiteIsland;

// "Send to phone" inside the island: dragging a file over the island opens a drop zone (only while dragging),
// dropping it turns the drop zone into a QR code that the phone scans.
public partial class MainWindow
{
    private ShareServer? _share;
    private Grid? _dropHint, _shareCard;
    private TextBlock? _shTitle, _shName, _shStatus;
    private Border? _zoneL, _zoneR;
    private ScaleTransform? _zoneLScale, _zoneRScale;
    private Grid? _flashBox;
    private TextBlock? _flashText;
    private int _dropSide = -1; // 0 = phone (left), 1 = shelf (right)
    private Border? _shFill;
    private Image? _shQr;
    private DispatcherTimer? _shareTimer;
    private ScaleTransform? _cardScale;
    private bool _dropShown;
    private long _shLastSent, _shLastTime, _shDoneAt;
    private double _shSpeed;
    private int _flashToken;

    private const double ShareBarW = 190;

    private void EnsureDropUi()
    {
        if (_dropHint != null) return;
        DropLayer.Background = Brushes.Transparent;

        // ---- drop zones: left = send to phone, right = shelf (only visible while a file is dragged over the island) ----
        _dropHint = new Grid { Margin = new Thickness(12, 8, 12, 8), Opacity = 0 };
        _dropHint.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _dropHint.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _dropHint.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _dropHint.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _zoneL = DropZone("phone", "Send to phone", "QR code", out _zoneLScale);
        _zoneR = DropZone("shelf", "Shelf", "keep it here", out _zoneRScale);
        _zoneL.Margin = new Thickness(0, 0, 5, 0);
        _zoneR.Margin = new Thickness(5, 0, 0, 0);
        Grid.SetColumn(_zoneR, 1);
        _dropHint.Children.Add(_zoneL);
        _dropHint.Children.Add(_zoneR);
        var shiftHint = Txt("Hold Shift to add it to the apps ring", 10.5, false, true);
        shiftHint.HorizontalAlignment = HorizontalAlignment.Center;
        shiftHint.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(shiftHint, 1);
        Grid.SetColumnSpan(shiftHint, 2);
        _dropHint.Children.Add(shiftHint);
        DropLayer.Children.Add(_dropHint);

        // ---- short message ("Added to the shelf") ----
        _flashBox = new Grid { Opacity = 0, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var fsp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var check = Icons.Make("check", 34, null, 1.8);
        check.HorizontalAlignment = HorizontalAlignment.Center;
        fsp.Children.Add(check);
        _flashText = Txt("", 14, true);
        _flashText.HorizontalAlignment = HorizontalAlignment.Center;
        _flashText.Margin = new Thickness(0, 8, 0, 0);
        fsp.Children.Add(_flashText);
        _flashBox.Children.Add(fsp);
        DropLayer.Children.Add(_flashBox);

        // ---- QR card ----
        _shareCard = new Grid { Margin = new Thickness(14, 6, 14, 6), Opacity = 0, Visibility = Visibility.Collapsed, RenderTransformOrigin = new Point(0.5, 0.5) };
        _cardScale = new ScaleTransform(0.9, 0.9);
        _shareCard.RenderTransform = _cardScale;
        _shareCard.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _shareCard.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var qrBox = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(7), VerticalAlignment = VerticalAlignment.Center, Width = 136, Height = 136 };
        _shQr = new Image { SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(_shQr, BitmapScalingMode.NearestNeighbor);
        qrBox.Child = _shQr;
        _shareCard.Children.Add(qrBox);

        var right = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _shTitle = Txt("Scan to download", 14.5, true);
        right.Children.Add(_shTitle);
        _shName = Txt("", 11.5, false, true);
        _shName.TextTrimming = TextTrimming.CharacterEllipsis;
        _shName.Margin = new Thickness(0, 2, 0, 0);
        right.Children.Add(_shName);

        var track = new Border { Height = 4, Width = ShareBarW, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        track.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        var fillHost = new Grid { Width = ShareBarW, HorizontalAlignment = HorizontalAlignment.Left };
        _shFill = new Border { Height = 4, Width = 0, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
        _shFill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        track.Child = _shFill;
        right.Children.Add(track);

        _shStatus = Txt("Waiting for your phone", 11, false, true);
        _shStatus.Margin = new Thickness(0, 6, 0, 0);
        right.Children.Add(_shStatus);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        btns.Children.Add(Pill("Copy link", () => { if (_share != null) { SetClipboardText(_share.Url); if (_shStatus != null) _shStatus.Text = "Link copied"; } }));
        var stop = Pill("Stop", () => StopShare());
        stop.Margin = new Thickness(6, 0, 0, 0);
        btns.Children.Add(stop);
        right.Children.Add(btns);
        Grid.SetColumn(right, 1);
        _shareCard.Children.Add(right);
        DropLayer.Children.Add(_shareCard);

        _shareTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _shareTimer.Tick += (_, _) => ShareTick();
    }

    private Border Pill(string text, Action click)
    {
        var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 4, 11, 4), Cursor = Cursors.Hand };
        b.SetResourceReference(Border.BackgroundProperty, "TileBg");
        b.Child = Txt(text, 11, true);
        b.MouseEnter += (_, _) => b.Opacity = 0.8;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    private void Fade(UIElement e, double to, int ms, Action? done = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (done != null) a.Completed += (_, _) => done();
        e.BeginAnimation(OpacityProperty, a);
    }

    private Border DropZone(string icon, string title, string sub, out ScaleTransform scale)
    {
        var z = new Border { CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1.5), Opacity = 0.6, RenderTransformOrigin = new Point(0.5, 0.5) };
        z.SetResourceReference(Border.BackgroundProperty, "TileBg");
        z.SetResourceReference(Border.BorderBrushProperty, "TileStroke");
        scale = new ScaleTransform(1, 1);
        z.RenderTransform = scale;
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var ic = Icons.Make(icon, 34, null, 1.6);
        ic.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(ic);
        var t = Txt(title, 13.5, true);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(t);
        var u = Txt(sub, 11, false, true);
        u.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(u);
        z.Child = sp;
        return z;
    }

    /// <summary>Lights up the zone under the pointer (0 = phone, 1 = shelf).</summary>
    private void SetDropSide(int side)
    {
        if (side == _dropSide || _zoneL == null || _zoneR == null) return;
        _dropSide = side;
        HighlightZone(_zoneL, _zoneLScale!, side == 0);
        HighlightZone(_zoneR, _zoneRScale!, side == 1);
    }

    private void HighlightZone(Border z, ScaleTransform sc, bool on)
    {
        z.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0.55, TimeSpan.FromMilliseconds(160)));
        z.SetResourceReference(Border.BorderBrushProperty, on ? "AccentBrush" : "TileStroke");
        var a = new DoubleAnimation(on ? 1.03 : 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new SpringEase { Damping = 0.6 } };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>Shows or hides the drop zones. The pages and dots are covered only while they (or a QR code) are shown.</summary>
    private void ShowDropHint(bool on)
    {
        EnsureDropUi();
        _flashToken++;
        if (on)
        {
            _dropShown = true;
            _dropSide = -1;
            SetDropSide(0);
            _flashBox!.Visibility = Visibility.Collapsed;
            _flashBox.Opacity = 0;
            DropLayer.Visibility = Visibility.Visible;
            Fade(DropLayer, 1, 160);
            Fade(Pages, 0, 140);
            Fade(Dots, 0, 140);
            _dropHint!.Visibility = Visibility.Visible;
            Fade(_dropHint, 1, 150);
            if (_share != null) Fade(_shareCard!, 0, 100);
        }
        else
        {
            _dropShown = false;
            Fade(_dropHint!, 0, 130, () => { if (!_dropShown) _dropHint!.Visibility = Visibility.Collapsed; });
            if (_share != null) Fade(_shareCard!, 1, 180);
            else HideDropLayer();
        }
    }

    private void HideDropLayer()
    {
        Fade(Pages, 1, 180);
        Fade(Dots, 1, 180);
        Fade(DropLayer, 0, 170, () =>
        {
            if (!_dropShown && _share == null) DropLayer.Visibility = Visibility.Collapsed;
        });
    }

    private void FlashDrop(string text)
    {
        EnsureDropUi();
        int token = ++_flashToken;
        _dropShown = false;
        _flashText!.Text = text;
        if (DropLayer.Visibility != Visibility.Visible)
        {
            DropLayer.Visibility = Visibility.Visible;
            Fade(DropLayer, 1, 160);
            Fade(Pages, 0, 140);
            Fade(Dots, 0, 140);
        }
        Fade(_dropHint!, 0, 100, () => { if (token == _flashToken) _dropHint!.Visibility = Visibility.Collapsed; });
        _flashBox!.Visibility = Visibility.Visible;
        Fade(_flashBox, 1, 160);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (token != _flashToken) return;
            Fade(_flashBox!, 0, 140, () => { if (token == _flashToken) _flashBox!.Visibility = Visibility.Collapsed; });
            if (_share == null) HideDropLayer();
        };
        t.Start();
    }

    private void OnDragActive()
    {
        _leaveTimer.Stop();
        if (!_expanded) Animate(true);
        if (!_dropShown) ShowDropHint(true);
    }

    private void StartShare(string[] files)
    {
        EnsureDropUi();
        _flashToken++;
        try { _share?.Dispose(); } catch { }
        _share = null;
        try { _share = new ShareServer(files); }
        catch { FlashDrop("Could not share this"); return; }

        _shQr!.Source = ShareQr.Make(_share.Url);
        _shName!.Text = _share.Items.Count == 1 ? _share.Items[0].Name : _share.Items.Count + " files";
        _shTitle!.Text = "Scan to download";
        _shFill!.Width = 0;
        _shStatus!.Text = "Waiting for your phone  ·  " + ShareServer.Size(_share.Total);
        _shLastSent = 0;
        _shLastTime = Environment.TickCount64;
        _shSpeed = 0;
        _shDoneAt = 0;

        if (!_expanded) Animate(true);
        DropLayer.Visibility = Visibility.Visible;
        Fade(DropLayer, 1, 160);
        Fade(Pages, 0, 140);
        Fade(Dots, 0, 140);

        // the drop zones fade away and the QR code springs in
        _dropShown = false;
        Fade(_dropHint!, 0, 200, () => { if (!_dropShown) _dropHint!.Visibility = Visibility.Collapsed; });
        _flashBox!.Visibility = Visibility.Collapsed;

        _shareCard!.Visibility = Visibility.Visible;
        _cardScale!.ScaleX = _cardScale.ScaleY = 0.88;
        var spring = new SpringEase { Damping = 0.72 };
        var grow = new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(620)) { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = spring };
        _cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        _shareCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(120) });

        _holdUntil = Math.Max(_holdUntil, Environment.TickCount64 + 120000);
        _shareTimer!.Start();
    }

    private void StopShare()
    {
        _shareTimer?.Stop();
        try { _share?.Dispose(); } catch { }
        _share = null;
        if (_shareCard != null)
            Fade(_shareCard, 0, 160, () => { if (_share == null) _shareCard!.Visibility = Visibility.Collapsed; });
        if (!_dropShown) HideDropLayer();

        // let the island close again (it was held open while sharing)
        _holdUntil = Environment.TickCount64 + 1500;
        if (!Island.IsMouseOver && !_holdOpen)
        {
            _leaveTimer.Stop();
            _leaveTimer.Interval = TimeSpan.FromMilliseconds(1600);
            _leaveTimer.Start();
        }
    }

    private void ShareTick()
    {
        var s = _share;
        if (s == null) { _shareTimer?.Stop(); return; }
        if (s.Idle) { StopShare(); return; }

        long sent = s.Sent, now = Environment.TickCount64;
        double dt = (now - _shLastTime) / 1000.0;
        if (dt > 0.25)
        {
            double inst = (sent - _shLastSent) / dt;
            _shSpeed = _shSpeed <= 0 ? inst : _shSpeed * 0.7 + inst * 0.3;
            _shLastSent = sent;
            _shLastTime = now;
        }

        if (s.Connections > 0) _holdUntil = Math.Max(_holdUntil, now + 15000);
        if (sent <= 0 && s.Connections == 0) return;

        double frac = s.Total > 0 ? Math.Min(1, (double)sent / s.Total) : 0;
        _shFill!.Width = ShareBarW * frac;
        if (frac >= 0.999 && s.Connections == 0)
        {
            _shTitle!.Text = "Sent";
            _shStatus!.Text = ShareServer.Size(s.Total) + " delivered";
            if (_shDoneAt == 0) _shDoneAt = now;
            else if (now - _shDoneAt > 6000) StopShare();
        }
        else
        {
            _shDoneAt = 0;
            _shTitle!.Text = "Sending...";
            _shStatus!.Text = ShareServer.Size(Math.Min(sent, s.Total)) + " of " + ShareServer.Size(s.Total) + (_shSpeed > 1024 ? "  ·  " + ShareServer.Size((long)_shSpeed) + "/s" : "");
        }
    }
}
