using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace LiteIsland;

/// <summary>
/// Camera mirror. Runs in its own helper process (LiteIsland.exe --worker mirror), so the island's RAM stays tiny and a
/// camera problem can never freeze the island. A small rounded, mirrored preview you can drag; Esc or x closes it.
/// </summary>
internal static class Mirror
{
    public static string Run()
    {
        var win = new MirrorWindow();
        win.ShowDialog();
        return "closed";
    }

    private sealed class MirrorWindow : Window
    {
        private readonly Image _img = new() { Stretch = Stretch.UniformToFill };
        private readonly TextBlock _msg = new()
        {
            Foreground = Brushes.White, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24), Text = "Starting the camera..."
        };
        private readonly Border _close;
        private WriteableBitmap? _bmp;
        private MediaCapture? _capture;
        private MediaFrameReader? _reader;
        private int _busy;
        private bool _closed;

        public MirrorWindow()
        {
            Title = "LiteIsland Mirror";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = true;
            Width = 340; Height = 255;
            WindowStartupLocation = WindowStartupLocation.Manual;
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 24;
            Top = wa.Bottom - Height - 24;

            var frame = new Border { CornerRadius = new CornerRadius(24), Background = Brushes.Black, BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)), BorderThickness = new Thickness(1), Margin = new Thickness(6), SnapsToDevicePixels = true };
            frame.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45 };
            var inner = new Grid();
            inner.SizeChanged += (_, _) => inner.Clip = new RectangleGeometry(new Rect(0, 0, inner.ActualWidth, inner.ActualHeight), 23, 23);

            _img.RenderTransformOrigin = new Point(0.5, 0.5);
            _img.RenderTransform = new ScaleTransform(-1, 1); // a mirror: left is left
            inner.Children.Add(_img);
            inner.Children.Add(_msg);

            _close = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 10, 10, 0), Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                Child = new TextBlock { Text = "✕", Foreground = Brushes.White, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            _close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Close(); };
            inner.Children.Add(_close);
            frame.Child = inner;
            Content = frame;

            MouseEnter += (_, _) => _close.Opacity = 1;
            MouseLeave += (_, _) => _close.Opacity = 0;
            MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } } };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            MouseWheel += (_, e) =>
            {
                double w = Math.Clamp(Width + (e.Delta > 0 ? 40 : -40), 220, 760);
                double cx = Left + Width / 2, cy = Top + Height / 2;
                Width = w; Height = w * 0.75;
                Left = cx - Width / 2; Top = cy - Height / 2;
            };
            Loaded += async (_, _) => { Activate(); await StartAsync(); };
            Closed += (_, _) => { _closed = true; Stop(); };
        }

        private void Fail(string text)
        {
            _msg.Text = text;
            _msg.Visibility = Visibility.Visible;
        }

        private async Task StartAsync()
        {
            try
            {
                var groups = await MediaFrameSourceGroup.FindAllAsync();
                MediaFrameSourceGroup? group = null;
                MediaFrameSourceInfo? info = null;
                foreach (var g in groups)
                {
                    var i = g.SourceInfos.FirstOrDefault(s => s.SourceKind == MediaFrameSourceKind.Color);
                    if (i != null) { group = g; info = i; break; }
                }
                if (group == null || info == null) { Fail("No camera found"); return; }

                _capture = new MediaCapture();
                await _capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    SourceGroup = group,
                    SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    StreamingCaptureMode = StreamingCaptureMode.Video
                });

                if (!_capture.FrameSources.TryGetValue(info.Id, out MediaFrameSource? source)) { Fail("The camera could not be opened"); return; }

                // a small picture is enough for a mirror (less CPU and RAM): the smallest format that is at least 640 wide
                var fmt = source.SupportedFormats
                    .Where(f => f.VideoFormat.Width >= 640 && f.FrameRate.Denominator > 0 && (double)f.FrameRate.Numerator / f.FrameRate.Denominator >= 15)
                    .OrderBy(f => f.VideoFormat.Width * f.VideoFormat.Height)
                    .FirstOrDefault();
                if (fmt != null) { try { await source.SetFormatAsync(fmt); } catch { } }

                _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
                _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                _reader.FrameArrived += OnFrame;
                var status = await _reader.StartAsync();
                if (status != MediaFrameReaderStartStatus.Success) Fail("The camera is busy or blocked.\nCheck Windows Settings > Privacy > Camera.");
            }
            catch (Exception ex)
            {
                string m = ex.Message ?? "";
                Fail(ex is UnauthorizedAccessException || m.Contains("denied", StringComparison.OrdinalIgnoreCase)
                    ? "Camera access is off.\nWindows Settings > Privacy > Camera:\nallow desktop apps."
                    : "The camera could not be started.");
            }
        }

        private void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            if (_closed || Interlocked.Exchange(ref _busy, 1) == 1) return; // still drawing the last picture: skip this one
            try
            {
                using var frame = sender.TryAcquireLatestFrame();
                SoftwareBitmap? sb = frame?.VideoMediaFrame?.SoftwareBitmap;
                if (sb == null) { _busy = 0; return; }

                SoftwareBitmap conv = sb;
                bool own = false;
                if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || sb.BitmapAlphaMode == BitmapAlphaMode.Straight)
                {
                    conv = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    own = true;
                }
                int w = conv.PixelWidth, h = conv.PixelHeight;
                var buf = new byte[w * h * 4];
                conv.CopyToBuffer(buf.AsBuffer());
                if (own) conv.Dispose();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_closed) return;
                        if (_bmp == null || _bmp.PixelWidth != w || _bmp.PixelHeight != h)
                        {
                            _bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
                            _img.Source = _bmp;
                            _msg.Visibility = Visibility.Collapsed;
                        }
                        _bmp.WritePixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
                    }
                    catch { }
                    finally { Interlocked.Exchange(ref _busy, 0); }
                }));
            }
            catch { Interlocked.Exchange(ref _busy, 0); }
        }

        private async void Stop()
        {
            try
            {
                if (_reader != null)
                {
                    _reader.FrameArrived -= OnFrame;
                    await _reader.StopAsync();
                    _reader.Dispose();
                    _reader = null;
                }
                _capture?.Dispose();
                _capture = null;
            }
            catch { }
        }
    }
}
