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
/// A black tab that grows out of the left or right edge of the SCREEN (not out of the island). Left: volume and
/// brightness sliders. Right: the focus modes. Its size follows the island (length = collapsed island width,
/// thickness = collapsed island height + a little), so there is nothing to set. The window is hidden while idle.
/// </summary>
public sealed class EdgeLobe : Window
{
    private readonly bool _left;
    private readonly FontFamily _glyph;
    private readonly Grid _root = new();
    private readonly TranslateTransform _shift = new();
    private readonly DispatcherTimer _timer = new();
    private readonly double _len, _thick, _flare;
    private bool _shown, _inside;
    private int _drag = -1; // which slider is being dragged (left tab)

    // left tab: two sliders
    private readonly Border?[] _fill = new Border?[2];
    private readonly TextBlock?[] _icon = new TextBlock?[2];
    private readonly Grid?[] _box = new Grid?[2];
    private readonly double[] _val = new double[2];
    private double _slH, _slW;
    private const double SlGap = 8;

    // right tab: focus modes
    private readonly List<(Border Dot, TextBlock Glyph)> _dots = new();
    private TextBlock? _label;
    private readonly TranslateTransform _labelY = new();
    private double _pitch;
    private int _sel;
    private Grid? _face;

    public Action<double>? SetVolume, SetBrightness;
    public Action<int>? Pick;

    public double Length => _len;
    public double Thickness => _thick;
    public bool IsShown => _shown;
    public bool IsInside => _inside || _drag >= 0;

    private const double LabelRoom = 150;

    public EdgeLobe(bool left, FontFamily glyphFont, double length, double thickness)
    {
        _left = left;
        _glyph = glyphFont;
        _len = Math.Max(200, length);
        _thick = Math.Max(60, thickness);
        _flare = 40;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        Width = _thick + (left ? 6 : LabelRoom);
        Height = _len;
        Content = _root;
        _root.RenderTransform = _shift;
        _root.Opacity = 0;

        // the black shape: straight along the screen edge, an S-curve out to the face, an S-curve back
        double P = _thick, H = _len, f = _flare;
        string d = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"M 0,0 C 0,{f * 0.55} {P},{f * 0.45} {P},{f} L {P},{H - f} C {P},{H - f * 0.45} 0,{H - f * 0.55} 0,{H} Z");
        var path = new Path { Data = Geometry.Parse(d), Fill = new SolidColorBrush(Color.FromRgb(0x05, 0x05, 0x06)), Stretch = Stretch.None };
        _face = new Grid { Width = P, Height = H, HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right };
        _face.Children.Add(path);
        if (!left) { path.RenderTransformOrigin = new Point(0.5, 0.5); path.RenderTransform = new ScaleTransform(-1, 1); }
        _root.Children.Add(_face);

        if (left) BuildSliders();

        MouseEnter += (_, _) => { _inside = true; _timer.Stop(); };
        MouseLeave += (_, _) => { _inside = false; if (_drag < 0) Arm(900); };
        _timer.Tick += (_, _) => { _timer.Stop(); Retract(); };
        SourceInitialized += (_, _) =>
        {
            IntPtr hw = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hw, -20);
            SetWindowLong(hw, -20, ex | 0x08000000 | 0x00000080 | 0x00000008); // NOACTIVATE | TOOLWINDOW | TOPMOST
        };
    }

    // ---------- left tab ----------
    private void BuildSliders()
    {
        double body = _len - 2 * _flare;
        _slH = Math.Max(90, body - 26);
        _slW = Math.Max(24, (_thick - 20 - SlGap) / 2);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        for (int i = 0; i < 2; i++)
        {
            int k = i;
            var box = new Grid { Width = _slW, Height = _slH, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(i == 0 ? 0 : SlGap, 0, 0, 0) };
            box.Clip = new RectangleGeometry(new Rect(0, 0, _slW, _slH), Math.Min(14, _slW / 2), Math.Min(14, _slW / 2));
            box.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)) });
            var fill = new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = 0, Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7)), IsHitTestVisible = false };
            box.Children.Add(fill);
            var icon = new TextBlock { FontFamily = _glyph, FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0x6C, 0x70)),
                                       HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 9), IsHitTestVisible = false,
                                       Text = i == 0 ? "" : "" };
            box.Children.Add(icon);
            _fill[i] = fill; _icon[i] = icon; _box[i] = box;

            box.MouseLeftButtonDown += (_, e) => { _drag = k; _timer.Stop(); box.CaptureMouse(); Drag(k, e); e.Handled = true; };
            box.MouseMove += (_, e) => { if (_drag == k) Drag(k, e); };
            box.MouseLeftButtonUp += (_, e) => { if (_drag != k) return; Drag(k, e); _drag = -1; box.ReleaseMouseCapture(); if (!_inside) Arm(900); e.Handled = true; };
            box.MouseWheel += (_, e) => { Commit(k, Math.Clamp(_val[k] + (e.Delta > 0 ? 0.05 : -0.05), 0, 1)); e.Handled = true; };
            row.Children.Add(box);
        }
        _face!.Children.Add(row);
    }

    private void Drag(int k, MouseEventArgs e) => Commit(k, Math.Clamp(1 - e.GetPosition(_box[k]).Y / _slH, 0, 1));

    private void Commit(int k, double v)
    {
        Paint(k, v, null);
        if (k == 0) SetVolume?.Invoke(v); else SetBrightness?.Invoke(v);
    }

    private void Paint(int k, double v, string? glyph)
    {
        _val[k] = v;
        if (_fill[k] != null) _fill[k]!.Height = _slH * v;
        if (glyph != null && _icon[k] != null) _icon[k]!.Text = glyph;
    }

    /// <summary>Shows the tab with the current volume and (when the screen has it) brightness.</summary>
    public void ShowLevels(double? volume, string volGlyph, double? brightness, int ms)
    {
        if (_drag != 0 && volume != null) Paint(0, Math.Clamp(volume.Value, 0, 1), volGlyph);
        if (_box[1] != null)
        {
            if (_drag != 1 && brightness != null) { Paint(1, Math.Clamp(brightness.Value, 0, 1), null); _box[1]!.Opacity = 1; }
            else if (brightness == null && _drag != 1) _box[1]!.Opacity = 0.35; // no brightness control on this screen
        }
        Present(ms);
    }

    // ---------- right tab ----------
    public void ShowModes(IList<(string Glyph, Color Col, string Name)> modes, int sel, int ms)
    {
        if (_dots.Count == 0 && _face != null)
        {
            double body = _len - 2 * _flare;
            _pitch = Math.Clamp((body - 8) / modes.Count, 30, 46);
            double dot = _pitch - 6;
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, (_thick - dot) / 2, 0), Background = Brushes.Transparent };
            for (int i = 0; i < modes.Count; i++)
            {
                var b = new Border { Width = dot, Height = dot, CornerRadius = new CornerRadius(dot / 2), Margin = new Thickness(0, 3, 0, 3), Cursor = Cursors.Hand, Background = Brushes.Transparent };
                var g = new TextBlock { FontFamily = _glyph, FontSize = Math.Max(14, dot * 0.52), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
                b.Child = g;
                int idx = i;
                b.MouseLeftButtonUp += (_, e) => { Pick?.Invoke(idx); e.Handled = true; };
                col.Children.Add(b);
                _dots.Add((b, g));
            }
            col.MouseWheel += (_, e) => { Pick?.Invoke(Math.Clamp(_sel + (e.Delta > 0 ? -1 : 1), 0, modes.Count - 1)); e.Handled = true; };
            _face.Children.Add(col);

            _label = new TextBlock { FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left,
                                     VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 0, 0, 0), IsHitTestVisible = false, RenderTransform = _labelY,
                                     Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.7, Color = Colors.Black } };
            _root.Children.Add(_label);
        }

        for (int i = 0; i < modes.Count && i < _dots.Count; i++)
        {
            _dots[i].Glyph.Text = modes[i].Glyph;
            _dots[i].Glyph.Foreground = new SolidColorBrush(modes[i].Col);
            _dots[i].Dot.Opacity = i == sel ? 1 : 0.42;
        }
        if (_label != null)
        {
            _label.Text = modes[Math.Clamp(sel, 0, modes.Count - 1)].Name.ToUpperInvariant();
            double top = _len / 2 - modes.Count * (_pitch) / 2 + sel * _pitch + _pitch / 2 - 10;
            if (!_shown) _labelY.Y = top;
            else _labelY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(top, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        _sel = sel;
        Present(ms);
    }

    // ---------- show / hide ----------
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
            double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
            Left = _left ? 0 : sw - Width;
            Top = Math.Max(0, (sh - Height) / 2);
            Show();
            _shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(_left ? -_thick : _thick, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        }
        if (!IsInside) Arm(ms);
    }

    private void Retract()
    {
        if (!_shown) return;
        _shown = false;
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
        var x = new DoubleAnimation(_left ? -_thick : _thick, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        x.Completed += (_, _) => { if (!_shown) Hide(); };
        _shift.BeginAnimation(TranslateTransform.XProperty, x);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr h, int i, int v);
}
