using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LiteIsland;

// The two new pages: Downloads (card for a copied link, then the running / finished jobs) and Clipboard.
public partial class MainWindow
{
    private sealed class RowUi
    {
        public TextBlock Sub = null!;
        public ScaleTransform? Bar;
    }

    private readonly Dictionary<int, RowUi> _rows = new();
    private bool _clipFlash;

    // ---------- Downloads page ----------

    private void RenderDl()
    {
        if (DlHost == null) return;
        DlHost.Children.Clear();
        _rows.Clear();
        DlHost.Children.Add(_linkUrl != null ? BuildLinkCard() : BuildJobList());
    }

    private UIElement BuildJobList()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new Grid { Height = 20, Margin = new Thickness(0, 0, 0, 6) };
        var title = new TextBlock { Text = "Downloads", Style = (Style)FindResource("PageTitle") };
        head.Children.Add(title);
        if (_jobs.Any(j => j.State != JobState.Running && j.State != JobState.Paused))
        {
            var clear = Txt("Clear", 12, true);
            clear.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            clear.Cursor = Cursors.Hand;
            clear.HorizontalAlignment = HorizontalAlignment.Right;
            clear.VerticalAlignment = VerticalAlignment.Center;
            clear.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _jobs.RemoveAll(j => j.State != JobState.Running && j.State != JobState.Paused);
                RenderDl();
            };
            head.Children.Add(clear);
        }
        root.Children.Add(head);

        if (_jobs.Count == 0)
        {
            var hint = new TextBlock
            {
                Style = (Style)FindResource("Hint"),
                Text = "Copy a video link (YouTube, Instagram, X, Facebook ...)\nto download it here.\n\nSelect a file and hold your ring key for more."
            };
            Grid.SetRow(hint, 1);
            root.Children.Add(hint);
            return root;
        }

        var list = new StackPanel();
        foreach (Job j in _jobs) list.Children.Add(BuildJobRow(j));
        var sv = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = list
        };
        Grid.SetRow(sv, 1);
        root.Children.Add(sv);
        return root;
    }

    private static string JobSub(Job j)
    {
        switch (j.State)
        {
            case JobState.Running: return j.Detail.Length > 0 ? j.Detail : "Working…";
            case JobState.Paused: return "Paused";
            case JobState.Canceled: return "Stopped";
            case JobState.Failed: return j.Detail.Length > 0 ? j.Detail : "Failed";
            default:
                string secs = j.Seconds < 10 ? j.Seconds.ToString("0.0", InvC) + " s" : Fmt(j.Seconds);
                return "Done in " + secs + (j.Detail.Length > 0 ? " · " + j.Detail : "");
        }
    }

    private UIElement BuildJobRow(Job j)
    {
        var row = new Grid { MinHeight = 44, Margin = new Thickness(0, 0, 0, 6), Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // picture or type icon
        var tile = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
        if (j.Thumb != null) tile.Background = new ImageBrush(j.Thumb) { Stretch = Stretch.UniformToFill };
        else
        {
            tile.SetResourceReference(Border.BackgroundProperty, "TileBg");
            FrameworkElement ic = Icons.Make(j.Icon, 18, null, 1.7);
            ic.HorizontalAlignment = HorizontalAlignment.Center;
            ic.VerticalAlignment = VerticalAlignment.Center;
            tile.Child = ic;
        }
        row.Children.Add(tile);

        // title, state, progress bar
        var mid = new StackPanel();
        mid.VerticalAlignment = VerticalAlignment.Center;
        var name = Txt(j.Title, 13, true);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        mid.Children.Add(name);

        var ui = new RowUi();
        ui.Sub = Txt(JobSub(j), 11, false, j.State != JobState.Failed);
        if (j.State == JobState.Failed) ui.Sub.Foreground = RedBrush;
        ui.Sub.TextTrimming = TextTrimming.CharacterEllipsis;
        ui.Sub.Margin = new Thickness(0, 1, 0, 0);
        mid.Children.Add(ui.Sub);

        if (j.State == JobState.Running || j.State == JobState.Paused)
        {
            var track = new Grid { Height = 3, Margin = new Thickness(0, 5, 8, 0) };
            var back = new Border { CornerRadius = new CornerRadius(1.5) };
            back.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
            var fill = new Border { CornerRadius = new CornerRadius(1.5), RenderTransformOrigin = new Point(0, 0.5) };
            fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            ui.Bar = new ScaleTransform(j.Progress >= 0 ? j.Progress : 0, 1);
            fill.RenderTransform = ui.Bar;
            track.Children.Add(back);
            track.Children.Add(fill);
            mid.Children.Add(track);
        }
        _rows[j.Id] = ui;
        Grid.SetColumn(mid, 1);
        row.Children.Add(mid);

        // buttons
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (j.State == JobState.Running && j.Kind == "download") btns.Children.Add(SmallBtn("pause", "Pause", () => PauseJob(j)));
        if (j.State == JobState.Paused) btns.Children.Add(SmallBtn("play", "Resume", () => ResumeJob(j)));
        if (j.State == JobState.Running || j.State == JobState.Paused) btns.Children.Add(SmallBtn("stop", "Stop", () => StopJob(j)));
        if (j.State == JobState.Done && !string.IsNullOrEmpty(j.OutPath))
            btns.Children.Add(SmallBtn("folder", "Show in folder", () => ShowInFolder(j.OutPath!)));
        Grid.SetColumn(btns, 2);
        row.Children.Add(btns);

        if (j.State == JobState.Done && !string.IsNullOrEmpty(j.OutPath))
        {
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowInFolder(j.OutPath!); };
        }
        return row;
    }

    private Border SmallBtn(string icon, string tip, Action click)
    {
        Border b = RoundBtn(icon, tip, click, false, 24);
        b.Margin = new Thickness(6, 0, 0, 0);
        return b;
    }

    // A job changed: update its row (or rebuild the list when its state changed)
    private void UpdateJobRow(Job j, bool stateChanged)
    {
        if (!_expanded || _view != ViewDl || _linkUrl != null) return;
        if (stateChanged || !_rows.TryGetValue(j.Id, out RowUi? ui))
        {
            RenderDl();
            return;
        }
        ui.Sub.Text = JobSub(j);
        if (ui.Bar != null && j.Progress >= 0) ui.Bar.ScaleX = Math.Clamp(j.Progress, 0, 1);
    }

    // ---------- Clipboard page ----------

    private void RenderClip()
    {
        if (ClipHost == null) return;
        ClipHost.Children.Clear();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new Grid { Height = 20, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(new TextBlock { Text = "Clipboard", Style = (Style)FindResource("PageTitle") });
        if (_clipFlash)
        {
            var c = Txt("Copied", 12, true);
            c.HorizontalAlignment = HorizontalAlignment.Right;
            c.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(c);
        }
        else if (_clips.Count > 0)
        {
            var clear = Txt("Clear", 12, true);
            clear.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            clear.Cursor = Cursors.Hand;
            clear.HorizontalAlignment = HorizontalAlignment.Right;
            clear.VerticalAlignment = VerticalAlignment.Center;
            clear.MouseLeftButtonUp += (_, e) => { e.Handled = true; _clips.Clear(); RenderClip(); };
            head.Children.Add(clear);
        }
        root.Children.Add(head);

        if (!_s.ClipHistory || _clips.Count == 0)
        {
            var hint = new TextBlock
            {
                Style = (Style)FindResource("Hint"),
                Text = _s.ClipHistory
                    ? "Everything you copy shows up here.\nIt stays in RAM only and is never saved."
                    : "Clipboard history is switched off in Settings."
            };
            Grid.SetRow(hint, 1);
            root.Children.Add(hint);
            ClipHost.Children.Add(root);
            return;
        }

        var list = new StackPanel();
        foreach (ClipItem it in _clips) list.Children.Add(BuildClipRow(it));
        var sv = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = list
        };
        Grid.SetRow(sv, 1);
        root.Children.Add(sv);
        ClipHost.Children.Add(root);
    }

    private static readonly Regex HexColor = new("^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$", RegexOptions.Compiled);

    private UIElement BuildClipRow(ClipItem it)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(6, 5, 6, 5),
            Margin = new Thickness(0, 0, 0, 3),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        string t = it.Text.Trim();
        var tile = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        if (HexColor.IsMatch(t))
        {
            try { tile.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(t)); } catch { }
        }
        else
        {
            tile.SetResourceReference(Border.BackgroundProperty, "TileBg");
            FrameworkElement ic = Icons.Make(t.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "link" : "text", 15, null, 1.7);
            ic.HorizontalAlignment = HorizontalAlignment.Center;
            ic.VerticalAlignment = VerticalAlignment.Center;
            tile.Child = ic;
        }
        g.Children.Add(tile);

        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        string shown = Regex.Replace(t, @"\s+", " ");
        var text = Txt(shown.Length > 140 ? shown.Substring(0, 140) + "…" : shown, 12.5, true);
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.MaxHeight = 34;
        sp.Children.Add(text);
        var meta = Txt(it.Source + " · " + Ago(it.Time), 11, false, true);
        meta.Margin = new Thickness(0, 1, 0, 0);
        sp.Children.Add(meta);
        Grid.SetColumn(sp, 1);
        g.Children.Add(sp);
        row.Child = g;

        row.MouseEnter += (_, _) => row.Background = HoverBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            SetClipboardText(it.Text);
            _clipFlash = true;
            RenderClip();
            await Task.Delay(1200);
            _clipFlash = false;
            if (_expanded && _view == ViewClip) RenderClip();
        };
        return row;
    }

    private static string Ago(DateTime t)
    {
        TimeSpan d = DateTime.Now - t;
        if (d.TotalSeconds < 45) return "just now";
        if (d.TotalMinutes < 60) return ((int)Math.Max(1, d.TotalMinutes)).ToString(InvC) + " min ago";
        if (d.TotalHours < 24) return ((int)d.TotalHours).ToString(InvC) + " hr ago";
        return ((int)d.TotalDays).ToString(InvC) + (d.TotalDays < 2 ? " day ago" : " days ago");
    }
}
