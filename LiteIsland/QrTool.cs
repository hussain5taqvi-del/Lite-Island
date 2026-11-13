using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LiteIsland;

/// <summary>Draws a QR code in a few styles (the matrix comes from ZXing, the picture is drawn here).</summary>
internal static class QrStyle
{
    public static readonly string[] StyleNames = { "Classic", "Rounded", "Dots" };
    public static readonly (string Name, string A, string B)[] Palettes =
    {
        ("Ink", "#111111", "#111111"),
        ("Ocean", "#0A84FF", "#5E5CE6"),
        ("Sunset", "#FF9F0A", "#FF375F"),
        ("Forest", "#30D158", "#0A84FF"),
    };

    public static BitmapSource? Render(string text, int style, int palette, int px)
    {
        ZXing.Common.BitMatrix m;
        try
        {
            var hints = new Dictionary<ZXing.EncodeHintType, object>
            {
                { ZXing.EncodeHintType.MARGIN, 0 },
                { ZXing.EncodeHintType.ERROR_CORRECTION, ZXing.QrCode.Internal.ErrorCorrectionLevel.Q }
            };
            m = new ZXing.QrCode.QRCodeWriter().encode(text, ZXing.BarcodeFormat.QR_CODE, 0, 0, hints);
        }
        catch { return null; }

        int n = m.Width;
        const double quiet = 2.5;
        double u = px / (n + quiet * 2);
        double o = quiet * u;

        var fg = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(px, px)
        };
        fg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(Palettes[palette].A), 0));
        fg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(Palettes[palette].B), 1));
        fg.Freeze();

        bool Finder(int x, int y) => (x < 7 && y < 7) || (x >= n - 7 && y < 7) || (x < 7 && y >= n - 7);

        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(0, 0, px, px), px * 0.07, px * 0.07);

            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    if (!m[x, y] || Finder(x, y)) continue;
                    double cx = o + x * u, cy = o + y * u;
                    if (style == 0) dc.DrawRectangle(fg, null, new Rect(cx, cy, u + 0.6, u + 0.6));
                    else if (style == 1) dc.DrawRoundedRectangle(fg, null, new Rect(cx + u * 0.04, cy + u * 0.04, u * 0.92, u * 0.92), u * 0.34, u * 0.34);
                    else dc.DrawEllipse(fg, null, new Point(cx + u / 2, cy + u / 2), u * 0.43, u * 0.43);
                }

            void Eye(int ex, int ey)
            {
                double x0 = o + ex * u, y0 = o + ey * u;
                if (style == 0)
                {
                    var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
                    g.Children.Add(new RectangleGeometry(new Rect(x0, y0, 7 * u, 7 * u)));
                    g.Children.Add(new RectangleGeometry(new Rect(x0 + u, y0 + u, 5 * u, 5 * u)));
                    dc.DrawGeometry(fg, null, g);
                    dc.DrawRectangle(fg, null, new Rect(x0 + 2 * u, y0 + 2 * u, 3 * u, 3 * u));
                }
                else
                {
                    double ro = style == 1 ? 2.1 * u : 3.5 * u, ri = style == 1 ? 1.3 * u : 2.5 * u, rc = style == 1 ? 0.9 * u : 1.5 * u;
                    var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
                    g.Children.Add(new RectangleGeometry(new Rect(x0, y0, 7 * u, 7 * u), ro, ro));
                    g.Children.Add(new RectangleGeometry(new Rect(x0 + u, y0 + u, 5 * u, 5 * u), ri, ri));
                    dc.DrawGeometry(fg, null, g);
                    dc.DrawRoundedRectangle(fg, null, new Rect(x0 + 2 * u, y0 + 2 * u, 3 * u, 3 * u), rc, rc);
                }
            }
            Eye(0, 0); Eye(n - 7, 0); Eye(0, n - 7);
        }

        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}

/// <summary>QR code maker: type or paste text or a link, pick a style, copy or save the picture.</summary>
internal sealed class QrWindow : ToolShell
{
    private readonly TextBox _in = new();
    private readonly TextBlock _hint, _status;
    private readonly Image _pic = new();
    private readonly DispatcherTimer _deb = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly List<Border> _styleBtns = new();
    private readonly List<Ellipse> _palDots = new();
    private int _style = 1, _pal = 1;
    private BitmapSource? _last;

    public QrWindow() : base("QR Code", 380)
    {
        _in.Background = Brushes.Transparent;
        _in.BorderThickness = new Thickness(0);
        _in.Foreground = Kit.Text;
        _in.CaretBrush = Kit.Accent;
        _in.SelectionBrush = Kit.Accent;
        _in.FontSize = 14.5;
        _in.AcceptsReturn = true;
        _in.TextWrapping = TextWrapping.Wrap;
        _in.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _in.Padding = new Thickness(0);
        _in.TextChanged += (_, _) => { _hint.Visibility = _in.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; _deb.Stop(); _deb.Start(); };

        _hint = Kit.T("Type text or paste a link", 14.5, false, Kit.Faint);
        _hint.IsHitTestVisible = false;
        var field = new Grid { Height = 56 };
        field.Children.Add(_hint);
        field.Children.Add(_in);
        var fieldBox = new Border { Background = Kit.Fill, CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 11, 16, 11), Child = field };

        _pic.Width = 236; _pic.Height = 236;
        _pic.Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(_pic, BitmapScalingMode.HighQuality);
        var picBox = new Border
        {
            Width = 236, Height = 236, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0),
            CornerRadius = new CornerRadius(20), Background = Kit.Fill, Child = _pic, ClipToBounds = true
        };
        picBox.Clip = new RectangleGeometry(new Rect(0, 0, 236, 236), 20, 20);

        // style chips
        var styleRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        for (int i = 0; i < QrStyle.StyleNames.Length; i++)
        {
            int idx = i;
            var b = Kit.Button(null, QrStyle.StyleNames[i], () => { _style = idx; Mark(); Draw(); }, false, 30);
            b.Margin = new Thickness(3, 0, 3, 0);
            _styleBtns.Add(b);
            styleRow.Children.Add(b);
        }
        styleRow.Children.Add(new Border { Width = 1, Height = 18, Background = Kit.Sep, Margin = new Thickness(8, 0, 8, 0) });
        for (int i = 0; i < QrStyle.Palettes.Length; i++)
        {
            int idx = i;
            var pal = QrStyle.Palettes[i];
            var dot = new Ellipse
            {
                Width = 20, Height = 20, StrokeThickness = 2.4, Stroke = Brushes.Transparent,
                Fill = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(pal.A), (Color)ColorConverter.ConvertFromString(pal.B), 45)
            };
            var hit = new Border { Width = 28, Height = 30, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = dot, ToolTip = pal.Name };
            hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; _pal = idx; Mark(); Draw(); };
            Kit.Press(hit, 0.88);
            _palDots.Add(dot);
            styleRow.Children.Add(hit);
        }

        var actions = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var copy = Kit.Button("copy", "Copy image", CopyImage, true, 38);
        var save = Kit.Button("download", "Save", SaveImage, false, 38);
        Grid.SetColumn(save, 2);
        actions.Children.Add(copy);
        actions.Children.Add(save);

        _status = Kit.T("", 12, false, Kit.Dim);
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.Height = 16;

        var stack = new StackPanel();
        stack.Children.Add(fieldBox);
        stack.Children.Add(picBox);
        stack.Children.Add(styleRow);
        stack.Children.Add(actions);
        stack.Children.Add(_status);
        Body.Children.Add(stack);

        _deb.Tick += (_, _) => { _deb.Stop(); Draw(); };
        Mark();
    }

    protected override void OnShown()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string t = Clipboard.GetText().Trim();
                if (t.Length > 0 && t.Length <= 600) _in.Text = t;
            }
        }
        catch { }
        if (_in.Text.Length == 0) Draw();
        _in.Focus();
        _in.SelectAll();
    }

    private void Mark()
    {
        for (int i = 0; i < _styleBtns.Count; i++)
        {
            _styleBtns[i].Background = i == _style ? Kit.Accent : Kit.Fill;
            if (_styleBtns[i].Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock tb)
                tb.Foreground = i == _style ? Kit.OnAccent : Kit.Text;
        }
        for (int i = 0; i < _palDots.Count; i++) _palDots[i].Stroke = i == _pal ? Kit.Text : Brushes.Transparent;
    }

    private void Draw()
    {
        string text = _in.Text.Trim();
        _status.Text = "";
        if (text.Length == 0) text = "LiteIsland"; // a sample until something is typed
        _last = QrStyle.Render(text, _style, _pal, 1024);
        if (_last == null) { _status.Text = "That is too long for a QR code"; return; }
        _pic.Source = _last;
    }

    private void CopyImage()
    {
        if (_last == null || _in.Text.Trim().Length == 0) { _status.Text = "Type something first"; return; }
        _status.Text = Kit.SetClipImage(_last) ? "Copied" : "Could not copy";
    }

    private void SaveImage()
    {
        if (_last == null || _in.Text.Trim().Length == 0) { _status.Text = "Type something first"; return; }
        try
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LiteIsland");
            Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, "QR " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(_last));
            using (var fs = File.Create(file)) enc.Save(fs);
            _status.Text = "Saved in Pictures \\ LiteIsland";
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = true }); } catch { }
        }
        catch { _status.Text = "Could not save"; }
    }
}
