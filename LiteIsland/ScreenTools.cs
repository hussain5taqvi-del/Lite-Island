using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace LiteIsland;

/// <summary>One monitor: where it is (screen pixels) and a frozen picture of it.</summary>
internal sealed class Mon
{
    public int X, Y, W, H;
    public byte[] Px = Array.Empty<byte>(); // BGRA, W * 4 bytes per row
    public BitmapSource Shot = null!;
}

internal static class ScreenNative
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint ClrUsed, ClrImportant;
    }

    public delegate bool MonitorEnumProc(IntPtr mon, IntPtr hdc, ref RECT rc, IntPtr data);

    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

    public static List<Mon> Monitors()
    {
        var list = new List<Mon>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT rc, IntPtr d) =>
        {
            var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(h, ref mi))
                list.Add(new Mon { X = mi.Monitor.L, Y = mi.Monitor.T, W = mi.Monitor.R - mi.Monitor.L, H = mi.Monitor.B - mi.Monitor.T });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Copies the pixels of the monitor from the screen (also what a game or a video player shows).</summary>
    public static void Capture(Mon m)
    {
        IntPtr sdc = GetDC(IntPtr.Zero);
        IntPtr mdc = CreateCompatibleDC(sdc);
        var bi = new BITMAPINFOHEADER { Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), Width = m.W, Height = -m.H, Planes = 1, BitCount = 32 };
        IntPtr hbm = CreateDIBSection(sdc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mdc, hbm);
        BitBlt(mdc, 0, 0, m.W, m.H, sdc, m.X, m.Y, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
        m.Px = new byte[m.W * m.H * 4];
        Marshal.Copy(bits, m.Px, 0, m.Px.Length);
        for (int i = 3; i < m.Px.Length; i += 4) m.Px[i] = 255;
        SelectObject(mdc, old);
        DeleteObject(hbm);
        DeleteDC(mdc);
        ReleaseDC(IntPtr.Zero, sdc);
        m.Shot = BitmapSource.Create(m.W, m.H, 96, 96, PixelFormats.Bgra32, null, m.Px, m.W * 4);
        m.Shot.Freeze();
    }
}

/// <summary>
/// Screen tools: Text (copy text from the screen), Color picker, Draw, Lens search, Font finder.
/// Every one freezes all screens first (a picture of each monitor) and works on that picture.
/// </summary>
internal static class ScreenTools
{
    public enum Mode { Text, Color, Draw, Lens, Font }

    private static readonly List<Overlay> Wins = new();
    private static Mode _mode;
    private static bool _busy, _closing;

    public static void Start(Mode mode)
    {
        _mode = mode;
        var mons = ScreenNative.Monitors();
        if (mons.Count == 0) { Application.Current.Shutdown(); return; }
        foreach (Mon m in mons) ScreenNative.Capture(m);

        ScreenNative.GetCursorPos(out ScreenNative.POINT cp);
        Mon? home = null;
        foreach (Mon m in mons)
            if (cp.X >= m.X && cp.X < m.X + m.W && cp.Y >= m.Y && cp.Y < m.Y + m.H) home = m;
        home ??= mons[0];

        foreach (Mon m in mons)
        {
            var o = new Overlay(m, mode, ReferenceEquals(m, home));
            Wins.Add(o);
            o.Show();
        }
        foreach (Overlay o in Wins)
            if (o.IsHome) GlassWindow.ForceForeground(o);
    }

    public static void CloseAll(int delayMs = 0)
    {
        if (_closing) return;
        _closing = true;
        void Do()
        {
            foreach (Overlay o in Wins) { try { o.Close(); } catch { } }
            Wins.Clear();
            Application.Current?.Shutdown();
        }
        if (delayMs <= 0) { Do(); return; }
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        t.Tick += (_, _) => { t.Stop(); Do(); };
        t.Start();
    }

    // ---------- region picked (Text, Lens, Font) ----------

    public static async void Picked(Overlay o, Int32Rect r)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            switch (_mode)
            {
                case Mode.Text:
                    o.Banner(Working("Reading text…"));
                    byte[] png = await Task.Run(() => RegionPng(o.M, r, true));
                    string text = Clean(await WinRtOps.OcrBytesAsync(WinRtOps.Engine(), png));
                    if (text.Length == 0)
                    {
                        o.Banner(Result("warn", "No text found", "Try a bigger area", false));
                        CloseAll(1500);
                        return;
                    }
                    Kit.SetClipText(text);
                    int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                    o.Banner(Result("check", "Copied · " + words + (words == 1 ? " word" : " words"), Preview(text), true));
                    CloseAll(2300);
                    break;

                case Mode.Lens:
                case Mode.Font:
                    var bmp = RegionBitmap(o.M, r);
                    bool ok = Kit.SetClipImage(bmp);
                    string url = _mode == Mode.Lens ? "https://lens.google.com/" : "https://www.myfonts.com/pages/whatthefont";
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
                    o.Banner(Result(ok ? "check" : "warn",
                        ok ? "Picture copied" : "Could not copy the picture",
                        _mode == Mode.Lens ? "Press Ctrl + V on the Google Lens page" : "Paste or drop it on the WhatTheFont page", ok));
                    CloseAll(2600);
                    break;
            }
        }
        catch (Exception ex)
        {
            string msg = ex is InvalidOperationException ? ex.Message : "Something went wrong";
            o.Banner(Result("warn", "Could not read the text", msg, false));
            CloseAll(2600);
        }
    }

    private static string Clean(string t)
    {
        t = (t ?? "").Replace("\r", "").Trim();
        var lines = t.Split('\n');
        for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd();
        return string.Join("\n", lines).Trim();
    }

    private static string Preview(string t)
    {
        string one = t.Replace('\n', ' ');
        return one.Length > 90 ? one.Substring(0, 88).TrimEnd() + "…" : one;
    }

    private static byte[] Crop(Mon m, Int32Rect r, out int w, out int h)
    {
        w = Math.Max(1, Math.Min(r.Width, m.W - r.X));
        h = Math.Max(1, Math.Min(r.Height, m.H - r.Y));
        var buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(m.Px, ((r.Y + y) * m.W + r.X) * 4, buf, y * w * 4, w * 4);
        return buf;
    }

    private static BitmapSource RegionBitmap(Mon m, Int32Rect r)
    {
        byte[] buf = Crop(m, r, out int w, out int h);
        var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
        bs.Freeze();
        return bs;
    }

    /// <summary>PNG of the area for the text reader: dark pages are inverted and small areas are doubled (Windows reads dark-on-light text best).</summary>
    private static byte[] RegionPng(Mon m, Int32Rect r, bool forOcr)
    {
        byte[] buf = Crop(m, r, out int w, out int h);
        if (forOcr)
        {
            long sum = 0; int n = 0;
            for (int i = 0; i < buf.Length; i += 16) { sum += (buf[i] * 29 + buf[i + 1] * 150 + buf[i + 2] * 77) >> 8; n++; }
            if (n > 0 && sum / n < 112)
                for (int i = 0; i < buf.Length; i += 4) { buf[i] = (byte)(255 - buf[i]); buf[i + 1] = (byte)(255 - buf[i + 1]); buf[i + 2] = (byte)(255 - buf[i + 2]); }
        }
        BitmapSource src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
        if (forOcr && Math.Max(w, h) < 1100)
            src = new TransformedBitmap(src, new ScaleTransform(2, 2));
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // ---------- small banners ----------

    private static UIElement Working(string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var spin = Icons.Make("spinner", 18, Kit.Text, 2.2);
        spin.VerticalAlignment = VerticalAlignment.Center;
        var rot = new RotateTransform(0);
        spin.RenderTransform = rot;
        spin.RenderTransformOrigin = new Point(0.5, 0.5);
        rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800)) { RepeatBehavior = RepeatBehavior.Forever });
        sp.Children.Add(spin);
        var t = Kit.T(text, 14, true);
        t.Margin = new Thickness(10, 0, 0, 0);
        t.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(t);
        return sp;
    }

    private static UIElement Result(string icon, string title, string? sub, bool good)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var badge = new Grid { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        badge.Children.Add(new Ellipse { Fill = good ? Kit.Hex("#30D158") : Kit.Hex("#FF9F0A") });
        var ic = Icons.Make(icon, 14, Brushes.White, 2.6);
        ic.HorizontalAlignment = HorizontalAlignment.Center;
        ic.VerticalAlignment = VerticalAlignment.Center;
        badge.Children.Add(ic);
        sp.Children.Add(badge);
        var col = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360 };
        col.Children.Add(Kit.T(title, 14, true));
        if (!string.IsNullOrEmpty(sub))
        {
            var s = Kit.T(sub!, 12, false, Kit.Dim);
            s.TextWrapping = TextWrapping.Wrap;
            s.TextTrimming = TextTrimming.None;
            s.MaxHeight = 34;
            col.Children.Add(s);
        }
        sp.Children.Add(col);
        return sp;
    }

    // ---------- colors ----------

    public static readonly string[] ColorNames = { "HEX", "RGB", "HSL", "OKLCH" };

    public static string FormatColor(byte r, byte g, byte b, int fmt)
    {
        switch (fmt)
        {
            case 0: return "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
            case 1: return "rgb(" + r + ", " + g + ", " + b + ")";
            case 2:
                {
                    double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
                    double mx = Math.Max(rf, Math.Max(gf, bf)), mn = Math.Min(rf, Math.Min(gf, bf));
                    double l = (mx + mn) / 2, h = 0, s = 0, d = mx - mn;
                    if (d > 1e-9)
                    {
                        s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn);
                        if (mx == rf) h = (gf - bf) / d + (gf < bf ? 6 : 0);
                        else if (mx == gf) h = (bf - rf) / d + 2;
                        else h = (rf - gf) / d + 4;
                        h *= 60;
                    }
                    return "hsl(" + Math.Round(h) + ", " + Math.Round(s * 100) + "%, " + Math.Round(l * 100) + "%)";
                }
            default:
                {
                    double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
                    double rl = Lin(r / 255.0), gl = Lin(g / 255.0), bl = Lin(b / 255.0);
                    double l = Math.Cbrt(0.4122214708 * rl + 0.5363325363 * gl + 0.0514459929 * bl);
                    double m = Math.Cbrt(0.2119034982 * rl + 0.6806995451 * gl + 0.1073969566 * bl);
                    double s = Math.Cbrt(0.0883024619 * rl + 0.2817188376 * gl + 0.6299787005 * bl);
                    double L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
                    double A = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
                    double B = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
                    double C = Math.Sqrt(A * A + B * B);
                    double H = C < 0.0004 ? 0 : Math.Atan2(B, A) * 180 / Math.PI;
                    if (H < 0) H += 360;
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    return "oklch(" + (L * 100).ToString("0.#", ic) + "% " + C.ToString("0.###", ic) + " " + H.ToString("0.#", ic) + ")";
                }
        }
    }
}

/// <summary>A full-screen window showing the frozen picture of one monitor, with the tool on top.</summary>
internal sealed class Overlay : Window
{
    public readonly Mon M;
    public readonly bool IsHome;
    private readonly ScreenTools.Mode _mode;
    private readonly Grid _root = new();
    private readonly Image _img = new();
    private readonly Canvas _layer = new();
    private Border? _banner;

    // region mode
    private System.Windows.Shapes.Path? _dim;
    private Rectangle? _selRect;
    private Border? _sizeTag;
    private Point _start;
    private bool _dragging;

    // color mode
    private Grid? _loupe;
    private Image? _loupeImg;
    private Border? _info;
    private Ellipse? _swatch;
    private TextBlock? _value;
    private TextBlock? _fmtLine;
    private int _fmt;
    private bool _picked;
    private byte _cr, _cg, _cb;

    // draw mode
    private InkCanvas? _ink;
    private Border? _bar;
    private int _tool; // 0 pen, 1 highlighter, 2 eraser
    private Color _inkColor = Color.FromRgb(0xFF, 0x45, 0x3A);
    private readonly List<Border> _toolBtns = new();
    private readonly List<Ellipse> _colorDots = new();

    public Overlay(Mon m, ScreenTools.Mode mode, bool home)
    {
        M = m; _mode = mode; IsHome = home;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = false;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = m.X; Top = m.Y; Width = 200; Height = 200;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Cursor = mode == ScreenTools.Mode.Color ? Cursors.None : mode == ScreenTools.Mode.Draw ? Cursors.Arrow : Cursors.Cross;

        _img.Source = m.Shot;
        _img.Stretch = Stretch.Fill;
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.Linear);
        _root.Children.Add(_img);
        _root.Children.Add(_layer);
        Content = _root;

        SourceInitialized += (_, _) => Place();
        ContentRendered += (_, _) => { Place(); };
        KeyDown += OnKey;
        MouseRightButtonUp += (_, e) => { e.Handled = true; ScreenTools.CloseAll(); };

        BuildMode();
        Loaded += (_, _) => Kit.FadeIn(_layer, 160, 1.0);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(new Action(Place));
    }

    private void Place()
    {
        IntPtr h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        ScreenNative.SetWindowPos(h, new IntPtr(-1), M.X, M.Y, M.W, M.H, 0x0010);
    }

    private double Kx => ActualWidth > 0 ? M.W / ActualWidth : 1;
    private double Ky => ActualHeight > 0 ? M.H / ActualHeight : 1;

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; ScreenTools.CloseAll(); return; }
        if (_mode == ScreenTools.Mode.Color)
        {
            if (e.Key == Key.Tab || e.Key == Key.Right) { e.Handled = true; SetFmt(_fmt + 1); }
            else if (e.Key == Key.Left) { e.Handled = true; SetFmt(_fmt + 3); }
            else if (e.Key >= Key.D1 && e.Key <= Key.D4) { e.Handled = true; SetFmt(e.Key - Key.D1); }
            else if (e.Key == Key.Enter) { e.Handled = true; CopyColor(); }
        }
        else if (_mode == ScreenTools.Mode.Draw)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.Z) { e.Handled = true; Undo(); }
            else if (ctrl && e.Key == Key.C) { e.Handled = true; CopyDrawing(); }
        }
    }

    // ---------- banner (hint, progress, result) ----------

    public void Banner(UIElement content)
    {
        if (_banner != null) _layer.Children.Remove(_banner);
        _banner = Kit.Pill(content, 24, new Thickness(18, 11, 20, 11));
        _banner.IsHitTestVisible = false;
        _banner.HorizontalAlignment = HorizontalAlignment.Left;
        _layer.Children.Add(_banner);
        _banner.Opacity = 0;
        _banner.SizeChanged += (_, _) => PlaceBanner();
        PlaceBanner();
        Kit.FadeIn(_banner, 200, 0.94);
    }

    private void PlaceBanner()
    {
        if (_banner == null) return;
        double w = _banner.ActualWidth;
        Canvas.SetLeft(_banner, Math.Max(10, (ActualWidth - w) / 2));
        Canvas.SetTop(_banner, 44);
    }

    private void Hint(string text, string? keys = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(Kit.T(text, 14, true));
        if (keys != null)
        {
            var k = Kit.T(keys, 12, false, Kit.Dim);
            k.Margin = new Thickness(12, 0, 0, 0);
            k.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(k);
        }
        Banner(sp);
    }

    // ---------- build the tool ----------

    private void BuildMode()
    {
        switch (_mode)
        {
            case ScreenTools.Mode.Text:
            case ScreenTools.Mode.Lens:
            case ScreenTools.Mode.Font:
                BuildRegion();
                break;
            case ScreenTools.Mode.Color:
                BuildColor();
                break;
            case ScreenTools.Mode.Draw:
                BuildDraw();
                break;
        }
        if (IsHome)
        {
            Loaded += (_, _) =>
            {
                switch (_mode)
                {
                    case ScreenTools.Mode.Text: Hint("Drag over the text you want to copy", "Esc to cancel"); break;
                    case ScreenTools.Mode.Lens: Hint("Drag around what you want to search", "Esc to cancel"); break;
                    case ScreenTools.Mode.Font: Hint("Drag over the text to find its font", "Esc to cancel"); break;
                    case ScreenTools.Mode.Color: Hint("Click to copy the color", "Tab changes the format · Esc to cancel"); break;
                }
            };
        }
    }

    // ---------- region selection ----------

    private void BuildRegion()
    {
        _dim = new System.Windows.Shapes.Path { Fill = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), IsHitTestVisible = false };
        _selRect = new Rectangle
        {
            Stroke = Brushes.White, StrokeThickness = 1.6, RadiusX = 7, RadiusY = 7, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Opacity = 0.6, Color = Colors.Black }
        };
        var tagText = Kit.T("", 11.5, true);
        _sizeTag = Kit.Pill(tagText, 12, new Thickness(9, 3, 9, 3));
        _sizeTag.Visibility = Visibility.Collapsed;
        _sizeTag.IsHitTestVisible = false;
        _sizeTag.Tag = tagText;
        _layer.Children.Add(_dim);
        _layer.Children.Add(_selRect);
        _layer.Children.Add(_sizeTag);
        SizeChanged += (_, _) => UpdateDim(new Rect(0, 0, 0, 0));

        _root.Background = Brushes.Transparent;
        _root.MouseLeftButtonDown += (_, e) =>
        {
            _start = e.GetPosition(_root);
            _dragging = true;
            _root.CaptureMouse();
            e.Handled = true;
            UpdateSel(_start);
        };
        _root.MouseMove += (_, e) => { if (_dragging) UpdateSel(e.GetPosition(_root)); };
        _root.MouseLeftButtonUp += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            _root.ReleaseMouseCapture();
            Rect r = SelRect(e.GetPosition(_root));
            double kx = Kx, ky = Ky;
            var px = new Int32Rect((int)Math.Round(r.X * kx), (int)Math.Round(r.Y * ky), (int)Math.Round(r.Width * kx), (int)Math.Round(r.Height * ky));
            if (px.X < 0) { px.Width += px.X; px.X = 0; }
            if (px.Y < 0) { px.Height += px.Y; px.Y = 0; }
            if (px.Width < 8 || px.Height < 8) { UpdateDim(new Rect(0, 0, 0, 0)); _selRect!.Visibility = Visibility.Collapsed; _sizeTag!.Visibility = Visibility.Collapsed; return; }
            if (px.X + px.Width > M.W) px.Width = M.W - px.X;
            if (px.Y + px.Height > M.H) px.Height = M.H - px.Y;
            ScreenTools.Picked(this, px);
        };
        UpdateDim(new Rect(0, 0, 0, 0));
    }

    private Rect SelRect(Point p)
    {
        double x = Math.Max(0, Math.Min(_start.X, p.X)), y = Math.Max(0, Math.Min(_start.Y, p.Y));
        double x2 = Math.Min(ActualWidth, Math.Max(_start.X, p.X)), y2 = Math.Min(ActualHeight, Math.Max(_start.Y, p.Y));
        return new Rect(x, y, Math.Max(0, x2 - x), Math.Max(0, y2 - y));
    }

    private void UpdateSel(Point p)
    {
        Rect r = SelRect(p);
        UpdateDim(r);
        _selRect!.Visibility = r.Width > 1 ? Visibility.Visible : Visibility.Collapsed;
        _selRect.Width = r.Width; _selRect.Height = r.Height;
        Canvas.SetLeft(_selRect, r.X); Canvas.SetTop(_selRect, r.Y);
        ((TextBlock)_sizeTag!.Tag).Text = Math.Round(r.Width * Kx) + " × " + Math.Round(r.Height * Ky);
        _sizeTag.Visibility = r.Width > 24 ? Visibility.Visible : Visibility.Collapsed;
        _sizeTag.UpdateLayout();
        double tw = _sizeTag.ActualWidth;
        double ty = r.Bottom + 8 + _sizeTag.ActualHeight > ActualHeight ? r.Bottom - _sizeTag.ActualHeight - 8 : r.Bottom + 8;
        Canvas.SetLeft(_sizeTag, Math.Max(4, Math.Min(ActualWidth - tw - 4, r.Right - tw)));
        Canvas.SetTop(_sizeTag, ty);
    }

    private void UpdateDim(Rect hole)
    {
        if (_dim == null) return;
        var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
        g.Children.Add(new RectangleGeometry(new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight))));
        if (hole.Width > 0 && hole.Height > 0) g.Children.Add(new RectangleGeometry(hole, 7, 7));
        g.Freeze();
        _dim.Data = g;
    }

    // ---------- color picker ----------

    private void BuildColor()
    {
        _loupeImg = new Image { Width = 132, Height = 132, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_loupeImg, BitmapScalingMode.NearestNeighbor);
        _loupeImg.Clip = new EllipseGeometry(new Point(66, 66), 66, 66);
        var ring = new Ellipse
        {
            Width = 140, Height = 140, StrokeThickness = 4, Stroke = Brushes.White,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black }
        };
        var cell = new Border { Width = 132.0 / 15, Height = 132.0 / 15, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.4), Background = Brushes.Transparent };
        var cellOuter = new Border { Width = 132.0 / 15 + 2, Height = 132.0 / 15 + 2, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), Opacity = 0.7, IsHitTestVisible = false };
        _loupe = new Grid { Width = 140, Height = 140, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _loupe.Children.Add(ring);
        _loupe.Children.Add(_loupeImg);
        _loupe.Children.Add(cellOuter);
        _loupe.Children.Add(cell);

        _swatch = new Ellipse { Width = 20, Height = 20, Stroke = Kit.Frozen(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 };
        _value = Kit.T("#000000", 15, true);
        _value.Margin = new Thickness(9, 0, 0, 0);
        _value.VerticalAlignment = VerticalAlignment.Center;
        _fmtLine = Kit.T("", 10.5, true, Kit.Dim);
        _fmtLine.HorizontalAlignment = HorizontalAlignment.Center;
        _fmtLine.Margin = new Thickness(0, 4, 0, 0);
        var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        top.Children.Add(_swatch);
        top.Children.Add(_value);
        var col = new StackPanel();
        col.Children.Add(top);
        col.Children.Add(_fmtLine);
        _info = Kit.Pill(col, 18, new Thickness(14, 8, 14, 7));
        _info.IsHitTestVisible = false;
        _info.Visibility = Visibility.Collapsed;

        _layer.Children.Add(_loupe);
        _layer.Children.Add(_info);
        SetFmt(0);

        MouseMove += (_, e) => UpdateLoupe(e.GetPosition(_root));
        MouseEnter += (_, e) => UpdateLoupe(e.GetPosition(_root));
        MouseLeave += (_, _) => { if (!_picked) { _loupe.Visibility = Visibility.Collapsed; _info.Visibility = Visibility.Collapsed; } };
        MouseWheel += (_, e) => { SetFmt(_fmt + (e.Delta < 0 ? 1 : 3)); e.Handled = true; };
        MouseLeftButtonUp += (_, e) => { e.Handled = true; CopyColor(); };
    }

    private void SetFmt(int f)
    {
        _fmt = ((f % 4) + 4) % 4;
        if (_fmtLine != null)
        {
            var parts = new string[4];
            for (int i = 0; i < 4; i++) parts[i] = i == _fmt ? "● " + ScreenTools.ColorNames[i] : ScreenTools.ColorNames[i];
            _fmtLine.Text = string.Join("   ", parts);
        }
        if (_value != null && !_picked) _value.Text = ScreenTools.FormatColor(_cr, _cg, _cb, _fmt);
    }

    private void UpdateLoupe(Point p)
    {
        if (_picked || _loupe == null || _loupeImg == null || _info == null || _swatch == null || _value == null) return;
        int px = (int)Math.Clamp(p.X * Kx, 0, M.W - 1), py = (int)Math.Clamp(p.Y * Ky, 0, M.H - 1);
        int i = (py * M.W + px) * 4;
        _cb = M.Px[i]; _cg = M.Px[i + 1]; _cr = M.Px[i + 2];

        int x0 = Math.Clamp(px - 7, 0, Math.Max(0, M.W - 15)), y0 = Math.Clamp(py - 7, 0, Math.Max(0, M.H - 15));
        _loupeImg.Source = new CroppedBitmap(M.Shot, new Int32Rect(x0, y0, Math.Min(15, M.W), Math.Min(15, M.H)));
        _swatch.Fill = new SolidColorBrush(Color.FromRgb(_cr, _cg, _cb));
        _value.Text = ScreenTools.FormatColor(_cr, _cg, _cb, _fmt);

        _loupe!.Visibility = Visibility.Visible;
        _info.Visibility = Visibility.Visible;
        double lx = p.X - 70, ly = p.Y - 70;
        Canvas.SetLeft(_loupe, lx); Canvas.SetTop(_loupe, ly);
        _info.UpdateLayout();
        double iw = _info.ActualWidth > 0 ? _info.ActualWidth : 150;
        double iy = p.Y + 84;
        if (iy + 56 > ActualHeight) iy = p.Y - 84 - 56;
        Canvas.SetLeft(_info, Math.Clamp(p.X - iw / 2, 4, Math.Max(4, ActualWidth - iw - 4)));
        Canvas.SetTop(_info, iy);
    }

    private void CopyColor()
    {
        if (_picked || _value == null) return;
        string v = ScreenTools.FormatColor(_cr, _cg, _cb, _fmt);
        _picked = true;
        Kit.SetClipText(v);
        _value.Text = "Copied  " + v;
        if (_fmtLine != null) _fmtLine.Visibility = Visibility.Collapsed;
        ScreenTools.CloseAll(750);
    }

    // ---------- draw on screen ----------

    private void BuildDraw()
    {
        if (!IsHome) return;
        Cursor = Cursors.Pen;
        _ink = new InkCanvas { Background = Brushes.Transparent, EditingMode = InkCanvasEditingMode.Ink };
        _ink.UseCustomCursor = false;
        _root.Children.Insert(1, _ink); // between the picture and the layer
        ApplyTool();

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        string[] icons = { "pencil", "marker", "eraser" };
        string[] tips = { "Pen", "Highlighter", "Eraser" };
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            var b = Kit.Button(icons[i], null, () => { _tool = idx; ApplyTool(); }, false, 36, tips[i]);
            b.Margin = new Thickness(0, 0, 4, 0);
            _toolBtns.Add(b);
            row.Children.Add(b);
        }
        row.Children.Add(Divider());

        string[] cols = { "#FF453A", "#FF9F0A", "#FFD60A", "#32D74B", "#0A84FF", "#FFFFFF" };
        foreach (string c in cols)
        {
            Color cc = (Color)ColorConverter.ConvertFromString(c);
            var dot = new Ellipse { Width = 22, Height = 22, Fill = new SolidColorBrush(cc), Stroke = Brushes.Transparent, StrokeThickness = 2.4 };
            var hit = new Border { Width = 30, Height = 36, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = dot };
            hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; _inkColor = cc; if (_tool == 2) _tool = 0; ApplyTool(); };
            Kit.Press(hit, 0.88);
            _colorDots.Add(dot);
            row.Children.Add(hit);
        }
        row.Children.Add(Divider());
        var undo = Kit.Button("undo", null, Undo, false, 36, "Undo (Ctrl+Z)");
        undo.Margin = new Thickness(0, 0, 4, 0);
        row.Children.Add(undo);
        var clear = Kit.Button("trash", null, () => _ink?.Strokes.Clear(), false, 36, "Clear all");
        clear.Margin = new Thickness(0, 0, 4, 0);
        row.Children.Add(clear);
        row.Children.Add(Divider());
        var copy = Kit.Button("copy", "Copy", CopyDrawing, false, 36, "Copy the picture (Ctrl+C)");
        copy.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(copy);
        row.Children.Add(Kit.Button(null, "Done", () => ScreenTools.CloseAll(), true, 36));

        _bar = Kit.Pill(row, 30, new Thickness(10, 8, 10, 8));
        _bar.HorizontalAlignment = HorizontalAlignment.Left;
        _layer.Children.Add(_bar);
        _bar.Loaded += (_, _) =>
        {
            _bar.UpdateLayout();
            Canvas.SetLeft(_bar, Math.Max(8, (ActualWidth - _bar.ActualWidth) / 2));
            Canvas.SetTop(_bar, ActualHeight - _bar.ActualHeight - 46);
        };
        Loaded += (_, _) => { if (_ink != null) _ink.Focus(); };
    }

    private static UIElement Divider() =>
        new Border { Width = 1, Height = 22, Background = Kit.Sep, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

    private void ApplyTool()
    {
        if (_ink == null) return;
        if (_tool == 2) _ink.EditingMode = InkCanvasEditingMode.EraseByStroke;
        else
        {
            _ink.EditingMode = InkCanvasEditingMode.Ink;
            bool hl = _tool == 1;
            _ink.DefaultDrawingAttributes = new DrawingAttributes
            {
                Color = _inkColor,
                Width = hl ? 18 : 3.6,
                Height = hl ? 18 : 3.6,
                FitToCurve = true,
                IgnorePressure = true,
                IsHighlighter = hl,
                StylusTip = hl ? StylusTip.Rectangle : StylusTip.Ellipse
            };
        }
        for (int i = 0; i < _toolBtns.Count; i++)
            _toolBtns[i].Background = i == _tool ? Kit.Accent : Kit.Fill;
        for (int i = 0; i < _toolBtns.Count; i++)
            if (_toolBtns[i].Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is Viewbox vb && vb.Child is System.Windows.Shapes.Path pa)
                pa.Stroke = i == _tool ? Kit.OnAccent : Kit.Text;
        foreach (Ellipse d in _colorDots)
            d.Stroke = d.Fill is SolidColorBrush sb && sb.Color == _inkColor && _tool != 2 ? Kit.Text : Brushes.Transparent;
    }

    private void Undo()
    {
        if (_ink != null && _ink.Strokes.Count > 0) _ink.Strokes.RemoveAt(_ink.Strokes.Count - 1);
    }

    private void CopyDrawing()
    {
        if (_ink == null) return;
        _layer.Visibility = Visibility.Hidden;
        _root.UpdateLayout();
        var rtb = new RenderTargetBitmap(M.W, M.H, 96 * Kx, 96 * Ky, PixelFormats.Pbgra32);
        rtb.Render(_root);
        rtb.Freeze();
        _layer.Visibility = Visibility.Visible;
        bool ok = Kit.SetClipImage(rtb);
        Banner(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { Kit.T(ok ? "Picture copied" : "Could not copy the picture", 14, true) }
        });
        ScreenTools.CloseAll(900);
    }
}
