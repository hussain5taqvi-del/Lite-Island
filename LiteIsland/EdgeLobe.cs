using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LiteIsland;

/// <summary>
/// A black tab that grows out of the left or right edge of the SCREEN (not out of the island): the left one shows
/// volume / brightness as a tall slider, the right one the focus modes. The window only exists while it is shown.
/// </summary>
public sealed class EdgeLobe : Window
{
    private readonly bool _left;
    private readonly FontFamily _glyph;
    private readonly Grid _root = new();
    private readonly TranslateTransform _shift = new();
    private readonly DispatcherTimer _timer = new();
    private bool _shown, _inside, _drag;

    // left lobe
    private Border? _fill;
    private Grid? _box;
    private TextBlock? _icon;
    private const double SlW = 50, SlH = 176;

    // right lobe
    private readonly List<(Border Dot, TextBlock Glyph)> _dots = new();
    private TextBlock? _label;
    private readonly TranslateTransform _labelY = new();
    private const double Pitch = 46;

    /// <summary>The person moved the slider (0..1).</summary>
    public Action<double>? Set;
    /// <summary>The person picked a focus mode.</summary>
    public Action<int>? Pick;

    private const double P = 86;          // how far the tab sticks out
    private double _h;                    // total height
    private double _baseW, _sx = 1, _sy = 1;

    public EdgeLobe(bool left, FontFamily glyphFont, int items)
    {
        _left = left;
        _glyph = glyphFont;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SnapsToDevicePixels = true;

        double flare = left ? 82 : 70;
        double body = left ? SlH + 34 : items * Pitch + 18;
        _h = body + flare * 2;
        _baseW = left ? P + 24 : P + 190;
        Width = _baseW;
        Height = _h;
        Content = _root;
        _root.RenderTransform = _shift;
        _root.Opacity = 0;
        _root.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        _root.Width = P + (left ? 0 : 190);

        // the black shape: straight along the screen edge, an S-curve out to the face, an S-curve back
        double h = flare, H = _h;
        string d = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"M 0,0 C 0,{h * 0.55} {P},{h * 0.45} {P},{h} L {P},{H - h} C {P},{H - h * 0.45} 0,{H - h * 0.55} 0,{H} Z");
        var path = new Path { Data = Geometry.Parse(d), Fill = new SolidColorBrush(Color.FromRgb(0x05, 0x05, 0x06)), Stretch = Stretch.None, SnapsToDevicePixels = false };
        var face = new Grid { Width = P, Height = H, HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right };
        face.Children.Add(path);
        if (!left) { path.RenderTransformOrigin = new Point(0.5, 0.5); path.RenderTransform = new ScaleTransform(-1, 1); }
        _root.Children.Add(face);

        if (left) BuildSlider(face); else _faceRight = face;

        MouseEnter += (_, _) => { _inside = true; _timer.Stop(); };
        MouseLeave += (_, _) => { _inside = false; if (!_drag) Arm(900); };
        _timer.Tick += (_, _) => { _timer.Stop(); Close2(); };
        SourceInitialized += (_, _) =>
        {
            IntPtr h2 = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(h2, -20);
            SetWindowLong(h2, -20, ex | 0x08000000 | 0x00000080 | 0x00000008); // NOACTIVATE | TOOLWINDOW | TOPMOST
        };
    }

    private Grid? _faceRight;

    /// <summary>Width and height of the tab in percent (one setting for both tabs).</summary>
    public void Rescale(double wPct, double hPct)
    {
        double sx = Math.Clamp(wPct, 50, 200) / 100, sy = Math.Clamp(hPct, 50, 200) / 100;
        if (Math.Abs(sx - _sx) < 0.001 && Math.Abs(sy - _sy) < 0.001) return;
        _sx = sx; _sy = sy;
        _root.LayoutTransform = new ScaleTransform(sx, sy);
        Width = _baseW * sx;
        Height = _h * sy;
        if (_shown)
        {
            double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
            Left = _left ? 0 : sw - Width;
            Top = Math.Max(0, (sh - Height) / 2);
        }
    }

    public bool IsShown => _shown;
    public bool IsInside => _inside || _drag;

    private void BuildSlider(Grid face)
    {
        _box = new Grid { Width = SlW, Height = SlH, Background = Brushes.Transparent, Cursor = Cursors.Hand,
                          HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        _box.Clip = new RectangleGeometry(new Rect(0, 0, SlW, SlH), 17, 17);
        _box.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)) });
        _fill = new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = 0, Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7)), IsHitTestVisible = false };
        _box.Children.Add(_fill);
        _icon = new TextBlock { FontFamily = _glyph, FontSize = 17, Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0x6C, 0x70)),
                                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 12), IsHitTestVisible = false };
        _box.Children.Add(_icon);
        face.Children.Add(_box);

        _box.MouseLeftButtonDown += (_, e) => { _drag = true; _timer.Stop(); _box.CaptureMouse(); Move(e); e.Handled = true; };
        _box.MouseMove += (_, e) => { if (_drag) Move(e); };
        _box.MouseLeftButtonUp += (_, e) => { if (!_drag) return; _drag = false; _box.ReleaseMouseCapture(); Move(e); if (!_inside) Arm(900); e.Handled = true; };
        _box.MouseWheel += (_, e) => { Apply(Math.Clamp(_value + (e.Delta > 0 ? 0.05 : -0.05), 0, 1)); e.Handled = true; };
    }

    private double _value;
    private string _iconGlyph = "";

    private void Move(MouseEventArgs e) => Apply(Math.Clamp(1 - e.GetPosition(_box).Y / SlH, 0, 1));

    private void Apply(double v)
    {
        Level(v, _iconGlyph);
        Set?.Invoke(v);
    }

    private void Level(double v, string glyph)
    {
        _value = v;
        if (_fill != null) _fill.Height = SlH * v;
        if (_icon != null) _icon.Text = glyph;
    }

    /// <summary>Left lobe: show a level (volume, brightness) for a moment.</summary>
    public void ShowLevel(string glyph, double value, int ms)
    {
        _iconGlyph = glyph;
        if (!_drag) Level(Math.Clamp(value, 0, 1), glyph);
        Present(ms);
    }

    /// <summary>Right lobe: show the focus modes with the chosen one lit and named.</summary>
    public void ShowModes(IList<(string Glyph, Color Col, string Name)> modes, int sel, int ms)
    {
        if (_faceRight == null) return;
        if (_dots.Count == 0)
        {
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0), Background = Brushes.Transparent };
            for (int i = 0; i < modes.Count; i++)
            {
                var dot = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Margin = new Thickness(0, 4, 0, 4), Cursor = Cursors.Hand, Background = Brushes.Transparent };
                var g = new TextBlock { FontFamily = _glyph, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
                dot.Child = g;
                int idx = i;
                dot.MouseLeftButtonUp += (_, e) => { Pick?.Invoke(idx); e.Handled = true; };
                col.Children.Add(dot);
                _dots.Add((dot, g));
            }
            _faceRight.Children.Add(col);
            col.MouseWheel += (_, e) => { Pick?.Invoke(Math.Clamp(_sel + (e.Delta > 0 ? -1 : 1), 0, modes.Count - 1)); e.Handled = true; };

            _label = new TextBlock { FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right,
                                     VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, P + 16, 0), IsHitTestVisible = false, RenderTransform = _labelY,
                                     Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.7, Color = Colors.Black } };
            _root.Children.Add(_label);
        }

        for (int i = 0; i < modes.Count && i < _dots.Count; i++)
        {
            bool on = i == sel;
            _dots[i].Glyph.Text = modes[i].Glyph;
            _dots[i].Glyph.Foreground = new SolidColorBrush(modes[i].Col);
            _dots[i].Dot.Opacity = on ? 1 : 0.42;
        }
        if (_label != null)
        {
            _label.Text = modes[Math.Clamp(sel, 0, modes.Count - 1)].Name.ToUpperInvariant();
            // the name sits next to the lit icon (icons are centered vertically in the tab)
            double top = _h / 2 - modes.Count * Pitch / 2 + sel * Pitch + Pitch / 2 - 10 - 4;
            if (!_shown) _labelY.Y = top; else _labelY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(top, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        _sel = sel;
        Present(ms);
    }

    private int _sel;

    private void Arm(int ms)
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(ms);
        _timer.Start();
    }

    private void Present(int ms)
    {
        if (!_shown)
        {
            _shown = true;
            // vertically centered on the screen, glued to the left or right edge (primary screen)
            double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
            Left = _left ? 0 : sw - Width;
            Top = Math.Max(0, (sh - Height) / 2);
            Show();
            _shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation((_left ? -P : P) * _sx, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        }
        if (!_inside && !_drag) Arm(ms);
    }

    private void Close2()
    {
        if (!_shown) return;
        _shown = false;
        var o = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        _root.BeginAnimation(OpacityProperty, o);
        var x = new DoubleAnimation((_left ? -P : P) * _sx, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        x.Completed += (_, _) => { if (!_shown) Hide(); };
        _shift.BeginAnimation(TranslateTransform.XProperty, x);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr h, int i, int v);
}
