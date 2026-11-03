using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LiteIsland;

// Shelf page: files you drop on the RIGHT side of the island wait here. Click one to open it, drag it out to copy it
// into any folder or app, press x to remove it. Only the file paths are kept (nothing is copied), and only in memory.
public partial class MainWindow
{
    private readonly List<string> _shelf = new();
    private const int ShelfMax = 24;

    private void AddToShelf(string[] files)
    {
        int added = 0;
        foreach (string f in files)
        {
            if (!File.Exists(f) && !Directory.Exists(f)) continue;
            if (_shelf.Contains(f, StringComparer.OrdinalIgnoreCase)) continue;
            _shelf.Insert(0, f);
            added++;
        }
        while (_shelf.Count > ShelfMax) _shelf.RemoveAt(_shelf.Count - 1);
        if (added > 0) OpenTransient(ViewShelf, 6000); // the Shelf page shows up after the message
        FlashDrop(added > 0 ? (added == 1 ? "Added to the shelf" : added + " files added to the shelf") : "Already on the shelf");
        if (_expanded && _view == ViewShelf) BuildShelf();
    }

    private static bool IsOverSlider(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is FrameworkElement fe && fe.Tag is string t && t == "slider") return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void BuildShelf()
    {
        if (ShelfHost == null) return;
        _shelf.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p));
        ShelfHost.Children.Clear();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new Grid { Height = 26, Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(new TextBlock { Text = "Shelf", Style = (Style)FindResource("PageTitle"), VerticalAlignment = VerticalAlignment.Center });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (_shelf.Count > 0)
        {
            var clear = Pill("Clear", () => { _shelf.Clear(); BuildShelf(); });
            clear.Margin = new Thickness(0, 0, 6, 0);
            actions.Children.Add(clear);
        }
        actions.Children.Add(Pill("From phone", StartReceive));
        head.Children.Add(actions);
        root.Children.Add(head);

        if (_shelf.Count == 0)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var ic = Icons.Make("shelf", 30, null, 1.5);
            ic.HorizontalAlignment = HorizontalAlignment.Center;
            ic.Opacity = 0.6;
            sp.Children.Add(ic);
            var hint = new TextBlock { Text = "Drop files on the right side of the island\nto keep them here for later", Style = (Style)FindResource("Hint"), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            sp.Children.Add(hint);
            Grid.SetRow(sp, 1);
            root.Children.Add(sp);
        }
        else
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 0) };
            foreach (string path in _shelf.ToList()) wrap.Children.Add(ShelfTile(path));
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = wrap };
            Grid.SetRow(sv, 1);
            root.Children.Add(sv);
        }
        ShelfHost.Children.Add(root);
    }

    private UIElement ShelfTile(string path)
    {
        var tile = new Border { Width = 84, Height = 84, CornerRadius = new CornerRadius(20), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, ToolTip = path, Background = Brushes.Transparent };
        tile.SetResourceReference(Border.BackgroundProperty, "TileBg");
        var grid = new Grid();
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 4, 6, 4) };

        ImageSource? img = Directory.Exists(path) ? null : IconHelper.Get(path);
        FrameworkElement icon;
        if (img != null) icon = new Image { Source = img, Width = 32, Height = 32 };
        else icon = Icons.Make(Directory.Exists(path) ? "merge" : "file", 28, null, 1.5);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(icon);
        var name = Txt(Path.GetFileName(path.TrimEnd('\\', '/')), 10.5, false, true);
        name.TextAlignment = TextAlignment.Center;
        name.TextWrapping = TextWrapping.Wrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.MaxHeight = 28;
        name.Margin = new Thickness(0, 5, 0, 0);
        sp.Children.Add(name);
        grid.Children.Add(sp);

        // remove button (shows on hover)
        var x = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 5, 0), Opacity = 0, Cursor = Cursors.Hand };
        x.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        var xi = Icons.Make("plus", 10, null, 2.2);
        xi.HorizontalAlignment = HorizontalAlignment.Center;
        xi.VerticalAlignment = VerticalAlignment.Center;
        xi.RenderTransformOrigin = new Point(0.5, 0.5);
        xi.RenderTransform = new RotateTransform(45);
        x.Child = xi;
        x.MouseLeftButtonUp += (_, e) => { e.Handled = true; _shelf.Remove(path); BuildShelf(); };
        grid.Children.Add(x);
        tile.Child = grid;

        tile.MouseEnter += (_, _) => { tile.Background = HoverBrush; x.Opacity = 1; };
        tile.MouseLeave += (_, _) => { tile.SetResourceReference(Border.BackgroundProperty, "TileBg"); x.Opacity = 0; };

        Point down = default;
        bool pressed = false;
        tile.PreviewMouseLeftButtonDown += (_, e) => { down = e.GetPosition(tile); pressed = true; };
        tile.MouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
            Point p = e.GetPosition(tile);
            if (Math.Abs(p.X - down.X) < 8 && Math.Abs(p.Y - down.Y) < 8) return;
            pressed = false;
            _shelfDragging = true;
            try { DragDrop.DoDragDrop(tile, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy); }
            catch { }
            finally { _shelfDragging = false; }
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            pressed = false;
            e.Handled = true;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { ShowHud("", "Could not open it", null, null, 2000); }
        };
        AddPress(tile, 0.96);
        return tile;
    }
}
