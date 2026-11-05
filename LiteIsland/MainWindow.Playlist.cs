using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LiteIsland;

// Playlist page: copy a link to a YouTube / Spotify / Apple Music playlist and it appears here. The songs play without
// those apps (the sound comes from YouTube, played by a small helper process that shows up like any music app, so the
// island's music page and the media keys work), and "Save all" keeps them on the PC as mp3.
public partial class MainWindow
{
    private PlInfo? _pl;
    private bool _plLoading;
    private string? _plError;
    private string _plUrl = "";
    private string _plKind = "";
    private int _plIdx = -1;
    private bool _plPlaying, _plTrackLoading;
    private string _plStatus = "";
    private int _plFails;
    private double _plPos, _plDur;
    private CancellationTokenSource? _plCts, _plPlayCts;
    private readonly PlayerHost _player = new();
    private bool _playerWired;

    private readonly List<(Border Row, Border Num)> _plRows = new();
    private readonly List<Border?> _plCovers = new();
    private Border? _plNowCover;
    private CancellationTokenSource? _plCoverCts;
    private Border? _plBarTrack, _plBarFill, _plPlayBtn, _plDlPill;
    private TextBlock? _plNow, _plSub;
    private ScrollViewer? _plScroll;

    private bool _plDownloading, _plDlDone;
    private CancellationTokenSource? _plDlCts;
    private string _plSavedDir = "";

    private bool PlaylistOpen => _pl != null || _plLoading || _plError != null;
    private bool PlaylistVisible => PlaylistOpen || PlaylistService.Library.Count > 0;

    // ---------- open / close ----------

    private void ShowPlaylist(string url, string kind)
    {
        if (_plUrl == url && PlaylistOpen && _plError == null) { OpenTransient(ViewPl, 15000); return; }
        PlInfo? saved = PlaylistService.Library.FirstOrDefault(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase));
        if (saved != null) { OpenSavedPlaylist(saved); return; }
        ClosePlaylist(false);
        _plUrl = url;
        _plKind = kind;
        _plLoading = true;
        _plError = null;
        _plCts = new CancellationTokenSource();
        CancellationToken ct = _plCts.Token;
        RefreshNav();
        OpenTransient(ViewPl, 20000);
        BuildPl();

        _ = Task.Run(async () =>
        {
            try
            {
                PlInfo info = await PlaylistService.FetchAsync(url, kind, s => Dispatcher.InvokeAsync(() => { _plStatus = s; UpdatePlNow(); }), ct);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    _pl = info;
                    _plLoading = false;
                    _plStatus = "";
                    PlaylistService.AddToLibrary(info); // stays after a restart
                    _holdUntil = Math.Max(_holdUntil, Environment.TickCount64 + 20000);
                    RefreshNav();
                    BuildPl();
                    AfterPlaylistReady(info);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    _plLoading = false;
                    _plError = Short(ex.Message, 110);
                    BuildPl();
                });
            }
        });
    }

    /// <summary>A playlist from the saved list: no internet needed to show it.</summary>
    private void OpenSavedPlaylist(PlInfo info)
    {
        StopPlaylist();
        _pl = info;
        _plUrl = info.Url;
        _plKind = info.Kind;
        _plCts = new CancellationTokenSource();
        RefreshNav();
        OpenTransient(ViewPl, 15000);
        BuildPl();
        AfterPlaylistReady(info);
    }

    private void AfterPlaylistReady(PlInfo info)
    {
        CancellationToken life = _plCts?.Token ?? CancellationToken.None;
        // the first songs are made ready now, so "play" starts at once
        for (int i = 0; i < Math.Min(2, info.Tracks.Count); i++)
            _ = PlaylistService.GetAudioShared(info.Tracks[i], null, life).ContinueWith(_ => { }, TaskScheduler.Default);
        _ = LoadPlCoversAsync(info);
    }

    /// <summary>Stops the music and the work, keeps the saved playlists.</summary>
    private void StopPlaylist()
    {
        try { _plCts?.Cancel(); _plPlayCts?.Cancel(); _plDlCts?.Cancel(); _plCoverCts?.Cancel(); } catch { }
        _plCts = null; _plPlayCts = null; _plDlCts = null; _plCoverCts = null;
        _player.Stop();
        _pl = null; _plLoading = false; _plError = null; _plUrl = ""; _plIdx = -1;
        _plPlaying = false; _plTrackLoading = false; _plStatus = ""; _plFails = 0; _plPos = _plDur = 0;
        _plDownloading = false; _plDlDone = false;
        _plRows.Clear(); _plCovers.Clear();
        _plBarTrack = _plBarFill = _plPlayBtn = _plDlPill = _plNowCover = null;
        _plNow = _plSub = null; _plScroll = null;
    }

    private void ClosePlaylist(bool goBack)
    {
        StopPlaylist();
        PlHost?.Children.Clear();
        RefreshNav();
        if (_view != ViewPl) return;
        if (PlaylistService.Library.Count > 0) BuildPl(); // back to the saved list
        else if (goBack)
        {
            if (_expanded) GoToView(ViewMedia, -1); else ShowViewInstant(ViewMedia);
        }
    }

    // ---------- the page ----------

    private void BuildPl()
    {
        if (PlHost == null) return;
        PlHost.Children.Clear();
        _plRows.Clear(); _plCovers.Clear();
        _plBarTrack = _plBarFill = _plPlayBtn = _plDlPill = _plNowCover = null;
        _plNow = _plSub = null; _plScroll = null;
        if (_pl == null && !_plLoading && _plError == null) { BuildPlLibrary(); return; }

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // header: name + source on the left, save / close on the right
        var head = new Grid { Height = 30, Margin = new Thickness(0, 0, 0, 4) };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 120, 0) };
        string title = _pl?.Title ?? (_plLoading ? "Playlist" : "Playlist");
        var tt = Txt(title, 15, true);
        tt.TextTrimming = TextTrimming.CharacterEllipsis;
        titles.Children.Add(tt);
        _plSub = Txt(_pl != null ? _pl.Tracks.Count + " songs · " + _pl.Source : "", 11, false, true);
        titles.Children.Add(_plSub);
        head.Children.Add(titles);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (_pl != null)
        {
            _plDlPill = Pill(PlDlLabel(), PlDownloadToggle);
            _plDlPill.Margin = new Thickness(0, 0, 6, 0);
            actions.Children.Add(_plDlPill);
        }
        actions.Children.Add(Pill(PlaylistService.Library.Count > 0 && _pl != null ? "Back" : "Close", () => ClosePlaylist(true)));
        head.Children.Add(actions);
        root.Children.Add(head);

        if (_pl == null)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (_plError != null)
            {
                var ic = Icons.Make("warn", 26, null, 1.5);
                ic.HorizontalAlignment = HorizontalAlignment.Center;
                sp.Children.Add(ic);
                var msg = Txt(_plError, 12.5, false, true);
                msg.TextAlignment = TextAlignment.Center;
                msg.TextWrapping = TextWrapping.Wrap;
                msg.MaxWidth = 340;
                msg.Margin = new Thickness(0, 8, 0, 8);
                sp.Children.Add(msg);
                var retry = Pill("Try again", () => { string u = _plUrl, k = _plKind; ClosePlaylist(false); ShowPlaylist(u, k); });
                retry.HorizontalAlignment = HorizontalAlignment.Center;
                sp.Children.Add(retry);
            }
            else
            {
                _plNow = Txt(_plStatus.Length > 0 ? _plStatus : "Reading the playlist...", 12.5, false, true);
                _plNow.HorizontalAlignment = HorizontalAlignment.Center;
                var ic = Icons.Make("note", 28, null, 1.5);
                ic.HorizontalAlignment = HorizontalAlignment.Center;
                ic.Opacity = 0.7;
                sp.Children.Add(ic);
                _plNow.Margin = new Thickness(0, 8, 0, 0);
                sp.Children.Add(_plNow);
            }
            Grid.SetRow(sp, 1);
            Grid.SetRowSpan(sp, 2);
            root.Children.Add(sp);
            PlHost.Children.Add(root);
            return;
        }

        // the songs
        var list = new StackPanel();
        for (int i = 0; i < _pl.Tracks.Count; i++) list.Children.Add(PlRow(i, _pl.Tracks[i]));
        _plScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list };
        Grid.SetRow(_plScroll, 1);
        root.Children.Add(_plScroll);

        // controls: previous / play / next, name of the song, progress
        var foot = new Grid { Margin = new Thickness(0, 6, 0, 0), Height = 36 };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var prev = RoundBtn("back", "Previous", () => PlayIndex(Math.Max(0, _plIdx - 1)), false, 30);
        _plPlayBtn = RoundBtn(_plPlaying ? "pause" : "play", "Play / pause", PlToggle, true, 34);
        var next = RoundBtn("back", "Next", PlNext, false, 30);
        next.Child.RenderTransformOrigin = new Point(0.5, 0.5);
        next.Child.RenderTransform = new ScaleTransform(-1, 1);
        btns.Children.Add(prev);
        btns.Children.Add(_plPlayBtn);
        btns.Children.Add(next);
        foot.Children.Add(btns);

        _plNowCover = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Margin = new Thickness(2, 0, 8, 0), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
        _plNowCover.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        Grid.SetColumn(_plNowCover, 1);
        foot.Children.Add(_plNowCover);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _plNow = Txt("", 12.5, true);
        _plNow.TextTrimming = TextTrimming.CharacterEllipsis;
        info.Children.Add(_plNow);
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 5, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        var back = new Border { Height = 4, CornerRadius = new CornerRadius(2) };
        back.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        fill.SetResourceReference(Border.BackgroundProperty, "FillBrush");
        var tg = new Grid();
        tg.Children.Add(back);
        tg.Children.Add(fill);
        track.Child = tg;
        track.Tag = "slider";
        track.MouseLeftButtonUp += (_, e) =>
        {
            if (_plDur <= 0 || track.ActualWidth <= 0) return;
            double r = Math.Clamp(e.GetPosition(track).X / track.ActualWidth, 0, 1);
            _player.Send("SEEK|" + (r * _plDur).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
            e.Handled = true;
        };
        _plBarTrack = track;
        _plBarFill = fill;
        info.Children.Add(track);
        Grid.SetColumn(info, 2);
        foot.Children.Add(info);
        Grid.SetRow(foot, 2);
        root.Children.Add(foot);

        PlHost.Children.Add(root);
        PaintPlRows();
        UpdatePlNow();
        UpdatePlProgress();
        ApplyCoversFromCache();
    }

    // ---------- the saved list ----------

    private void BuildPlLibrary()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new Grid { Height = 30, Margin = new Thickness(0, 0, 0, 4) };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0) };
        titles.Children.Add(Txt("Playlists", 15, true));
        titles.Children.Add(Txt("Copy a playlist link to add one", 11, false, true));
        head.Children.Add(titles);
        root.Children.Add(head);

        var list = new StackPanel();
        foreach (PlInfo p in PlaylistService.Library.ToList())
        {
            PlInfo info = p;
            var row = new Border { Height = 40, CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 3), Background = Brushes.Transparent };
            row.SetResourceReference(Border.BackgroundProperty, "TileBg");
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cover = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
            cover.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
            var note = Icons.Make("note", 14, null, 1.6);
            note.HorizontalAlignment = HorizontalAlignment.Center;
            note.VerticalAlignment = VerticalAlignment.Center;
            note.Opacity = 0.6;
            cover.Child = note;
            g.Children.Add(cover);
            var tx = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var t1 = Txt(info.Title, 13, true);
            t1.TextTrimming = TextTrimming.CharacterEllipsis;
            tx.Children.Add(t1);
            tx.Children.Add(Txt(info.Tracks.Count + " songs · " + info.Source, 11, false, true));
            Grid.SetColumn(tx, 1);
            g.Children.Add(tx);
            var x = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, ToolTip = "Remove from the list", Background = Brushes.Transparent, Opacity = 0.6 };
            var xi = Icons.Make("close", 11, null, 2);
            xi.HorizontalAlignment = HorizontalAlignment.Center;
            xi.VerticalAlignment = VerticalAlignment.Center;
            x.Child = xi;
            x.MouseEnter += (_, _) => x.Opacity = 1;
            x.MouseLeave += (_, _) => x.Opacity = 0.6;
            x.MouseLeftButtonUp += (_, e) => { e.Handled = true; PlaylistService.RemoveFromLibrary(info); RefreshNav(); if (PlaylistService.Library.Count == 0) ClosePlaylist(true); else BuildPl(); };
            Grid.SetColumn(x, 2);
            g.Children.Add(x);
            row.Child = g;
            row.MouseEnter += (_, _) => row.Opacity = 0.85;
            row.MouseLeave += (_, _) => row.Opacity = 1;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenSavedPlaylist(info); };
            list.Children.Add(row);

            // first cover of the playlist (loads in the background)
            if (info.Tracks.Count > 0)
            {
                PlTrack first = info.Tracks[0];
                _ = LoadCoverImageAsync(first, 60, CancellationToken.None).ContinueWith(t =>
                {
                    if (t.Result != null) Dispatcher.InvokeAsync(() => cover.Child = CoverImage(t.Result));
                }, TaskScheduler.Default);
            }
        }
        var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list };
        Grid.SetRow(sv, 1);
        root.Children.Add(sv);
        PlHost.Children.Add(root);
    }

    // ---------- covers ----------

    private static readonly Dictionary<string, ImageSource> CoverCache = new();

    private static FrameworkElement CoverImage(ImageSource src) =>
        new Image { Source = src, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };

    /// <summary>A small cover picture (decoded at its display size, so it costs a few KB).</summary>
    private static async Task<ImageSource?> LoadCoverImageAsync(PlTrack t, int decodeWidth, CancellationToken ct)
    {
        try
        {
            string url = await PlaylistService.CoverUrlAsync(t, ct).ConfigureAwait(false);
            if (url.Length == 0) return null;
            string key = url + "|" + decodeWidth;
            lock (CoverCache) { if (CoverCache.TryGetValue(key, out ImageSource? hit)) return hit; }
            byte[]? b = await PlaylistService.CoverBytesAsync(url, ct).ConfigureAwait(false);
            if (b == null) return null;
            var bi = new BitmapImage();
            using (var ms = new MemoryStream(b))
            {
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = decodeWidth;
                bi.StreamSource = ms;
                bi.EndInit();
            }
            bi.Freeze();
            lock (CoverCache) { if (CoverCache.Count > 300) CoverCache.Clear(); CoverCache[key] = bi; }
            return bi;
        }
        catch { return null; }
    }

    private async Task LoadPlCoversAsync(PlInfo pl)
    {
        _plCoverCts?.Cancel();
        var cts = _plCoverCts = new CancellationTokenSource();
        CancellationToken ct = cts.Token;
        var gate = new SemaphoreSlim(3);
        bool changed = false;
        var tasks = new List<Task>();
        for (int i = 0; i < pl.Tracks.Count; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    string before = pl.Tracks[idx].Cover ?? "";
                    ImageSource? img = await LoadCoverImageAsync(pl.Tracks[idx], 56, ct).ConfigureAwait(false);
                    if (before.Length == 0 && !string.IsNullOrEmpty(pl.Tracks[idx].Cover)) changed = true;
                    if (img != null && !ct.IsCancellationRequested)
                        await Dispatcher.InvokeAsync(() => SetPlCover(pl, idx, img));
                }
                catch { }
                finally { gate.Release(); }
            }, ct));
        }
        try { await Task.WhenAll(tasks); } catch { }
        if (changed && !ct.IsCancellationRequested) PlaylistService.SaveLibrary(); // the found cover urls are kept
    }

    private void SetPlCover(PlInfo pl, int idx, ImageSource img)
    {
        if (!ReferenceEquals(_pl, pl)) return;
        if (idx >= 0 && idx < _plCovers.Count && _plCovers[idx] != null) _plCovers[idx]!.Child = CoverImage(img);
        if (idx == _plIdx && _plNowCover != null) _plNowCover.Child = CoverImage(img);
    }

    // after a page rebuild: put the covers that are already loaded back
    private void ApplyCoversFromCache()
    {
        if (_pl == null) return;
        PlInfo pl = _pl;
        for (int i = 0; i < pl.Tracks.Count && i < _plCovers.Count; i++)
        {
            string? c = pl.Tracks[i].Cover;
            if (string.IsNullOrEmpty(c)) continue;
            lock (CoverCache) { if (CoverCache.TryGetValue(c + "|56", out ImageSource? img) && _plCovers[i] != null) _plCovers[i]!.Child = CoverImage(img); }
        }
        if (_plIdx >= 0 && _plNowCover != null && !string.IsNullOrEmpty(pl.Tracks[_plIdx].Cover))
            lock (CoverCache) { if (CoverCache.TryGetValue(pl.Tracks[_plIdx].Cover + "|56", out ImageSource? img)) _plNowCover.Child = CoverImage(img); }
    }

    private UIElement PlRow(int i, PlTrack t)
    {
        var row = new Border { Height = 28, CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 1) };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var num = new Border { Child = Txt((i + 1).ToString(), 11, false, true) };
        ((TextBlock)num.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)num.Child).VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(num);

        var name = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        name.Inlines.Add(new Run(t.Title));
        if (t.Artist.Length > 0)
        {
            var ar = new Run("   " + t.Artist) { FontSize = 11 };
            ar.SetResourceReference(TextElement.ForegroundProperty, "TextDimBrush");
            name.Inlines.Add(ar);
        }
        Grid.SetColumn(name, 2);
        g.Children.Add(name);

        var cover = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, ClipToBounds = true };
        cover.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        Grid.SetColumn(cover, 1);
        g.Children.Add(cover);
        _plCovers.Add(cover);

        if (t.Duration > 0)
        {
            var d = Txt(FmtTime(t.Duration), 11, false, true);
            d.VerticalAlignment = VerticalAlignment.Center;
            d.Margin = new Thickness(6, 0, 8, 0);
            Grid.SetColumn(d, 3);
            g.Children.Add(d);
        }
        row.Child = g;
        int idx = i;
        row.MouseEnter += (_, _) => { if (idx != _plIdx) row.SetResourceReference(Border.BackgroundProperty, "TileBg"); };
        row.MouseLeave += (_, _) => { if (idx != _plIdx) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; PlayIndex(idx); };
        _plRows.Add((row, num));
        return row;
    }

    private static string FmtTime(double sec)
    {
        int s = (int)Math.Round(Math.Max(0, sec));
        return (s / 60) + ":" + (s % 60).ToString("00");
    }

    private void PaintPlRows()
    {
        for (int i = 0; i < _plRows.Count; i++)
        {
            var (row, num) = _plRows[i];
            bool cur = i == _plIdx;
            if (cur) row.SetResourceReference(Border.BackgroundProperty, "TileBg"); else row.Background = Brushes.Transparent;
            if (cur)
            {
                var ic = Icons.Make(_plPlaying ? "audio" : "play", 12, null, 1.8);
                ic.HorizontalAlignment = HorizontalAlignment.Center;
                ic.VerticalAlignment = VerticalAlignment.Center;
                num.Child = ic;
            }
            else if (num.Child is not TextBlock)
            {
                var tb = Txt((i + 1).ToString(), 11, false, true);
                tb.HorizontalAlignment = HorizontalAlignment.Center;
                tb.VerticalAlignment = VerticalAlignment.Center;
                num.Child = tb;
            }
        }
    }

    private void UpdatePlNow()
    {
        if (_plNow == null) return;
        if (_pl == null) { _plNow.Text = _plStatus.Length > 0 ? _plStatus : "Reading the playlist..."; return; }
        if (_plIdx < 0) { _plNow.Text = "Pick a song"; return; }
        PlTrack t = _pl.Tracks[_plIdx];
        _plNow.Text = _plTrackLoading ? (_plStatus.Length > 0 ? _plStatus : "Loading...") + "  " + t.Title : (_plStatus.Length > 0 ? _plStatus : t.Title);
    }

    private void UpdatePlProgress()
    {
        if (_plBarFill == null || _plBarTrack == null) return;
        double w = _plBarTrack.ActualWidth;
        _plBarFill.Width = _plDur > 0 && w > 0 ? Math.Clamp(_plPos / _plDur, 0, 1) * w : 0;
    }

    private void SetPlPlayIcon()
    {
        if (_plPlayBtn == null) return;
        var ic = Icons.Make(_plPlaying ? "pause" : "play", 17.6, InkDark);
        ic.HorizontalAlignment = HorizontalAlignment.Center;
        ic.VerticalAlignment = VerticalAlignment.Center;
        _plPlayBtn.Child = ic;
    }

    // ---------- playing ----------

    private void WirePlayer()
    {
        if (_playerWired) return;
        _playerWired = true;
        _player.Event += (k, v) => Dispatcher.InvokeAsync(() => OnPlayerEvent(k, v));
    }

    private void OnPlayerEvent(string kind, string val)
    {
        if (_pl == null) return;
        switch (kind)
        {
            case "S":
                _plPlaying = val == "Playing";
                SetPlPlayIcon();
                PaintPlRows();
                break;
            case "T":
            {
                int bar = val.IndexOf('|');
                if (bar > 0 &&
                    double.TryParse(val.AsSpan(0, bar), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double p) &&
                    double.TryParse(val.AsSpan(bar + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d))
                {
                    _plPos = p; _plDur = d > 0 && !double.IsInfinity(d) ? d : 0;
                    if (_expanded && _view == ViewPl) UpdatePlProgress();
                }
                break;
            }
            case "E": PlNext(); break;
            case "N": PlNext(); break;
            case "V": PlayIndex(Math.Max(0, _plIdx - 1)); break;
            case "F":
                if (!_plTrackLoading)
                {
                    _plStatus = Short(val, 60);
                    UpdatePlNow();
                    _plFails++;
                    if (_plFails < 4) PlNext();
                }
                break;
        }
    }

    private void PlToggle()
    {
        if (_pl == null) return;
        if (_plIdx < 0) { PlayIndex(0); return; }
        _player.Send("TOGGLE");
    }

    private void PlNext()
    {
        if (_pl == null) return;
        if (_plIdx + 1 < _pl.Tracks.Count) PlayIndex(_plIdx + 1);
        else { _plPlaying = false; SetPlPlayIcon(); PaintPlRows(); }
    }

    private async void PlayIndex(int i)
    {
        if (_pl == null || i < 0 || i >= _pl.Tracks.Count) return;
        _plPlayCts?.Cancel();
        var cts = _plPlayCts = new CancellationTokenSource();
        CancellationToken ct = cts.Token;
        CancellationToken life = _plCts?.Token ?? CancellationToken.None;
        PlInfo pl = _pl;
        _plIdx = i;
        _plTrackLoading = true;
        _plStatus = "";
        _plPos = _plDur = 0;
        PaintPlRows();
        UpdatePlNow();
        UpdatePlProgress();
        WirePlayer();
        _player.Start();
        PlTrack t = pl.Tracks[i];
        try
        {
            Task<string?> coverFile = Task.Run(async () => { try { return await PlaylistService.CoverFileAsync(t, life); } catch { return null; } });
            string path = await PlaylistService.GetAudioShared(t, null, life).WaitAsync(ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(_pl, pl)) return;
            string? cover = null;
            try { cover = await coverFile.WaitAsync(TimeSpan.FromMilliseconds(1500), ct); } catch (TimeoutException) { }
            if (ct.IsCancellationRequested || !ReferenceEquals(_pl, pl)) return;
            _plFails = 0;
            _plTrackLoading = false;
            _plStatus = "";
            _player.Load(path, t.Title, t.Artist, cover ?? "");
            UpdatePlNow();
            // the next two songs get ready while this one plays
            for (int k = 1; k <= 2 && i + k < pl.Tracks.Count; k++)
                _ = PlaylistService.GetAudioShared(pl.Tracks[i + k], null, life).ContinueWith(_ => { }, TaskScheduler.Default);
            // the cover of this song in the footer
            _ = LoadCoverImageAsync(t, 56, life).ContinueWith(r => { if (r.Result != null) Dispatcher.InvokeAsync(() => SetPlCover(pl, i, r.Result)); }, TaskScheduler.Default);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || !ReferenceEquals(_pl, pl)) return;
            _plFails++;
            _plTrackLoading = false;
            _plStatus = Short(ex.Message, 70);
            UpdatePlNow();
            if (_plFails < 4 && i + 1 < pl.Tracks.Count)
            {
                await Task.Delay(1200);
                if (!ct.IsCancellationRequested && ReferenceEquals(_pl, pl)) PlayIndex(i + 1);
            }
        }
    }

    // ---------- keep the songs on the PC ----------

    private string PlDlLabel() => _plDownloading ? "Stop" : _plDlDone ? "Open folder" : "Save all";

    private void PlDownloadToggle()
    {
        if (_pl == null) return;
        if (_plDownloading) { _plDlCts?.Cancel(); return; }
        if (_plDlDone)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _plSavedDir + "\"") { UseShellExecute = true }); } catch { }
            return;
        }
        _ = SavePlaylistAsync(_pl);
    }

    private async Task SavePlaylistAsync(PlInfo pl)
    {
        _plDownloading = true;
        var cts = _plDlCts = new CancellationTokenSource();
        string dir = Path.Combine(CurrentDownloadDir, PlaylistService.SafeName(pl.Title));
        _plSavedDir = dir;
        int ok = 0, bad = 0;

        void Label(string t)
        {
            if (_plDlPill?.Child is TextBlock tb) tb.Text = t;
        }

        try
        {
            for (int i = 0; i < pl.Tracks.Count; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                Label("Saving " + (i + 1) + "/" + pl.Tracks.Count);
                try { await PlaylistService.SaveTrackAsync(pl.Tracks[i], dir, i + 1, null, cts.Token); ok++; }
                catch (OperationCanceledException) { throw; }
                catch { bad++; }
            }
        }
        catch (OperationCanceledException) { }
        _plDownloading = false;
        if (!ReferenceEquals(_pl, pl)) return;
        _plDlDone = ok > 0 && !cts.IsCancellationRequested;
        Label(PlDlLabel());
        if (ok > 0)
            ShowHud("", cts.IsCancellationRequested ? "Stopped" : "Playlist saved", null, ok + " songs" + (bad > 0 ? ", " + bad + " failed" : ""), 3500);
        else if (!cts.IsCancellationRequested)
            ShowHud("", "Could not save", null, "Check the internet", 3000);
    }
}
