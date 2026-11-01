using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LiteIsland;

// Mixer page: one volume slider for every program that has sound (like the Windows volume mixer, but in the island).
// The audio sessions are only read while this page is open, and released when you leave it.
public partial class MainWindow
{
    private List<MixApp> _mixApps = new();
    private DispatcherTimer? _mixTimer;
    private bool _mixDragging;
    private string _mixSig = "";

    private static string MixSignature(List<MixApp> l) => string.Join(",", l.Select(a => a.Pid + (a.Active ? "a" : "i")));

    private void ReleaseMixer()
    {
        foreach (var a in _mixApps) a.Release();
        _mixApps = new List<MixApp>();
        _mixSig = "";
    }

    private void BuildMixer()
    {
        if (MixerHost == null) return;
        if (_mixDragging) return;
        ReleaseMixer();
        _mixApps = AudioMixer.Query();
        _mixSig = MixSignature(_mixApps);

        MixerHost.Children.Clear();
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new Grid { Height = 20, Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(new TextBlock { Text = "Mixer", Style = (Style)FindResource("PageTitle") });
        root.Children.Add(head);

        if (_mixApps.Count == 0)
        {
            var hint = new TextBlock { Text = "No app is using sound right now", Style = (Style)FindResource("Hint") };
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(hint, 1);
            root.Children.Add(hint);
        }
        else
        {
            var list = new StackPanel();
            foreach (MixApp a in _mixApps) list.Children.Add(MixerRow(a));
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list };
            Grid.SetRow(sv, 1);
            root.Children.Add(sv);
        }
        MixerHost.Children.Add(root);

        _mixTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _mixTimer.Tick -= OnMixTick;
        _mixTimer.Tick += OnMixTick;
        _mixTimer.Start();
    }

    private void OnMixTick(object? s, EventArgs e)
    {
        if (!_expanded || _view != ViewMixer)
        {
            _mixTimer?.Stop();
            ReleaseMixer();
            return;
        }
        if (_mixDragging) return;
        // look for new / closed programs without rebuilding while nothing changed
        var fresh = AudioMixer.Query();
        string sig = MixSignature(fresh);
        foreach (var a in fresh) a.Release();
        if (sig != _mixSig) BuildMixer();
    }

    private UIElement MixerRow(MixApp app)
    {
        var row = new Grid { Height = 36, Margin = new Thickness(0, 0, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });

        // icon (click = mute)
        var iconHost = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Click to mute" };
        UIElement icon;
        ImageSource? img = app.Path != null ? IconHelper.Get(app.Path) : null;
        if (img != null) icon = new Image { Source = img, Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        else { var f = Icons.Make("audio", 18); f.HorizontalAlignment = HorizontalAlignment.Center; f.VerticalAlignment = VerticalAlignment.Center; icon = f; }
        iconHost.Child = (UIElement)icon;
        row.Children.Add(iconHost);

        var name = Txt(app.Name, 12.5, true);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        // slider
        var hit = new Grid { Height = 28, Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Tag = "slider" };
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3) };
        track.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        var fill = new Border { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
        fill.SetResourceReference(Border.BackgroundProperty, "FillBrush");
        hit.Children.Add(track);
        hit.Children.Add(fill);
        Grid.SetColumn(hit, 2);
        row.Children.Add(hit);

        var pct = Txt("", 11.5, false, true);
        pct.HorizontalAlignment = HorizontalAlignment.Right;
        pct.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(pct, 3);
        row.Children.Add(pct);

        void Show()
        {
            double w = Math.Max(0, hit.ActualWidth);
            fill.Width = app.Muted ? 0 : w * app.Volume;
            pct.Text = app.Muted ? "Muted" : Math.Round(app.Volume * 100).ToString(CultureInfo.InvariantCulture) + "%";
            iconHost.Opacity = app.Muted ? 0.35 : 1;
        }
        void SetFromX(double x)
        {
            double w = Math.Max(1, hit.ActualWidth);
            app.SetVolume((float)Math.Clamp(x / w, 0, 1));
            Show();
        }
        hit.SizeChanged += (_, _) => Show();
        hit.MouseLeftButtonDown += (_, e) => { _mixDragging = true; hit.CaptureMouse(); SetFromX(e.GetPosition(hit).X); e.Handled = true; };
        hit.MouseMove += (_, e) => { if (hit.IsMouseCaptured) SetFromX(e.GetPosition(hit).X); };
        hit.MouseLeftButtonUp += (_, e) => { hit.ReleaseMouseCapture(); _mixDragging = false; e.Handled = true; };
        hit.LostMouseCapture += (_, _) => _mixDragging = false;
        hit.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            app.SetVolume(app.Volume + (e.Delta > 0 ? 0.05f : -0.05f));
            Show();
        };
        iconHost.MouseLeftButtonUp += (_, e) => { e.Handled = true; app.SetMute(!app.Muted); Show(); };
        iconHost.MouseEnter += (_, _) => iconHost.Background = HoverBrush;
        iconHost.MouseLeave += (_, _) => iconHost.Background = Brushes.Transparent;
        Show();
        return row;
    }
}
