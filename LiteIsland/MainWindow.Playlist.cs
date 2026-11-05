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
    private Border? _plBarTrack, _plBarFill, _plPlayBtn, _plDlPill;
    private TextBlock? _plNow, _plSub;
    private ScrollViewer? _plScroll;

    private bool _plDownloading, _plDlDone;
    private CancellationTokenSource? _plDlCts;
    private string _plSavedDir = "";

    private bool PlaylistOpen => _pl != null || _plLoading || _plError != null;

    // ---------- open / close ----------

    private void ShowPlaylist(string url, string kind)
    {
        if (_plUrl == url && PlaylistOpen && _plError == null) { OpenTransient(ViewPl, 15000); return; }
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
                    _holdUntil = Math.Max(_holdUntil, Environment.TickCount64 + 20000);
                    RefreshNav();
                    BuildPl();
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

    private void ClosePlaylist(bool goBack)
    {
        try { _plCts?.Cancel(); _plPlayCts?.Cancel(); _plDlCts?.Cancel(); } catch { }
        _plCts = null; _plPlayCts = null; _plDlCts = null;
        _player.Stop();
        bool had = PlaylistOpen;
        _pl = null; _plLoading = false; _plError = null; _plUrl = ""; _plIdx = -1;
        _plPlaying = false; _plTrackLoading = false; _plStatus = ""; _plFails = 0; _plPos = _plDur = 0;
        _plDownloading = false; _plDlDone = false;
        _plRows.Clear();
        _plBarTrack = _plBarFill = _plPlayBtn = _plDlPill = null;
        _plNow = _plSub = null; _plScroll = null;
        PlHost?.Children.Clear();
        PlaylistService.Sweep(Array.Empty<string>());
        if (had) RefreshNav();
        if (goBack && _view == ViewPl)
        {
            if (_expanded) GoToView(ViewMedia, -1); else ShowViewInstant(ViewMedia);
        }
    }

    // ---------- the page ----------

    private void BuildPl()
    {
        if (PlHost == null) return;
        PlHost.Children.Clear();
        _plRows.Clear();
        _plBarTrack = _plBarFill = _plPlayBtn = _plDlPill = null;
        _plNow = _plSub = null; _plScroll = null;

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
        actions.Children.Add(Pill("Close", () => ClosePlaylist(true)));
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

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
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
        Grid.SetColumn(info, 1);
        foot.Children.Add(info);
        Grid.SetRow(foot, 2);
        root.Children.Add(foot);

        PlHost.Children.Add(root);
        PaintPlRows();
        UpdatePlNow();
        UpdatePlProgress();
    }

    private UIElement PlRow(int i, PlTrack t)
    {
        var row = new Border { Height = 28, CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 1) };
        var g = new Grid();
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
        Grid.SetColumn(name, 1);
        g.Children.Add(name);

        if (t.Duration > 0)
        {
            var d = Txt(FmtTime(t.Duration), 11, false, true);
            d.VerticalAlignment = VerticalAlignment.Center;
            d.Margin = new Thickness(6, 0, 8, 0);
            Grid.SetColumn(d, 2);
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
            string path = await PlaylistService.GetAudioShared(t, null, life).WaitAsync(ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(_pl, pl)) return;
            _plFails = 0;
            _plTrackLoading = false;
            _plStatus = "";
            _player.Load(path, t.Title, t.Artist, t.Thumb);
            UpdatePlNow();
            PlaylistService.Sweep(new[] { Key(pl, i - 1), Key(pl, i), Key(pl, i + 1) });
            if (i + 1 < pl.Tracks.Count) _ = PlaylistService.GetAudioShared(pl.Tracks[i + 1], null, life).ContinueWith(_ => { }, TaskScheduler.Default);
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

    private static string Key(PlInfo pl, int i) => i >= 0 && i < pl.Tracks.Count ? pl.Tracks[i].Key : "-";

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
