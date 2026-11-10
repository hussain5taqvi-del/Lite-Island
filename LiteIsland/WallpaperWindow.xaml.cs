using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LiteIsland;

/// <summary>
/// Wallpapers page: browse 4K static wallpapers (4kwallpapers.com), search them, click once to download
/// the sharpest (4K or bigger) file and set it. Only one page of small thumbnails is kept in memory.
/// </summary>
public partial class WallpaperWindow : Window
{
    private enum Tab { Static, Live, Library }

    /// <summary>Raised when a live wallpaper video was chosen (MainWindow turns the live wallpaper on with it).</summary>
    public static Action<string>? LiveChosen;
    /// <summary>Reads / sets whether the live wallpaper is on (MainWindow owns the setting). SetLive returns false when there is no video yet.</summary>
    public static Func<bool>? LiveIsOn;
    public static Func<bool, bool>? SetLive;
    private bool _syncing;

    // MainWindow applies the change on its own dispatcher turn: look again right after
    private void SyncLiveSoon() => Dispatcher.BeginInvoke(new Action(SyncLive), DispatcherPriority.Background);

    private void SyncLive()
    {
        _syncing = true;
        try { LiveSwitch.IsChecked = LiveIsOn?.Invoke() ?? false; } finally { _syncing = false; }
    }

    private void LiveSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || !_ready) return;
        bool on = LiveSwitch.IsChecked == true;
        if (SetLive != null && !SetLive(on))
        {
            SyncLive();
            ShowStatus("Pick a live wallpaper first (Live tab or Add video), then switch it on.");
            return;
        }
        if (on && ShowingStatusHint) ShowStatus("");
    }
    private bool ShowingStatusHint => Status.Visibility == Visibility.Visible && Cards.Children.Count > 0;

    private const double Gap = 12; // space between cards

    private Tab _tab = Tab.Static;
    private int _page = 1; // next page to load
    private bool _ready;
    private bool _loadingMore, _ended;
    private readonly System.Collections.Generic.HashSet<string> _seen = new();
    private int _loadId;
    private double _cardW = 230;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _thumbGate = new(4); // at most 4 thumbnails download at once

    public WallpaperWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => GlassWindow.ForceForeground(this);

        Closed += (_, _) =>
        {
            _cts?.Cancel();
            Cards.Children.Clear();
        };

        _ready = true;
        SyncLive();
        UpdateChrome();
        Reload();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        GlassWindow.Apply(this);
    }

    private static Brush B(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    // ---------- Header events ----------

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _tab = TabLib.IsChecked == true ? Tab.Library : TabLive.IsChecked == true ? Tab.Live : Tab.Static;
        _page = 1;
        UpdateChrome();
        Reload();
    }

    private void Latest_Click(object sender, RoutedEventArgs e)
    {
        string? f = LiveService.LatestDownload();
        if (f == null) { ShowStatus("No video found in your Downloads folder (last 3 days).\nDownload one from the site first."); return; }
        try
        {
            Directory.CreateDirectory(LiveService.LiveDir);
            string dest = Path.Combine(LiveService.LiveDir, Path.GetFileName(f));
            if (!File.Exists(dest)) File.Copy(f, dest);
            LiveChosen?.Invoke(dest); SyncLiveSoon();
            ShowStatus("Live wallpaper set: " + Path.GetFileName(f));
        }
        catch (Exception ex) { ShowStatus("Could not use it: " + ex.Message); }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(WallpaperService.Root);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + WallpaperService.Root + "\"") { UseShellExecute = true });
        }
        catch { }
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        bool live = _tab == Tab.Live;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = live ? "Choose a video" : "Choose a wallpaper",
            Filter = live ? "Videos|*.mp4;*.webm;*.mkv;*.mov;*.avi|All files|*.*" : "Pictures|*.jpg;*.jpeg;*.png;*.bmp|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            string src = dlg.FileName;
            if (live ? !LiveService.IsVideo(src) : !WallpaperService.IsImage(src))
            {
                ShowStatus(live ? "Choose a video (MP4, WebM, MKV, MOV)." : "Choose a picture (JPG, PNG or BMP).");
                return;
            }
            string dir = live ? LiveService.LiveDir : WallpaperService.StaticDir;
            string dest = Path.Combine(dir, Path.GetFileName(src));
            Directory.CreateDirectory(dir);
            if (!File.Exists(dest))
            {
                ShowStatus("Copying...");
                await Task.Run(() => File.Copy(src, dest));
                ShowStatus("");
            }
            if (live) { LiveChosen?.Invoke(dest); ShowStatus("Live wallpaper set."); }
            else WallpaperManager.Apply(dest, ShowStatus);
            SyncLiveSoon();
        }
        catch (Exception ex)
        {
            ShowStatus("Could not add the file: " + ex.Message);
        }
    }

    // ---------- Screen state ----------

    private void UpdateChrome()
    {
        bool lib = _tab == Tab.Library;
        bool live = _tab == Tab.Live;
        LatestBtn.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        AddBtn.Visibility = lib ? Visibility.Collapsed : Visibility.Visible;
        AddText.Text = live ? "Add video" : "Add picture";
    }

    private void ShowStatus(string text)
    {
        Status.Text = text;
        Status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // The grid has equal columns that fill the whole width, so there is never an empty strip on the right
    private void Scroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RecalcCardSize();
        foreach (UIElement c in Cards.Children) ApplyCardSize(c);
    }

    private void RecalcCardSize()
    {
        double avail = Scroll.ActualWidth - 20; // scroll bar + margin
        if (avail < 200) return;
        int cols = Math.Max(2, (int)(avail / 240));
        Cards.Columns = cols;
        _cardW = Math.Floor(avail / cols) - Gap;
    }

    private void ApplyCardSize(UIElement el)
    {
        if (el is Border card && card.Tag is Border thumb)
            thumb.Height = Math.Round(_cardW * 9 / 16);
    }

    // ---------- Loading a page of wallpapers ----------

    private void Reload()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        ++_loadId;
        _page = 1; _ended = false; _loadingMore = false;
        _seen.Clear();
        Cards.Children.Clear();
        RecalcCardSize();
        Scroll.ScrollToTop();

        if (_tab == Tab.Library) { ShowLibrary(); return; }
        ShowStatus("Loading...");
        _ = LoadMoreAsync();
    }

    // Endless scroll: the next page is fetched shortly before the end is reached
    private void Scroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (_tab == Tab.Library || _loadingMore || _ended || _page == 1) return;
        if (Scroll.ScrollableHeight - Scroll.VerticalOffset < 500 || Scroll.ScrollableHeight <= 0) _ = LoadMoreAsync();
    }

    private async Task LoadMoreAsync()
    {
        if (_loadingMore || _ended) return;
        _loadingMore = true;
        CancellationToken ct = _cts!.Token;
        int id = _loadId;
        try
        {
            var items = _tab == Tab.Live
                ? await LiveService.BrowseAsync("", _page, ct)
                : await WallpaperService.BrowseSmartAsync(_page, "", ct);
            if (id != _loadId) return;

            var fresh = items.Where(x => _seen.Add(x.PageUrl)).ToList();
            if (fresh.Count == 0)
            {
                _ended = true;
                if (Cards.Children.Count == 0) ShowStatus("Nothing found.");
                return;
            }
            _page++;
            ShowStatus("");
            int i = 0;
            foreach (var it in fresh) Cards.Children.Add(BuildCard(it, ct, i++));
            if (_tab == Tab.Live) _ = LiveService.PrefetchAsync(fresh, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (id == _loadId && Cards.Children.Count == 0) ShowStatus(FriendlyError(ex));
        }
        finally
        {
            if (id == _loadId)
            {
                _loadingMore = false;
                // short first page: keep filling until the window is full
                if (!_ended) Dispatcher.BeginInvoke(new Action(() =>
                {
                    Scroll.UpdateLayout();
                    if (id == _loadId && _tab != Tab.Library && Scroll.ScrollableHeight - Scroll.VerticalOffset < 500) _ = LoadMoreAsync();
                }), DispatcherPriority.Background);
            }
        }
    }

    // ---------- Online card ----------

    private UIElement BuildCard(WallItem it, CancellationToken ct, int index)
    {
        var card = new Border
        {
            Style = (Style)FindResource("CardItem"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(Gap / 2),
            Opacity = 0
        };
        var sp = new StackPanel();

        var thumb = new Border
        {
            Height = Math.Round(_cardW * 9 / 16),
            Background = B("#1FFFFFFF"),
            CornerRadius = new CornerRadius(17, 17, 0, 0)
        };
        bool live = it.Source == WallSource.MoeWalls;
        UIElement badge = Badge(live ? "LIVE" : it.Source == WallSource.Wallhaven && !IsUhd(it.Resolution) ? "HD" : "4K");
        thumb.Child = badge;
        if (live) WireLivePreview(card, thumb, it, badge);
        card.Tag = thumb;
        sp.Children.Add(thumb);

        sp.Children.Add(TitleText(it.Title));

        var info = new TextBlock
        {
            Margin = new Thickness(14, 2, 14, 0),
            FontSize = 11.5,
            Foreground = (Brush)FindResource("TextSecondary"),
            Text = live ? "MoeWalls" : it.Source == WallSource.Wallhaven ? "Wallpapers" : "4K Wallpapers",
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        sp.Children.Add(info);

        var bar = new ProgressBar
        {
            Style = (Style)FindResource("Progress"),
            Margin = new Thickness(14, 9, 14, 0),
            Visibility = Visibility.Collapsed,
            Maximum = 100
        };
        sp.Children.Add(bar);

        var arrow = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 6,0 V 9 M 2,5.6 L 6,9.6 L 10,5.6"),
            Stroke = Brushes.White,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0)
        };
        var btnContent = new StackPanel { Orientation = Orientation.Horizontal };
        btnContent.Children.Add(arrow);
        btnContent.Children.Add(new TextBlock { Text = live ? "Download & set live" : "Download & set", VerticalAlignment = VerticalAlignment.Center });
        var btn = new Button
        {
            Content = btnContent,
            Style = (Style)FindResource("BtnBlue"),
            Margin = new Thickness(14, 10, 14, 14)
        };
        string source = info.Text;
        btn.Click += (_, _) => OnSetClick(it, btn, bar, info, source);
        sp.Children.Add(btn);

        card.Child = sp;
        _ = LoadThumbAsync(it, thumb, ct);

        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
        {
            BeginTime = TimeSpan.FromMilliseconds(Math.Min(index, 12) * 30),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        return card;
    }

    /// <summary>Hover over a live wallpaper: a few seconds of its preview play on the card (frames from mpv, looping, one at a time).</summary>
    private void WireLivePreview(Border card, Border thumb, WallItem it, UIElement badge)
    {
        var layer = new System.Windows.Shapes.Path { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0, IsHitTestVisible = false };
        thumb.Child = null; // the badge moves into the grid below
        var host = new Grid();
        host.Children.Add(layer);
        host.Children.Add(badge);
        thumb.Child = host;

        DispatcherTimer? delay = null, play = null;
        int token = 0;

        void Stop()
        {
            play?.Stop(); play = null;
            layer.BeginAnimation(OpacityProperty, null);
            layer.Opacity = 0;
            layer.Fill = null;
        }

        async Task StartAsync(int my)
        {
            LiveService.LivePage page = await LiveService.GetPageAsync(it.PageUrl);
            if (my != token || page.PreviewUrl == null || !card.IsMouseOver) return;
            var files = await LiveFrames.PreviewFramesAsync(page.PreviewUrl);
            if (my != token || files.Count < 2 || !card.IsMouseOver) return;

            var frames = new System.Collections.Generic.List<BitmapImage>();
            foreach (string f in files)
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.DecodePixelWidth = 400;
                    bi.UriSource = new Uri(f, UriKind.Absolute);
                    bi.EndInit();
                    bi.Freeze();
                    frames.Add(bi);
                }
                catch { }
            }
            if (my != token || frames.Count < 2 || !card.IsMouseOver) return;

            Stop();
            double w = thumb.ActualWidth > 20 ? thumb.ActualWidth : _cardW, h = thumb.ActualHeight > 20 ? thumb.ActualHeight : thumb.Height;
            const double r = 17;
            var fig = new PathFigure { StartPoint = new Point(0, r), IsClosed = true };
            fig.Segments.Add(new ArcSegment(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(new Point(w - r, 0), true));
            fig.Segments.Add(new ArcSegment(new Point(w, r), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(new Point(w, h), true));
            fig.Segments.Add(new LineSegment(new Point(0, h), true));
            var geo = new PathGeometry(); geo.Figures.Add(fig); geo.Freeze();
            layer.Data = geo;
            layer.Width = w; layer.Height = h;
            var brush = new ImageBrush(frames[0]) { Stretch = Stretch.UniformToFill };
            layer.Fill = brush;
            layer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));

            int n = 0;
            play = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(125) };
            play.Tick += (_, _) => { n = (n + 1) % frames.Count; brush.ImageSource = frames[n]; };
            play.Start();
        }

        card.MouseEnter += (_, _) =>
        {
            int my = ++token;
            delay?.Stop();
            delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            delay.Tick += (s, e2) => { ((DispatcherTimer)s!).Stop(); _ = StartAsync(my); };
            delay.Start();
            _ = LiveService.GetPageAsync(it.PageUrl); // the click is instant after this
        };
        card.MouseLeave += (_, _) => { token++; delay?.Stop(); Stop(); };
        card.Unloaded += (_, _) => { token++; delay?.Stop(); Stop(); };
    }

    private async Task LoadThumbAsync(WallItem it, Border target, CancellationToken ct)
    {
        try
        {
            await _thumbGate.WaitAsync(ct);
            try
            {
                var bmp = await WallpaperService.LoadThumbAsync(it, ct);
                if (bmp != null && !ct.IsCancellationRequested)
                {
                    var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill, Opacity = 0 };
                    target.Background = brush;
                    brush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)));
                }
            }
            finally
            {
                _thumbGate.Release();
            }
        }
        catch
        {
            // cancelled or no picture: the glass placeholder stays
        }
    }

    private async void OnSetClick(WallItem it, Button btn, ProgressBar bar, TextBlock info, string sourceName)
    {
        btn.IsEnabled = false;
        bar.Visibility = Visibility.Visible;
        bar.Value = 0;
        info.Text = "Finding the best file...";
        try
        {
            // Always the sharpest file the site has (4K or bigger), never a smaller copy
            DownloadInfo? dl = await WallpaperService.ResolveDownloadAsync(it, false, 0, CancellationToken.None);
            bool live = it.Source == WallSource.MoeWalls;
            if (dl == null)
            {
                OpenInBrowser(it.PageUrl);
                info.Text = live ? "Opened in your browser: download it there, then press \"Use latest download\"" : "No direct link: page opened in your browser";
                return;
            }

            string dest = Path.Combine(live ? LiveService.LiveDir : WallpaperService.StaticDir, dl.FileName);

            bool have = File.Exists(dest) && new FileInfo(dest).Length > 0;
            if (!have)
            {
                info.Text = "Downloading 0%";
                var progress = new Progress<double>(p =>
                {
                    bar.Value = p * 100;
                    info.Text = "Downloading " + (int)(p * 100) + "%";
                });
                await WallpaperService.DownloadAsync(dl.Url, dl.Referer, dest, progress, CancellationToken.None);
            }

            info.Text = "Setting...";
            string? error = null;
            if (live) LiveChosen?.Invoke(dest);
            else WallpaperManager.Apply(dest, msg => error = msg);
            SyncLiveSoon();
            info.Text = error == null ? (live ? "Done: live wallpaper on" : "Done: wallpaper set") : "Failed: " + Short(error);
        }
        catch (Exception)
        {
            info.Text = "Could not download. Please try again.";
        }
        finally
        {
            btn.IsEnabled = true;
            bar.Visibility = Visibility.Collapsed;
            if (info.Text.Length == 0) info.Text = sourceName;
        }
    }

    // Plain words instead of raw .NET error text
    private static string FriendlyError(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        if (inner is System.Net.Sockets.SocketException || inner is System.Net.Http.HttpRequestException hx && hx.StatusCode == null)
            return "No internet connection.\nConnect to the internet and switch the tab to try again.";
        return "Wallpapers are not available right now.\nPlease try again in a minute.";
    }

    private static bool IsUhd(string res)
    {
        int x = res.IndexOf('x');
        return x > 0 && int.TryParse(res.Substring(0, x), out int w) && w >= 3800;
    }

    private static string Short(string s) => s.Length > 60 ? s.Substring(0, 60) + "..." : s;

    private static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    // ---------- My library ----------

    private void ShowLibrary()
    {
        var files = WallpaperService.LibraryFiles().Where(WallpaperService.IsImage).ToList();
        files.AddRange(LiveService.LiveFiles());
        if (files.Count == 0)
        {
            ShowStatus("Nothing here yet.\nDownload a wallpaper from Wallpapers, or add your own picture.");
            return;
        }
        ShowStatus("");

        int i = 0;
        foreach (var f in files) Cards.Children.Add(BuildLibraryCard(f, i++));
    }

    private UIElement BuildLibraryCard(string file, int index)
    {
        var card = new Border
        {
            Style = (Style)FindResource("CardItem"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(Gap / 2),
            Opacity = 0
        };
        var sp = new StackPanel();

        var thumb = new Border
        {
            Height = Math.Round(_cardW * 9 / 16),
            Background = B("#1FFFFFFF"),
            CornerRadius = new CornerRadius(17, 17, 0, 0)
        };
        bool video = LiveService.IsVideo(file);
        if (video)
        {
            thumb.Child = Badge("LIVE");
            _ = Task.Run(async () =>
            {
                string? cov = await LiveFrames.CoverAsync(file);
                if (cov == null) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.DecodePixelWidth = 480;
                        bi.UriSource = new Uri(cov, UriKind.Absolute);
                        bi.EndInit();
                        bi.Freeze();
                        thumb.Background = new ImageBrush(bi) { Stretch = Stretch.UniformToFill };
                    }
                    catch { }
                });
            });
        }
        else
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 480;
            bi.UriSource = new Uri(file, UriKind.Absolute);
            bi.EndInit();
            bi.Freeze();
            thumb.Background = new ImageBrush(bi) { Stretch = Stretch.UniformToFill };
        }
        catch { }
        card.Tag = thumb;
        sp.Children.Add(thumb);

        sp.Children.Add(TitleText(NiceName(file)));

        long size = 0;
        try { size = new FileInfo(file).Length; } catch { }
        string res = ResolutionOf(file);
        sp.Children.Add(new TextBlock
        {
            Margin = new Thickness(14, 2, 14, 0),
            FontSize = 11.5,
            Foreground = (Brush)FindResource("TextSecondary"),
            Text = (res.Length > 0 ? res + "  ·  " : "") + (size / 1024.0 / 1024.0).ToString("0.0") + " MB"
        });

        var row = new Grid { Margin = new Thickness(14, 10, 14, 14) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var set = new Button { Content = "Set", Style = (Style)FindResource("BtnBlue") };
        set.Click += (_, _) =>
        {
            if (video) { LiveChosen?.Invoke(file); ShowStatus("Live wallpaper set."); }
            else WallpaperManager.Apply(file, ShowStatus);
            SyncLiveSoon();
        };
        row.Children.Add(set);

        var del = new Button
        {
            Content = "Delete",
            Style = (Style)FindResource("Btn"),
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(del, 1);
        del.Click += (_, _) =>
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                ShowStatus("Could not delete: " + ex.Message);
            }
            Reload();
        };
        row.Children.Add(del);

        sp.Children.Add(row);
        card.Child = sp;

        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
        {
            BeginTime = TimeSpan.FromMilliseconds(Math.Min(index, 12) * 30),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        return card;
    }

    // "4k-ferrari-296-gt-modificata-9876-3840x2160.jpg" -> "Ferrari 296 Gt Modificata"
    private static string NiceName(string file)
    {
        string n = Path.GetFileNameWithoutExtension(file);
        n = Regex.Replace(n, @"^4k-", "", RegexOptions.IgnoreCase);
        n = Regex.Replace(n, @"-\d{3,5}x\d{3,5}$", "");
        n = Regex.Replace(n, @"-\d{3,6}$", "");
        var words = n.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return Path.GetFileNameWithoutExtension(file);
        return string.Join(" ", words.Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
    }

    private static string ResolutionOf(string file)
    {
        var m = Regex.Match(Path.GetFileNameWithoutExtension(file), @"(\d{3,5})x(\d{3,5})$");
        return m.Success ? m.Groups[1].Value + " x " + m.Groups[2].Value : "";
    }

    // ---------- Small pieces ----------

    private TextBlock TitleText(string text) => new TextBlock
    {
        Text = text,
        Margin = new Thickness(14, 12, 14, 0),
        FontWeight = FontWeights.SemiBold,
        FontSize = 13.5,
        TextTrimming = TextTrimming.CharacterEllipsis,
        ToolTip = text
    };

    private static UIElement Badge(string text) => new Border
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(10),
        Padding = new Thickness(8, 3, 8, 3),
        CornerRadius = new CornerRadius(9),
        Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White
        }
    };
}
