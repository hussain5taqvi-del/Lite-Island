using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LiteIsland;

// Pages inside the expanded island: Media, Clock + Weather, Calendar, System, Downloads, Clipboard, Timer, Mixer, Shelf.
// Swap pages with the mouse wheel or the dots.
public partial class MainWindow
{
    private const int ViewCount = 9;
    private const int ViewMedia = 0, ViewClock = 1, ViewCal = 2, ViewSys = 3, ViewDl = 4, ViewClip = 5, ViewTimer = 6, ViewMixer = 7, ViewShelf = 8;

    private static readonly FontFamily GlyphFont = new("Segoe MDL2 Assets");
    private static Brush TileBrush = MakeFrozen(new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x28)));
    private static Brush HoverBrush = MakeFrozen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush GreenBrush = MakeFrozen(new SolidColorBrush(Color.FromRgb(0x7C, 0xFF, 0xB2)));
    private static readonly Brush RedBrush = MakeFrozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));
    private static readonly Brush DimBrush = MakeFrozen(new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)));
    private static readonly Brush BadgeBrush = MakeFrozen(new SolidColorBrush(Color.FromArgb(0xE0, 0x55, 0x55, 0x58)));
    private static readonly Brush BadgeHoverBrush = MakeFrozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
    private static readonly Brush SoftBrush = MakeFrozen(new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)));

    private Grid[] _pages = Array.Empty<Grid>();
    private int _view;
    private long _lastWheel;

    // ---------- Page switching ----------

    private void InitViews()
    {
        _pages = new[] { PageMedia, PageClock, PageCal, PageSys, PageDl, PageClip, PageTimer, PageMixer, PageShelf };

        for (int i = 0; i < ViewCount; i++)
        {
            int index = i;
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Margin = new Thickness(3, 0, 3, 0),
                Opacity = i == 0 ? 1.0 : 0.3,
                Cursor = Cursors.Hand
            };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextBrush");
            dot.MouseLeftButtonUp += (_, _) => GoToView(index, index > _view ? 1 : -1);
            Dots.Children.Add(dot);
        }
    }

    private void UpdateDots()
    {
        for (int i = 0; i < Dots.Children.Count; i++)
            Dots.Children[i].Opacity = i == _view ? 1.0 : 0.3;
    }

    private void GoToView(int index, int direction)
    {
        index = ((index % ViewCount) + ViewCount) % ViewCount;
        if (index == _view) return;

        Grid oldPage = _pages[_view];
        Grid newPage = _pages[index];
        _view = index;
        UpdateDots();

        const double shift = 22;

        // The new page is built BEFORE the movement starts (animations run on the clock: a build in the middle
        // would make the first frames jump), and both pages are moved as pictures (cheap) until they settle.
        newPage.Opacity = 0;
        newPage.Visibility = Visibility.Visible;
        OnViewShown(index);
        PageCache(oldPage, true);
        PageCache(newPage, true);
        newPage.UpdateLayout();

        // Old page fades out and slides away
        var oldMove = (TranslateTransform)oldPage.RenderTransform;
        var fadeOut = new DoubleAnimation(0, Dur(130));
        fadeOut.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_pages[_view], oldPage)) oldPage.Visibility = Visibility.Collapsed;
            PageCache(oldPage, false);
        };
        oldPage.BeginAnimation(OpacityProperty, fadeOut);
        oldMove.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-direction * shift, Dur(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        // New page fades in and slides into place
        var newMove = (TranslateTransform)newPage.RenderTransform;
        newPage.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, Dur(220)));
        var slideIn = new DoubleAnimation(direction * shift, 0, Dur(360))
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
        };
        slideIn.Completed += (_, _) => PageCache(newPage, false); // settled: live content again (buttons, bars)
        newMove.BeginAnimation(TranslateTransform.XProperty, slideIn);
    }

    private void PageCache(Grid page, bool on)
    {
        try { page.CacheMode = on ? new BitmapCache(_dpi) { SnapsToDevicePixels = true } : null; }
        catch { }
    }

    // Called when the island is closed (and hidden): go back to the music page without any
    // animation, and stop the timers of the page that was open (System page timer etc.)
    private void ResetToMediaPage()
    {
        if (_view == ViewMedia) return;

        for (int i = 0; i < _pages.Length; i++)
        {
            Grid pg = _pages[i];
            pg.BeginAnimation(OpacityProperty, null);
            PageCache(pg, false);
            if (pg.RenderTransform is TranslateTransform tt)
            {
                tt.BeginAnimation(TranslateTransform.XProperty, null);
                tt.X = 0;
            }
            pg.Opacity = 1;
            pg.Visibility = i == ViewMedia ? Visibility.Visible : Visibility.Collapsed;
        }

        _view = ViewMedia;
        UpdateDots();
        UpdateStatsTimer();
        UpdateProgressTimer();
    }

    // Switches to a page without any animation (used while the island is closed)
    private void ShowViewInstant(int index)
    {
        for (int i = 0; i < _pages.Length; i++)
        {
            Grid pg = _pages[i];
            pg.BeginAnimation(OpacityProperty, null);
            PageCache(pg, false);
            if (pg.RenderTransform is TranslateTransform tt)
            {
                tt.BeginAnimation(TranslateTransform.XProperty, null);
                tt.X = 0;
            }
            pg.Opacity = 1;
            pg.Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }
        _view = index;
        UpdateDots();
        OnViewShown(index);
    }

    private void OnViewShown(int index)
    {
        UpdateStatsTimer(); // measures only while the System page is open
        switch (index)
        {
            case ViewCal: BuildCalendar(); break;
            case ViewClock: UpdateClock(); break;
            case ViewDl: RenderDl(); break;
            case ViewClip: RenderClip(); break;
            case ViewTimer: BuildTimer(); break;
            case ViewMixer: BuildMixer(); break;
            case ViewShelf: BuildShelf(); break;
        }
    }

    // Mouse wheel swaps pages, unless the mouse is over a list that can still scroll
    private void Island_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_expanded || _dropShown || _share != null) return;
        if (IsOverSlider(e.OriginalSource as DependencyObject)) return; // a volume slider handles the wheel itself

        bool up = e.Delta > 0;
        var sv = FindScrollViewer(e.OriginalSource as DependencyObject);
        if (sv != null)
        {
            bool canScroll = up ? sv.VerticalOffset > 0 : sv.VerticalOffset < sv.ScrollableHeight;
            if (canScroll) return; // let the list scroll
        }

        e.Handled = true;

        long now = Environment.TickCount64;
        if (now - _lastWheel < 300) return; // one swap per wheel flick
        _lastWheel = now;

        int dir = up ? -1 : 1;
        GoToView(_view + dir, dir);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ScrollViewer sv) return sv;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // ---------- Drag and drop ----------
    // Drop a file or folder on the island: a QR code appears and the phone downloads it from this PC.
    // Drop with Shift held: the item is added to the apps ring instead.

    private bool _shelfDragging; // a file is being dragged OUT of the shelf: the island must not treat it as a drop

    private void Island_DragEnter(object sender, DragEventArgs e)
    {
        if (_shelfDragging || !e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        OnDragActive();
        SetDropSide(DropSideAt(e));
    }

    private void Island_DragOver(object sender, DragEventArgs e)
    {
        if (_shelfDragging || !e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        _leaveTimer.Stop();
        if (_dropShown) SetDropSide(DropSideAt(e));
    }

    private int DropSideAt(DragEventArgs e) => e.GetPosition(Island).X > Island.ActualWidth / 2 ? 1 : 0;

    private void Island_DragLeave(object sender, DragEventArgs e)
    {
        if (_shelfDragging) return;
        Point p = e.GetPosition(Island);
        bool inside = p.X >= 0 && p.Y >= 0 && p.X < Island.ActualWidth && p.Y < Island.ActualHeight;
        if (inside) return;
        if (_dropShown) ShowDropHint(false);
        if (_holdOpen) return;
        _leaveTimer.Stop();
        _leaveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _leaveTimer.Start();
    }

    private void Island_Drop(object sender, DragEventArgs e)
    {
        if (_shelfDragging) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        int side = DropSideAt(e);

        // Hold Shift while dropping: add to the apps ring.
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0)
        {
            int before = _s.FavoriteApps.Count;
            foreach (string f in files) AddUnique(_s.FavoriteApps, f, 40);
            _s.Save();
            int added = _s.FavoriteApps.Count - before;
            FlashDrop(added > 0 ? "Added to the apps ring" : "Already in the apps ring");
            return;
        }
        // Right half: the shelf. Left half: send to the phone (QR code).
        if (side == 1) { AddToShelf(files); return; }
        StartShare(files);
    }

    private void SendToPhone(string[] files)
    {
        Dispatcher.BeginInvoke(new Action(() => StartShare(files)), DispatcherPriority.Background);
    }

    // ---------- Helpers ----------

    private static void AddUnique(List<string> list, string path, int max)
    {
        if (list.Count >= max) return;
        if (list.Exists(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(path);
    }

    private static void Launch(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // File was moved or deleted, or no app can open it
        }
    }

    // Opens a favorite app or tray file. The island stays open for a moment so a new window
    // covering the island does not make it close at once.
    private void OpenItem(string path)
    {
        Launch(path);
        _holdUntil = Environment.TickCount64 + 2500;

        // Keep the island above the newly opened window
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            Topmost = false;
            Topmost = true;
        };
        t.Start();
    }

    private static void ShowInFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private static string DisplayName(string path)
    {
        string n = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrEmpty(n) ? path : n;
    }

    // Keeps the island open while a context menu is showing
    private void AttachMenu(FrameworkElement el, ContextMenu menu)
    {
        menu.Opened += (_, _) => _holdOpen = true;
        menu.Closed += (_, _) =>
        {
            _holdOpen = false;
            if (!Island.IsMouseOver) Animate(false);
        };
        el.ContextMenu = menu;
    }

    private static TextBlock GlyphBlock(string glyph, double size, Brush? color)
    {
        var tb = new TextBlock
        {
            Text = glyph,
            FontFamily = GlyphFont,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (color != null) tb.Foreground = color;
        else tb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); // follows the text color setting
        return tb;
    }

    private static Grid ThreeColumnRow(double height)
    {
        var row = new Grid { Height = height, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return row;
    }
}
