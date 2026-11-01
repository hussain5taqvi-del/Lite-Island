using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;

namespace LiteIsland;

/// <summary>
/// Runs inside the helper process (LiteIsland.exe --worker pick MODE). Takes a picture of the whole screen and
/// then: "text" = drag a rectangle, the text inside is recognized (Windows OCR, offline);
/// "qr" = every QR code on the screen is searched; "color" = click a pixel to get its color.
/// Result: the text, "NONE" (nothing found) or "CANCEL".
/// </summary>
internal static class ScreenPick
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[]? bits, ref BmpInfo bi, uint usage);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int cx, uint flags);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    [StructLayout(LayoutKind.Sequential)]
    private struct BmpInfo
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
    }

    public static string Run(string mode)
    {
        try { SetProcessDPIAware(); } catch { }
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        byte[] px = Capture(vx, vy, vw, vh);
        var shot = BitmapSource.Create(vw, vh, 96, 96, PixelFormats.Bgr32, null, px, vw * 4);
        shot.Freeze();

        if (mode == "qr") return ReadQr(px, vw, vh);

        var win = new PickWindow(shot, px, mode, vx, vy, vw, vh);
        win.ShowDialog();
        return win.Result;
    }

    private static string ReadQr(byte[] px, int w, int h)
    {
        var reader = new BarcodeReaderGeneric { AutoRotate = true };
        reader.Options.TryHarder = true;
        reader.Options.PossibleFormats = new[] { BarcodeFormat.QR_CODE };
        var src = new RGBLuminanceSource(px, w, h, RGBLuminanceSource.BitmapFormat.BGR32);
        var r = reader.Decode(src);
        return r?.Text is { Length: > 0 } t ? t : "NONE";
    }

    private static byte[] Capture(int x, int y, int w, int h)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
        SelectObject(mem, old);

        var bi = new BmpInfo { Size = Marshal.SizeOf<BmpInfo>(), Width = w, Height = -h, Planes = 1, BitCount = 32 };
        var data = new byte[w * h * 4];
        GetDIBits(mem, bmp, 0, (uint)h, data, ref bi, 0);
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        return data;
    }

    private sealed class PickWindow : Window
    {
        private readonly BitmapSource _shot;
        private readonly byte[] _px;
        private readonly string _mode;
        private readonly int _vx, _vy, _vw, _vh;
        private readonly Canvas _canvas = new();
        private readonly System.Windows.Shapes.Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), IsHitTestVisible = false };
        private readonly System.Windows.Shapes.Rectangle _sel = new() { Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly Border _tip = new()
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x16, 0x18)),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 5, 10, 5), IsHitTestVisible = false
        };
        private readonly TextBlock _tipText = new() { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold };
        private readonly System.Windows.Shapes.Ellipse _mag = new() { Width = 112, Height = 112, StrokeThickness = 3, Stroke = Brushes.White, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly ImageBrush _magBrush;
        private Point _start;
        private bool _drag;

        public string Result = "CANCEL";

        public PickWindow(BitmapSource shot, byte[] px, string mode, int vx, int vy, int vw, int vh)
        {
            _shot = shot; _px = px; _mode = mode; _vx = vx; _vy = vy; _vw = vw; _vh = vh;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Black;
            Cursor = Cursors.Cross;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 0; Top = 0; Width = 100; Height = 100;

            var img = new Image { Source = shot, Stretch = Stretch.Fill, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            var root = new Grid();
            root.Children.Add(img);
            _canvas.Background = Brushes.Transparent;
            _canvas.Children.Add(_dim);
            _canvas.Children.Add(_sel);
            _tip.Child = _tipText;
            _magBrush = new ImageBrush(shot) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(_magBrush, BitmapScalingMode.NearestNeighbor);
            _mag.Fill = _magBrush;
            _canvas.Children.Add(_mag);
            _canvas.Children.Add(_tip);
            root.Children.Add(_canvas);
            Content = root;

            _tipText.Text = mode == "text" ? "Drag around the text  ·  Esc to cancel" : "Click a color  ·  Esc to cancel";
            Canvas.SetLeft(_tip, 20); Canvas.SetTop(_tip, 20);
            if (mode == "color") _mag.Visibility = Visibility.Visible;

            SourceInitialized += (_, _) =>
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                SetWindowPos(h, new IntPtr(-1), _vx, _vy, _vw, _vh, 0x0040); // topmost, exactly over every screen (pixels)
            };
            Loaded += (_, _) => { Activate(); Focus(); Redraw(new Rect(0, 0, 0, 0)); };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseRightButtonUp += (_, _) => Close();
        }

        private double Fx => _shot.PixelWidth / Math.Max(1.0, ActualWidth);
        private double Fy => _shot.PixelHeight / Math.Max(1.0, ActualHeight);

        private void Redraw(Rect r)
        {
            var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
            g.Children.Add(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
            if (r.Width > 0 && r.Height > 0) g.Children.Add(new RectangleGeometry(r));
            _dim.Data = g;
            if (_mode == "color") _dim.Visibility = Visibility.Collapsed;
        }

        private void OnDown(object s, MouseButtonEventArgs e)
        {
            if (_mode == "color") { Finish(e.GetPosition(this)); return; }
            _start = e.GetPosition(this);
            _drag = true;
            CaptureMouse();
            _sel.Visibility = Visibility.Visible;
        }

        private void OnMove(object s, MouseEventArgs e)
        {
            Point p = e.GetPosition(this);
            if (_mode == "color")
            {
                int x = Math.Clamp((int)(p.X * Fx), 0, _vw - 1), y = Math.Clamp((int)(p.Y * Fy), 0, _vh - 1);
                _magBrush.Viewbox = new Rect(x - 7, y - 7, 15, 15);
                double mx = p.X + 24, my = p.Y + 24;
                if (mx + 120 > ActualWidth) mx = p.X - 136;
                if (my + 150 > ActualHeight) my = p.Y - 160;
                Canvas.SetLeft(_mag, mx); Canvas.SetTop(_mag, my);
                _tipText.Text = Hex(x, y);
                Canvas.SetLeft(_tip, mx + 12); Canvas.SetTop(_tip, my + 118);
                return;
            }
            if (!_drag) return;
            var r = new Rect(Math.Min(_start.X, p.X), Math.Min(_start.Y, p.Y), Math.Abs(p.X - _start.X), Math.Abs(p.Y - _start.Y));
            Canvas.SetLeft(_sel, r.X); Canvas.SetTop(_sel, r.Y);
            _sel.Width = r.Width; _sel.Height = r.Height;
            Redraw(r);
        }

        private string Hex(int x, int y)
        {
            int i = (y * _vw + x) * 4;
            return "#" + _px[i + 2].ToString("X2") + _px[i + 1].ToString("X2") + _px[i].ToString("X2");
        }

        private void OnUp(object s, MouseButtonEventArgs e)
        {
            if (_mode == "color" || !_drag) return;
            _drag = false;
            ReleaseMouseCapture();
            Point p = e.GetPosition(this);
            int x = (int)(Math.Min(_start.X, p.X) * Fx), y = (int)(Math.Min(_start.Y, p.Y) * Fy);
            int w = (int)(Math.Abs(p.X - _start.X) * Fx), h = (int)(Math.Abs(p.Y - _start.Y) * Fy);
            x = Math.Clamp(x, 0, _vw - 2); y = Math.Clamp(y, 0, _vh - 2);
            w = Math.Min(w, _vw - x); h = Math.Min(h, _vh - y);
            if (w < 8 || h < 8) { Close(); return; }

            _tipText.Text = "Reading...";
            Canvas.SetLeft(_tip, 20); Canvas.SetTop(_tip, 20);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                string tmp = Path.Combine(Path.GetTempPath(), "li_pick_" + Guid.NewGuid().ToString("N") + ".png");
                try
                {
                    var crop = new CroppedBitmap(_shot, new Int32Rect(x, y, w, h));
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(crop));
                    using (var fs = File.Create(tmp)) enc.Save(fs);
                    string text = WinRtOps.OcrFileAsync(WinRtOps.Engine(), tmp).GetAwaiter().GetResult();
                    Result = string.IsNullOrWhiteSpace(text) ? "NONE" : text.Trim();
                }
                catch { Result = "NONE"; }
                finally { try { File.Delete(tmp); } catch { } }
                Close();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void Finish(Point p)
        {
            int x = Math.Clamp((int)(p.X * Fx), 0, _vw - 1), y = Math.Clamp((int)(p.Y * Fy), 0, _vh - 1);
            Result = Hex(x, y);
            Close();
        }
    }
}
