using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LiteIsland;

// Copy a link (YouTube, Instagram, X, Facebook ...): the island opens a download card with the quality choice.
// Also the clipboard history list that is fed by the same clipboard listener.
public partial class MainWindow
{
    private sealed class ClipItem
    {
        public string Text = "";
        public string Source = "";
        public DateTime Time;
    }

    private readonly List<ClipItem> _clips = new();
    private string? _linkUrl;
    private LinkInfo? _link;
    private string _linkStatus = "";
    private bool _linkLoading, _linkFailed, _linkOpts;
    private DlOptions _dl = new();
    private CancellationTokenSource? _linkCts;
    private TextBlock? _linkStatusText;

    private static readonly Brush InkDark = MakeFrozen(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)));
    private static readonly int[] AutoDeleteSteps = { 0, 60, 1440, 10080 };

    // ---------- Clipboard events ----------

    private void OnClipboardText(string text, string source)
    {
        if (_s.ClipHistory)
        {
            AddClip(text, source);
            if (_expanded && _view == ViewClip) RenderClip();
            // A copied text pops the Clipboard page up for a moment (links open the Downloads page below)
            bool isLink = _s.LinkDetect && Downloader.TryGetLink(text, _s.LinkAnySite, out _);
            if (!isLink && !_expanded) OpenTransient(ViewClip, 5000);
        }

        if (_s.LinkDetect && Downloader.TryGetLink(text, _s.LinkAnySite, out string url))
        {
            if (url == _linkUrl && (_linkLoading || _link != null || _linkFailed)) return; // same link again
            ShowLink(url);
        }
    }

    private void AddClip(string text, string source)
    {
        if (text.Length > 2000) text = text.Substring(0, 2000);
        _clips.RemoveAll(c => c.Text == text);
        _clips.Insert(0, new ClipItem { Text = text, Source = source, Time = DateTime.Now });
        while (_clips.Count > 25) _clips.RemoveAt(_clips.Count - 1);
    }

    // ---------- Opening the card ----------

    private void ShowLink(string url)
    {
        _linkCts?.Cancel();
        _linkCts = new CancellationTokenSource();
        _linkUrl = url;
        _link = null;
        _linkLoading = true;
        _linkFailed = false;
        _linkOpts = false;
        _linkStatus = "Looking up the link...";
        _dl = new DlOptions { Height = _s.DefaultQuality, Dir = CurrentDownloadDir };

        // Links of sites that are not well known are checked quietly first: the island only opens when a video is really there
        bool quiet = !Downloader.IsKnownHost(url);
        if (!quiet)
        {
            // The Download button is there at once (no waiting): the card starts with the site name and
            // fills in the title, picture and qualities a moment later.
            var ph = new LinkInfo { Url = url, Title = Downloader.HostOf(url) };
            _link = ph;
            _linkLoading = false;
            _dl.Height = Downloader.PickQuality(_s.DefaultQuality, Downloader.Qualities(0));
            OpenLinkPage();
            _ = FillLinkAsync(ph, _linkCts.Token);
            return;
        }

        _ = LoadLinkAsync(url, _linkCts.Token, quiet);
    }

    // Fills the instant card in place. Failures change nothing: the button already works and the download reports its own problem.
    private async Task FillLinkAsync(LinkInfo ph, CancellationToken ct)
    {
        string url = ph.Url;
        void Copy(LinkInfo from)
        {
            if (!string.IsNullOrEmpty(from.Title)) ph.Title = from.Title;
            if (!string.IsNullOrEmpty(from.Uploader)) ph.Uploader = from.Uploader;
            if (!string.IsNullOrEmpty(from.ThumbUrl)) ph.ThumbUrl = from.ThumbUrl;
            if (!string.IsNullOrEmpty(from.Id)) ph.Id = from.Id;
            if (!string.IsNullOrEmpty(from.Extractor)) ph.Extractor = from.Extractor;
            if (from.Duration > 0) ph.Duration = from.Duration;
            if (from.MaxHeight > 0) ph.MaxHeight = from.MaxHeight;
        }

        void Refresh()
        {
            if (ct.IsCancellationRequested || !ReferenceEquals(_link, ph)) return;
            int[] q = Downloader.Qualities(ph.MaxHeight);
            if (Array.IndexOf(q, _dl.Height) < 0) _dl.Height = Downloader.PickQuality(_s.DefaultQuality, q);
            _holdUntil = Math.Max(_holdUntil, Environment.TickCount64 + 15000);
            if (!_linkOpts) RenderDl();
        }

        try
        {
            LinkInfo? quick = await Downloader.QuickInfoAsync(url, ct);
            if (quick != null)
            {
                Copy(quick);
                ph.Thumb = await Downloader.LoadThumbAsync(ph, ct);
                Refresh();
            }

            LinkInfo full = await Downloader.FetchAsync(url, null, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(_link, ph)) return;
            Copy(full);
            if (ph.Thumb == null) ph.Thumb = await Downloader.LoadThumbAsync(ph, ct);
            Refresh();
        }
        catch (OperationCanceledException) { }
        catch { /* keep the card as it is */ }
    }

    // Open the island on the Downloads page. It closes by itself again if the cursor never comes near it.
    private void OpenLinkPage() => OpenTransient(ViewDl, 22000);

    private void OpenTransient(int view, int holdMs)
    {
        _leaveTimer.Stop();
        _holdUntil = Environment.TickCount64 + holdMs;
        if (!_expanded)
        {
            ShowViewInstant(view); // no page slide on top of the island opening
            Animate(true);
        }
        else if (_view != view) GoToView(view, 1);
        else OnViewShown(view);
        _leaveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _leaveTimer.Start();
    }

    private async Task LoadLinkAsync(string url, CancellationToken ct, bool quiet)
    {
        try
        {
            // YouTube: the card opens at once, the length / best quality follow a moment later
            LinkInfo? quick = quiet ? null : await Downloader.QuickInfoAsync(url, ct);
            if (quick != null)
            {
                quick.Thumb = await Downloader.LoadThumbAsync(quick, ct);
                if (ct.IsCancellationRequested) return;
                _dl.Height = Downloader.PickQuality(_s.DefaultQuality, Downloader.Qualities(0));
                _link = quick;
                _linkLoading = false;
                _holdUntil = Math.Max(_holdUntil, Environment.TickCount64 + 15000);
                RenderDl();

                try
                {
                    LinkInfo full = await Downloader.FetchAsync(url, null, ct);
                    if (ct.IsCancellationRequested || !ReferenceEquals(_link, quick)) return;
                    quick.Duration = full.Duration;
                    quick.MaxHeight = full.MaxHeight;
                    int[] q2 = Downloader.Qualities(quick.MaxHeight);
                    if (Array.IndexOf(q2, _dl.Height) < 0) _dl.Height = Downloader.PickQuality(_s.DefaultQuality, q2);
                    if (!_linkOpts) RenderDl();
                }
                catch (OperationCanceledException) { throw; }
                catch { /* the card stays as it is; the download itself will report a problem */ }
                return;
            }

            LinkInfo info = await Downloader.FetchAsync(url, s =>
                Dispatcher.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    _linkStatus = s;
                    if (_linkStatusText != null) _linkStatusText.Text = s;
                }), ct);

            info.Thumb = await Downloader.LoadThumbAsync(info, ct);
            if (ct.IsCancellationRequested) return;

            int[] q = Downloader.Qualities(info.MaxHeight);
            _dl.Height = Downloader.PickQuality(_s.DefaultQuality, q);
            _link = info;
            _linkLoading = false;
            if (quiet) OpenLinkPage();
            _holdUntil = Math.Max(_holdUntil, Environment.TickCount64 + 15000);
            RenderDl();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            if (quiet)
            {
                // not a video link: say nothing
                _linkLoading = false;
                _linkUrl = null;
                return;
            }
            _linkLoading = false;
            _linkFailed = true;
            _linkStatus = Short(ex.Message, 80);
            RenderDl();
        }
    }

    private void CloseLink()
    {
        _linkCts?.Cancel();
        _linkUrl = null;
        _link = null;
        _linkLoading = false;
        _linkFailed = false;
        RenderDl();
        // Nothing left to show: the pop-up closes by itself when the cursor is not on the island
        _holdUntil = 0;
        _leaveTimer.Stop();
        _leaveTimer.Interval = TimeSpan.FromMilliseconds(500);
        _leaveTimer.Start();
    }

    // ---------- Starting the download ----------

    private void StartDownloadFromCard()
    {
        if (_link == null) return;
        LinkInfo info = _link;
        var o = new DlOptions
        {
            Height = _dl.Height,
            AudioOnly = _dl.AudioOnly,
            Subs = _dl.Subs,
            Dir = CurrentDownloadDir,
            AutoDeleteMinutes = _dl.AutoDeleteMinutes,
            LimitMBps = _s.DlLimitMBps
        };

        if (!o.AudioOnly) { _s.DefaultQuality = o.Height; _s.Save(); }

        Job j = NewJob("download", info.Title, o.AudioOnly ? "audio" : "video");
        j.Thumb = info.Thumb;
        j.Detail = "Starting...";
        j.Restart = job => RunDownload(job, info, o);

        _linkCts?.Cancel();
        _linkUrl = null;
        _link = null;
        RenderDl();

        _ = RunDownload(j, info, o);

        // The island goes back to its small shape: the download runs quietly in the background
        // (progress is on the Downloads page, a message appears when it is done)
        _holdUntil = 0;
        _holdOpen = false;
        if (_expanded) Animate(false);
        ShowHud("\uE896", "Download started", null, null, 1600);
    }

    private readonly SemaphoreSlim _dlGate = new(1, 1);   // one download at a time: they would only share the same bandwidth

    private async Task RunDownload(Job j, LinkInfo info, DlOptions o)
    {
        o.LimitMBps = _s.DlLimitMBps;
        j.Cts = new CancellationTokenSource();
        j.State = JobState.Running;
        j.StartTick = Environment.TickCount64;
        j.Progress = -1;
        OnJobChanged(j, true);
        CancellationToken ct = j.Cts.Token;

        string? file = null;
        var errors = new List<string>();
        bool gated = false;
        try
        {
            Directory.CreateDirectory(o.Dir);
            if (_dlGate.CurrentCount == 0) { j.Detail = "Waiting for the other download..."; Tick(j, -1); }
            await _dlGate.WaitAsync(ct);
            gated = true;
            Action<string> status = s => { j.Detail = s; Tick(j, -1); };
            await Tools.EnsureYtDlpAsync(status, ct);
            await Tools.EnsureFFmpegAsync(status, ct);
            if (Downloader.HostOf(info.Url).Contains("youtu")) await Tools.EnsureDenoAsync(status, ct);
            j.Cache ??= Tools.NewJobCache();
            o.TempDir = j.Cache;
            j.Detail = "Starting...";

            // Up to three tries, each a little different, so a video does not fail because one thing was off:
            // 1) normal  2) after refreshing yt-dlp, with a relaxed format  3) IPv4 and other clients
            int code = 0;
            string lastError = "";
            for (int attempt = 0; attempt < 3; attempt++)
            {
                lock (errors) errors.Clear();
                file = null;
                o.Attempt = attempt;
                if (attempt == 1) { j.Detail = "Refreshing the downloader..."; Tick(j, -1); await Tools.UpdateYtDlpNowAsync(ct); }
                else if (attempt == 2) { j.Detail = "Trying another way..."; Tick(j, -1); }

                code = await Proc.RunAsync(Tools.YtDlp, Downloader.BuildArgs(info, o),
                    line =>
                    {
                        if (line.StartsWith("[dl] ", StringComparison.Ordinal)) OnDlLine(j, line.Substring(5));
                        else if (line.StartsWith("[file] ", StringComparison.Ordinal)) file = line.Substring(7).Trim();
                    },
                    line => { lock (errors) errors.Add(line); },
                    ct);
                if (code == 0) break;

                List<string> copy;
                lock (errors) copy = errors.ToList();
                lastError = Downloader.FriendlyError(copy);
                if (!Downloader.Retryable(lastError) || attempt == 2) break;

                // start the next try from a clean cache folder (a half-written part must not confuse it)
                Tools.DeleteCache(j.Cache);
                j.Cache = Tools.NewJobCache();
                o.TempDir = j.Cache;
            }

            if (code != 0) throw new InvalidOperationException(lastError);

            if (file == null || !File.Exists(file))
            {
                // yt-dlp did not report the name: take the newest file in the folder
                file = new DirectoryInfo(o.Dir).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
            }

            if (file != null && File.Exists(file) && new FileInfo(file).Length == 0)
            {
                try { File.Delete(file); } catch { }
                throw new InvalidOperationException("The site sent an empty file, try another quality");
            }

            j.OutPath = file;
            j.OutBytes = file != null && File.Exists(file) ? new FileInfo(file).Length : 0;
            j.Progress = 1;
            j.Detail = j.OutBytes > 0 ? Size(j.OutBytes) : "";

            if (o.AutoDeleteMinutes > 0 && file != null)
            {
                _s.AutoDelete.Add(new AutoDeleteItem { Path = file, DueUtcTicks = DateTime.UtcNow.AddMinutes(o.AutoDeleteMinutes).Ticks });
                _s.Save();
            }
            FinishJob(j, JobState.Done);
        }
        catch (OperationCanceledException)
        {
            if (j.State == JobState.Running) FinishJob(j, JobState.Canceled, "Stopped"); // paused jobs keep their state
        }
        catch (Exception ex)
        {
            FinishJob(j, JobState.Failed, Short(ex.Message, 90));
        }
        finally
        {
            if (j.State != JobState.Paused) { Tools.DeleteCache(j.Cache); j.Cache = null; }
            if (gated) _dlGate.Release();
        }
    }

    // "[dl] downloaded total estimate speed eta" (numbers, or NA)
    private void OnDlLine(Job j, string rest)
    {
        string[] t = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double V(int i) => i < t.Length && double.TryParse(t[i], NumberStyles.Float, InvC, out double v) ? v : double.NaN;

        double got = V(0), tot = V(1), est = V(2), spd = V(3), eta = V(4);
        double total = !double.IsNaN(tot) && tot > 0 ? tot : (!double.IsNaN(est) && est > 0 ? est : 0);
        double frac = total > 0 && !double.IsNaN(got) ? Math.Min(0.99, got / total) : -1;

        string d = frac >= 0 ? ((int)(frac * 100)).ToString(InvC) + "%" : "";
        if (!double.IsNaN(spd) && spd > 0) d += (d.Length > 0 ? " · " : "") + Rate(spd);
        if (!double.IsNaN(eta) && eta >= 0) d += (d.Length > 0 ? " · " : "") + Eta(eta) + " left";
        j.Detail = d.Length > 0 ? d : "Downloading...";
        Tick(j, frac);
    }

    private static string Eta(double s)
    {
        int n = (int)Math.Round(s);
        if (n < 60) return n.ToString(InvC) + " s";
        if (n < 3600) return (n / 60).ToString(InvC) + " min " + (n % 60).ToString(InvC) + " s";
        return (n / 3600).ToString(InvC) + " h " + (n % 3600 / 60).ToString(InvC) + " min";
    }

    private void PauseJob(Job j)
    {
        if (j.State != JobState.Running) return;
        j.State = JobState.Paused;
        j.Detail = "Paused";
        try { j.Cts.Cancel(); } catch { }
        OnJobChanged(j, true);
    }

    private void ResumeJob(Job j)
    {
        if (j.State != JobState.Paused || j.Restart == null) return;
        _ = j.Restart(j);
    }

    private void StopJob(Job j)
    {
        if (j.State == JobState.Running)
        {
            try { j.Cts.Cancel(); } catch { }  // the job's own handler marks it "Stopped"
        }
        else if (j.State == JobState.Paused)
        {
            FinishJob(j, JobState.Canceled, "Stopped");
        }
    }

    // ---------- UI helpers ----------

    private TextBlock Txt(string text, double size, bool bold = false, bool dim = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        t.SetResourceReference(TextBlock.ForegroundProperty, dim ? "TextDimBrush" : "TextBrush");
        return t;
    }

    /// <summary>Buttons shrink a little while pressed and spring back (like iOS).</summary>
    private void AddPress(FrameworkElement b, double to = 0.95)
    {
        var st = new ScaleTransform(1, 1);
        b.RenderTransformOrigin = new Point(0.5, 0.5);
        if (b.RenderTransform != null && b.RenderTransform != Transform.Identity) return; // keep an existing transform
        b.RenderTransform = st;
        b.PreviewMouseLeftButtonDown += (_, _) => Scale(st, to, 80, false);
        b.PreviewMouseLeftButtonUp += (_, _) => Scale(st, 1, 260, true);
        b.MouseLeave += (_, _) => { if (st.ScaleX < 0.999) Scale(st, 1, 200, true); };
    }

    private static void Scale(ScaleTransform st, double to, double ms, bool spring)
    {
        IEasingFunction ease = spring ? new SpringEase { Damping = 0.55 } : new CubicEase { EasingMode = EasingMode.EaseOut };
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private Border RoundBtn(string icon, string tip, Action click, bool active = false, double size = 30)
    {
        var b = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Opacity = 0.9,
            Margin = new Thickness(0, 0, 8, 0)
        };
        b.SetResourceReference(Border.BackgroundProperty, active ? "AccentBrush" : "TileBg");
        FrameworkElement ic = Icons.Make(icon, size * 0.52, active ? InkDark : null);
        ic.HorizontalAlignment = HorizontalAlignment.Center;
        ic.VerticalAlignment = VerticalAlignment.Center;
        b.Child = ic;
        b.MouseEnter += (_, _) => b.Opacity = 1;
        b.MouseLeave += (_, _) => b.Opacity = 0.9;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        AddPress(b, 0.9);
        return b;
    }

    private Brush OnFillBrush => _s.Theme == "Light" ? Brushes.White : InkDark;

    // White (dark theme) or black (light theme) pill with an inverse text
    private Border PillButton(UIElement content, Action click)
    {
        var b = new Border
        {
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(14, 0, 14, 0),
            Cursor = Cursors.Hand,
            Child = content,
            Opacity = 0.95
        };
        b.SetResourceReference(Border.BackgroundProperty, "FillBrush");
        b.MouseEnter += (_, _) => b.Opacity = 1;
        b.MouseLeave += (_, _) => b.Opacity = 0.95;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        AddPress(b);
        return b;
    }

    private Border DownloadPill()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement ic = Icons.Make("download", 14, OnFillBrush, 2);
        ic.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(ic);
        sp.Children.Add(new TextBlock
        {
            Text = _dl.AudioOnly ? "MP3" : Downloader.QualityLabel(_dl.Height),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = OnFillBrush,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        return PillButton(sp, StartDownloadFromCard);
    }

    private Border Chip(UIElement content, Action click, bool selected = false)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            Child = content,
            MinHeight = 28
        };
        b.SetResourceReference(Border.BackgroundProperty, selected ? "FillBrush" : "TileBg");
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        AddPress(b);
        return b;
    }

    private TextBlock ChipText(string text, bool selected, double size = 12.5)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Foreground = selected ? OnFillBrush : (Brush)FindResource("TextBrush")
        };
    }

    private static string AutoDeleteLabel(int minutes) => minutes switch
    {
        0 => "Keep",
        60 => "1 hour",
        1440 => "1 day",
        _ => "7 days"
    };

    private void CycleAutoDelete()
    {
        int i = Array.IndexOf(AutoDeleteSteps, _dl.AutoDeleteMinutes);
        _dl.AutoDeleteMinutes = AutoDeleteSteps[(i + 1) % AutoDeleteSteps.Length];
        RenderDl();
    }

    private void OpenDownloadFolder()
    {
        try { Directory.CreateDirectory(CurrentDownloadDir); } catch { }
        Launch(CurrentDownloadDir);
        _holdUntil = Environment.TickCount64 + 2500;
    }

    private void ChooseDownloadFolder()
    {
        _holdOpen = true;
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Download folder", InitialDirectory = CurrentDownloadDir };
            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                _s.DownloadDir = dlg.FolderName;
                _dl.Dir = dlg.FolderName;
                _s.Save();
            }
        }
        catch { }
        _holdOpen = false;
        if (!Island.IsMouseOver) { _leaveTimer.Stop(); _holdUntil = Environment.TickCount64 + 6000; _leaveTimer.Start(); }
        RenderDl();
    }

    // ---------- The card ----------

    private UIElement BuildLinkCard()
    {
        _linkStatusText = null;
        if (_linkLoading || _linkFailed || _link == null) return BuildLinkStatus();
        return _linkOpts ? BuildLinkOptions() : BuildLinkCompact();
    }

    private UIElement BuildLinkStatus()
    {
        var g = new Grid();
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };

        if (_linkLoading)
        {
            FrameworkElement spin = Icons.Make("spinner", 26, (Brush)FindResource("AccentBrush"), 2.4);
            spin.HorizontalAlignment = HorizontalAlignment.Center;
            var rot = new RotateTransform();
            spin.RenderTransform = rot;
            spin.RenderTransformOrigin = new Point(0.5, 0.5);
            rot.BeginAnimation(RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
                { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
            sp.Children.Add(spin);
        }
        else
        {
            FrameworkElement w = Icons.Make("warn", 26, RedBrush, 2);
            w.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(w);
        }

        var host = Txt(Downloader.HostOf(_linkUrl ?? ""), 12, true, true);
        host.HorizontalAlignment = HorizontalAlignment.Center;
        host.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(host);

        var st = Txt(_linkStatus, 13, true);
        st.HorizontalAlignment = HorizontalAlignment.Center;
        st.TextAlignment = TextAlignment.Center;
        st.TextWrapping = TextWrapping.Wrap;
        st.MaxWidth = 320;
        st.Margin = new Thickness(0, 3, 0, 0);
        sp.Children.Add(st);
        _linkStatusText = st;

        if (_linkFailed)
        {
            string url = _linkUrl ?? "";
            var retry = new Border
            {
                CornerRadius = new CornerRadius(13),
                Padding = new Thickness(14, 4, 14, 4),
                Margin = new Thickness(0, 9, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand,
                Child = ChipText("Try again", false)
            };
            retry.SetResourceReference(Border.BackgroundProperty, "TileBg");
            retry.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowLink(url); };
            sp.Children.Add(retry);
        }

        g.Children.Add(sp);
        FrameworkElement x = RoundBtn("close", "Close", CloseLink, false, 22);
        x.HorizontalAlignment = HorizontalAlignment.Right;
        x.VerticalAlignment = VerticalAlignment.Top;
        x.Margin = new Thickness(0);
        g.Children.Add(x);
        return g;
    }

    private UIElement BuildLinkCompact()
    {
        LinkInfo info = _link!;
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // top: picture + texts
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var thumb = new Border { Width = 132, Height = 74, CornerRadius = new CornerRadius(10), ClipToBounds = true };
        if (info.Thumb != null) thumb.Background = new ImageBrush(info.Thumb) { Stretch = Stretch.UniformToFill };
        else thumb.SetResourceReference(Border.BackgroundProperty, "TileBg");
        var tg = new Grid();
        if (info.Thumb == null)
        {
            FrameworkElement v = Icons.Make("video", 26, null, 1.6);
            v.HorizontalAlignment = HorizontalAlignment.Center;
            v.VerticalAlignment = VerticalAlignment.Center;
            v.Opacity = 0.5;
            tg.Children.Add(v);
        }
        if (info.Duration > 0)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xB8, 0, 0, 0)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(5, 1, 5, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 5, 5),
                Child = new TextBlock { Text = Fmt(info.Duration), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White }
            };
            tg.Children.Add(badge);
        }
        thumb.Child = tg;
        top.Children.Add(thumb);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 26, 0) };
        var site = new StackPanel { Orientation = Orientation.Horizontal };
        FrameworkElement li = Icons.Make("link", 11, null, 2);
        li.Opacity = 0.6;
        li.VerticalAlignment = VerticalAlignment.Center;
        site.Children.Add(li);
        var hostT = Txt(info.Host, 11, false, true);
        hostT.Margin = new Thickness(4, 0, 0, 0);
        site.Children.Add(hostT);
        texts.Children.Add(site);

        var title = Txt(info.Title, 14, true);
        title.TextWrapping = TextWrapping.Wrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.MaxHeight = 38;
        title.Margin = new Thickness(0, 3, 0, 0);
        texts.Children.Add(title);

        string sub = info.Uploader;
        if (info.MaxHeight > 0) sub += (sub.Length > 0 ? " · " : "") + "Up to " + Downloader.QualityLabel(info.MaxHeight);
        if (sub.Length > 0)
        {
            var subT = Txt(sub, 11.5, false, true);
            subT.TextTrimming = TextTrimming.CharacterEllipsis;
            subT.Margin = new Thickness(0, 3, 0, 0);
            texts.Children.Add(subT);
        }
        Grid.SetColumn(texts, 1);
        top.Children.Add(texts);
        g.Children.Add(top);

        FrameworkElement close = RoundBtn("close", "Close", CloseLink, false, 22);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(0);
        g.Children.Add(close);

        // bottom: tools on the left, download on the right
        var bottom = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(RoundBtn("note", "Audio only (MP3)", () => { _dl.AudioOnly = !_dl.AudioOnly; RenderDl(); }, _dl.AudioOnly));
        tools.Children.Add(RoundBtn("timer", "Auto-delete: " + AutoDeleteLabel(_dl.AutoDeleteMinutes) + " (click to change)",
            CycleAutoDelete, _dl.AutoDeleteMinutes > 0));
        tools.Children.Add(RoundBtn("folder", "Open the download folder", OpenDownloadFolder));
        bottom.Children.Add(tools);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(DownloadPill());
        FrameworkElement more = RoundBtn("chevron", "Quality and more", () => { _linkOpts = true; RenderDl(); });
        more.Margin = new Thickness(6, 0, 0, 0);
        right.Children.Add(more);
        Grid.SetColumn(right, 1);
        bottom.Children.Add(right);

        Grid.SetRow(bottom, 3);
        g.Children.Add(bottom);
        return g;
    }

    private Border TimeBox(string value, string hint, Action<string> set)
    {
        var tb = new TextBox
        {
            Width = 54,
            FontSize = 12,
            Text = value,
            ToolTip = hint,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(2, 2, 2, 2)
        };
        tb.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        tb.GotKeyboardFocus += (_, _) => { _holdOpen = true; };
        tb.LostKeyboardFocus += (_, _) =>
        {
            _holdOpen = false;
            _holdUntil = Environment.TickCount64 + 5000;
            _leaveTimer.Stop();
            _leaveTimer.Start();
        };
        tb.PreviewMouseLeftButtonDown += (_, _) => GlassWindow.ForceForeground(this); // typing needs real focus
        tb.TextChanged += (_, _) => set(tb.Text);

        var b = new Border { CornerRadius = new CornerRadius(7), Child = tb };
        b.SetResourceReference(Border.BackgroundProperty, "TileBg");
        return b;
    }

    private UIElement BuildLinkOptions()
    {
        LinkInfo info = _link!;
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Save as: Video | Audio Only        MP4 / MP3
        var r1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 7) };
        r1.Children.Add(OptLabel("Save as"));
        r1.Children.Add(Chip(IconText("video", "Video", !_dl.AudioOnly), () => { _dl.AudioOnly = false; RenderDl(); }, !_dl.AudioOnly));
        r1.Children.Add(Chip(IconText("audio", "Audio Only", _dl.AudioOnly), () => { _dl.AudioOnly = true; RenderDl(); }, _dl.AudioOnly));
        var fmt = Chip(ChipText(_dl.AudioOnly ? "MP3" : "MP4", false), () => { }, false);
        fmt.Cursor = Cursors.Arrow;
        r1.Children.Add(fmt);
        g.Children.Add(r1);

        // Quality: 720p ... best the video has
        var r2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 7) };
        r2.Children.Add(OptLabel("Quality"));
        if (_dl.AudioOnly)
        {
            var best = Txt("Best available", 12.5, false, true);
            best.VerticalAlignment = VerticalAlignment.Center;
            r2.Children.Add(best);
        }
        else
        {
            foreach (int h in Downloader.Qualities(info.MaxHeight))
            {
                int hh = h;
                bool sel = _dl.Height == h;
                r2.Children.Add(Chip(ChipText(Downloader.QualityLabel(h), sel), () => { _dl.Height = hh; RenderDl(); }, sel));
            }
        }
        Grid.SetRow(r2, 1);
        g.Children.Add(r2);

        // Extras: subtitles, folder, auto-delete
        var r3 = new StackPanel { Orientation = Orientation.Horizontal };
        r3.Children.Add(OptLabel("Extras"));
        var subs = Txt("Subtitles", 12.5, true);
        subs.VerticalAlignment = VerticalAlignment.Center;
        subs.Margin = new Thickness(0, 0, 7, 0);
        r3.Children.Add(subs);
        r3.Children.Add(Toggle(_dl.Subs, () => { _dl.Subs = !_dl.Subs; RenderDl(); }));

        var folderRow = new StackPanel { Orientation = Orientation.Horizontal };
        folderRow.Children.Add(Icons.Make("folder", 13, null, 1.8));
        var fname = ChipText(Short(new DirectoryInfo(CurrentDownloadDir).Name, 12), false, 11.5);
        fname.Margin = new Thickness(5, 0, 0, 0);
        folderRow.Children.Add(fname);
        var folderChip = Chip(folderRow, ChooseDownloadFolder);
        folderChip.Margin = new Thickness(12, 0, 6, 0);
        folderChip.ToolTip = CurrentDownloadDir;
        r3.Children.Add(folderChip);

        var adRow = new StackPanel { Orientation = Orientation.Horizontal };
        adRow.Children.Add(Icons.Make("timer", 13, null, 1.8));
        var adT = ChipText(AutoDeleteLabel(_dl.AutoDeleteMinutes), false, 11.5);
        adT.Margin = new Thickness(5, 0, 0, 0);
        adRow.Children.Add(adT);
        var adChip = Chip(adRow, CycleAutoDelete);
        adChip.ToolTip = "Auto-delete the file after this time";
        r3.Children.Add(adChip);
        Grid.SetRow(r3, 2);
        g.Children.Add(r3);

        // bottom: back, download
        var bottom = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var back = RoundBtn("back", "Back", () => { _linkOpts = false; RenderDl(); });
        back.HorizontalAlignment = HorizontalAlignment.Left;
        bottom.Children.Add(back);
        Border dl = DownloadPill();
        Grid.SetColumn(dl, 1);
        bottom.Children.Add(dl);
        Grid.SetRow(bottom, 4);
        g.Children.Add(bottom);
        return g;
    }

    private TextBlock OptLabel(string t)
    {
        var l = Txt(t, 12, false, true);
        l.Width = 54;
        l.VerticalAlignment = VerticalAlignment.Center;
        return l;
    }

    private UIElement IconText(string icon, string text, bool selected)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        FrameworkElement ic = Icons.Make(icon, 14, selected ? OnFillBrush : null, 1.8);
        ic.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(ic);
        var t = ChipText(text, selected);
        t.Margin = new Thickness(5, 0, 0, 0);
        sp.Children.Add(t);
        return sp;
    }

    private Border Toggle(bool on, Action click)
    {
        var knob = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = Brushes.White,
            HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(2)
        };
        var b = new Border { Width = 40, Height = 22, CornerRadius = new CornerRadius(11), Cursor = Cursors.Hand, Child = knob };
        b.SetResourceReference(Border.BackgroundProperty, on ? "AccentBrush" : "TrackBrush");
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }
}
