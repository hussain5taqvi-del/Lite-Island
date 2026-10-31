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
    private enum Tab { Static, Library }

    private const double Gap = 12; // space between cards

    private Tab _tab = Tab.Static;
    private int _page = 1;
    private bool _ready;
    private string _catPath = "";   // "" = Recent
    private string _catName = "Recent";

    // Same categories as the 4kwallpapers.com menu: (name, url path)
    private static readonly (string Name, string Path)[] Categories =
    {
        ("Recent", ""), ("Popular", "most-popular-4k-wallpapers"), ("Featured", "best-4k-wallpapers"),
        ("Abstract", "abstract"), ("Animals", "animals"), ("Anime", "anime"), ("Architecture", "architecture"),
        ("Bikes", "bikes"), ("Black/Dark", "black-dark"), ("Cars", "cars"), ("Celebrations", "celebrations"),
        ("Cute", "cute"), ("Fantasy", "fantasy"), ("Flowers", "flowers"), ("Food", "food"), ("Games", "games"),
        ("Gradients", "gradients"), ("CGI", "graphics-cgi"), ("Lifestyle", "lifestyle"), ("Love", "love"),
        ("Military", "military"), ("Minimal", "minimal"), ("Movies", "movies"), ("Music", "music"),
        ("Nature", "nature"), ("People", "people"), ("Photography", "photography"), ("Quotes", "quotes"),
        ("Sci-Fi", "sci-fi"), ("Space", "space"), ("Sports", "sports"), ("Technology", "technology"),
        ("World", "world"),
    };
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

        BuildChips();

        _ready = true;
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

    private void BuildChips()
    {
        bool first = true;
        foreach (var (name, path) in Categories)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("Chip"),
                Content = name,
                Tag = path,
                GroupName = "cats",
                IsChecked = first
            };
            chip.Checked += Chip_Checked;
            Chips.Children.Add(chip);
            first = false;
        }
    }

    private void Chip_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton rb) return;
        _catPath = rb.Tag as string ?? "";
        _catName = rb.Content as string ?? "";
        _page = 1;
        Reload();
    }

    // Mouse wheel over the chips scrolls them sideways
    private void Chips_Wheel(object sender, MouseWheelEventArgs e)
    {
        ChipsHost.ScrollToHorizontalOffset(ChipsHost.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _tab = TabLib.IsChecked == true ? Tab.Library : Tab.Static;
        _page = 1;
        UpdateChrome();
        Reload();
    }

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_page <= 1) return;
        _page--;
        Reload();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        _page++;
        Reload();
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
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a wallpaper",
            Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            string src = dlg.FileName;
            if (!WallpaperService.IsImage(src))
            {
                ShowStatus("Choose a picture (JPG, PNG or BMP).");
                return;
            }
            string dest = Path.Combine(WallpaperService.StaticDir, Path.GetFileName(src));
            Directory.CreateDirectory(WallpaperService.StaticDir);
            if (!File.Exists(dest))
            {
                ShowStatus("Copying...");
                await Task.Run(() => File.Copy(src, dest));
                ShowStatus("");
            }
            WallpaperManager.Apply(dest, ShowStatus);
            if (_tab == Tab.Library) Reload();
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
        Pager.Visibility = lib ? Visibility.Collapsed : Visibility.Visible;
        ChipsHost.Visibility = lib ? Visibility.Collapsed : Visibility.Visible;
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

    private async void Reload()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        int id = ++_loadId;

        Cards.Children.Clear();
        PageLabel.Text = _tab == Tab.Library ? "Page " + _page : _catName + " \u00B7 Page " + _page;
        PrevBtn.IsEnabled = _page > 1;
        NextBtn.IsEnabled = false;
        RecalcCardSize();

        if (_tab == Tab.Library)
        {
            ShowLibrary();
            return;
        }

        ShowStatus("Loading...");
        try
        {
            var items = await WallpaperService.BrowseSmartAsync(_page, _catPath, ct);
            if (id != _loadId) return;

            if (items.Count == 0)
            {
                ShowStatus(_page > 1 ? "No more results." : "Nothing found.");
                return;
            }

            ShowStatus("");
            NextBtn.IsEnabled = true;
            int i = 0;
            foreach (var it in items) Cards.Children.Add(BuildCard(it, ct, i++));
            Scroll.ScrollToTop();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (id == _loadId)
                ShowStatus(FriendlyError(ex));
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
        thumb.Child = Badge(it.Source == WallSource.Wallhaven && !IsUhd(it.Resolution) ? "HD" : "4K");
        card.Tag = thumb;
        sp.Children.Add(thumb);

        sp.Children.Add(TitleText(it.Title));

        var info = new TextBlock
        {
            Margin = new Thickness(14, 2, 14, 0),
            FontSize = 11.5,
            Foreground = (Brush)FindResource("TextSecondary"),
            Text = it.Source == WallSource.Wallhaven ? "Wallpapers" : "4K Wallpapers",
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
        btnContent.Children.Add(new TextBlock { Text = "Download & set", VerticalAlignment = VerticalAlignment.Center });
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
            if (dl == null)
            {
                OpenInBrowser(it.PageUrl);
                info.Text = "No direct link: page opened in your browser";
                return;
            }

            string dest = Path.Combine(WallpaperService.StaticDir, dl.FileName);

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
            WallpaperManager.Apply(dest, msg => error = msg);
            info.Text = error == null ? "Done: wallpaper set" : "Failed: " + Short(error);
        }
        catch (Exception ex)
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
            return "No internet connection.\nConnect to the internet and press Next/Previous or switch category to try again.";
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
        set.Click += (_, _) => WallpaperManager.Apply(file, ShowStatus);
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
