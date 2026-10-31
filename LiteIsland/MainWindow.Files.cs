using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace LiteIsland;

// Hold the ring key on a selected file: ring of actions (convert, compress, zip ...). Also the shared job plumbing
// (progress in the island, "Done" message) and the RAM guard.
public partial class MainWindow
{
    private SelectionService? _sel;
    private ClipboardService? _clipSvc;
    private RadialWindow? _wheel;
    private DispatcherTimer? _guardTimer, _sweepTimer;
    private readonly List<Job> _jobs = new();
    private int _jobSeq;
    private readonly SemaphoreSlim _convGate = new(2, 2);
    private string? _toastPath;
    private long _toastUntil;

    private void InitTasks()
    {
        Tools.CleanCache();
        _clipSvc = new ClipboardService(Dispatcher);
        _clipSvc.TextCopied += OnClipboardText;

        _sel = new SelectionService(Dispatcher);
        _sel.IgnoreClipboard = ms => _clipSvc?.IgnoreFor(ms);
        _sel.Requested += ShowWheel;
        _sel.KeyReleased += () => _wheel?.ReleaseClose();
        _sel.Problem += msg => ShowHud("\uE7BA", msg, null, null, 2200);
        InitAppsRing();
        InitUpdates();

        ApplyTaskSettings();

        // The OS already enforces the hard RAM limit; this only gives memory back when it gets close
        _guardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _guardTimer.Tick += (_, _) =>
        {
            if (!_expanded && !_hudActive && MemoryGuard.WorkingSetMb() > MemoryGuard.LimitMb * 0.85) TrimMemory();
        };
        _guardTimer.Start();

        // Downloads with "Auto-Delete": removed when their time has come
        _sweepTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _sweepTimer.Tick += (_, _) => SweepAutoDelete();
        _sweepTimer.Start();
        SweepAutoDelete();

        Bundle.Start(unpacked => { if (unpacked) Dispatcher.BeginInvoke(new Action(() => ShowHud("\uE73E", "Tools ready", null, "One-time setup done", 3500))); });
        _ = Tools.UpdateYtDlpIfOldAsync();
        FileeEngine.Prewarm();
    }

    private void DisposeTasks()
    {
        _guardTimer?.Stop();
        _sweepTimer?.Stop();
        _sel?.Dispose();
        _clipSvc?.Dispose();
        _wheel?.CloseOnce();
        foreach (Job j in _jobs.ToArray())
            if (j.State == JobState.Running) { try { j.Cts.Cancel(); } catch { } }
    }

    /// <summary>Called by the settings window and at start.</summary>
    public void ApplyTaskSettings()
    {
        PerfLog.Enabled = _s.PerfLog;
        if (_sel != null) { _sel.TriggerVk = _s.RingKey; _sel.AppsVk = _s.AppsKey; _sel.AppsEnabled = _s.AppsRing; }
        _sel?.SetEnabled(_s.FileWheel);
        _clipSvc?.SetEnabled(_s.LinkDetect || _s.ClipHistory);
        if (_view == ViewClip) RenderClip();
    }

    public string CurrentDownloadDir => string.IsNullOrWhiteSpace(_s.DownloadDir) ? Downloader.DefaultDir() : _s.DownloadDir;

    // ---------- The ring ----------

    private void ShowWheel(string[] files)
    {
        if (_sel != null && !_sel.Held) return; // let go before the ring could open
        if (_wheel != null && !_wheel.IsBusy) _wheel.CloseOnce(); // an old ring never blocks a new hold
        _appsWheel?.CloseOnce();
        MemoryGuard.Burst();

        string pinKey = RingPlan.PinKey(files);
        _s.RingPins.TryGetValue(pinKey, out List<string>? pinned);
        var pages = RingPlan.Build(files, pinned ?? new List<string>());
        if (pages == null)
        {
            // Formats of this file type are not known yet: the quick ring opens now, the full one is ready next time
            var quick = FileKinds.ActionsFor(files, false).Where(a => a.Id != "more").ToList();
            pages = new List<List<ActionDef>> { quick };
            foreach (string e in files.Select(FileeEngine.ExtOf).Distinct())
                _ = FileeEngine.TargetsAsync(e);
        }

        string label = files.Length > 1 ? files.Length + " files" : FileKinds.Label(files[0]);
        ImageSource? icon = files.Length == 1 ? IconHelper.Get(files[0]) : null;
        Color accent = Colors.DodgerBlue;
        try { accent = (Color)ColorConverter.ConvertFromString(_s.AccentColor); } catch { }

        bool TogglePin(ActionDef a)
        {
            if (a.Fmt == null) return false;
            if (!_s.RingPins.TryGetValue(pinKey, out List<string>? l)) _s.RingPins[pinKey] = l = new List<string>();
            bool now = !l.Remove(a.Fmt);
            if (now) l.Add(a.Fmt);
            if (l.Count == 0) _s.RingPins.Remove(pinKey);
            _s.Save();
            return now;
        }

        bool IsPinned(ActionDef a) => a.Fmt != null && _s.RingPins.TryGetValue(pinKey, out List<string>? l) && l.Contains(a.Fmt);

        var w = new RadialWindow(pages, label, icon, _s.Theme == "Light", accent, a => RunFileAction(a, files), TogglePin, IsPinned,
            false, "Let go to close", null, _s.Glass, _s.GlassOpacity, () => _sel != null && _sel.Held);
        _wheel = w;
        w.Closed += (_, _) =>
        {
            if (ReferenceEquals(_wheel, w)) _wheel = null;
            _trimTimer.Stop();
            _trimTimer.Start();
        };
        w.Show();
    }

    // ---------- Running an action ----------

    private RingResult? RunFileAction(ActionDef a, string[] files)
    {
        if (a.Id == "copypath")
        {
            SetClipboardText(string.Join(Environment.NewLine, files));
            ShowToast(true, "Path copied", "", null);
            return new RingResult();
        }

        if (a.Id == "phone")
        {
            SendToPhone(files);
            return new RingResult();
        }

        var jobs = new List<Job>();
        var tasks = new List<Task<bool>>();

        // These make ONE result from all selected files
        bool combined = a.Id == "zip" || a.Id == "merge" || (a.Id == "pdf" && files.Length > 1);
        if (combined)
        {
            string title = files.Length == 1 ? Path.GetFileName(files[0]) : files.Length + " files";
            Job j = NewJob("convert", title, a.Icon);
            jobs.Add(j);
            tasks.Add(RunConvert(j, a, files));
        }
        else
        {
            foreach (string f in files)
            {
                Job j = NewJob("convert", Path.GetFileName(f.TrimEnd('\\')), a.Icon);
                jobs.Add(j);
                tasks.Add(RunConvert(j, a, new[] { f }));
            }
        }

        return new RingResult
        {
            Done = Task.WhenAll(tasks).ContinueWith(t => t.Status == TaskStatus.RanToCompletion && t.Result.All(x => x)),
            Progress = () =>
            {
                double sum = 0;
                foreach (Job j in jobs)
                {
                    if (j.State == JobState.Done) sum += 1;
                    else if (j.Progress > 0) sum += j.Progress;
                    else return -1;
                }
                return jobs.Count == 0 ? -1 : sum / jobs.Count;
            }
        };
    }

    private Job NewJob(string kind, string title, string icon)
    {
        var j = new Job { Id = ++_jobSeq, Kind = kind, Title = title, Icon = icon, StartTick = Environment.TickCount64 };
        _jobs.Insert(0, j);
        while (_jobs.Count > 30)
        {
            Job? old = _jobs.LastOrDefault(x => x.State != JobState.Running && x.State != JobState.Paused);
            if (old == null) break;
            _jobs.Remove(old);
        }
        if (_view == ViewDl && _expanded && _linkUrl == null) RenderDl();
        return j;
    }

    private async Task<bool> RunConvert(Job j, ActionDef a, string[] files)
    {
        j.Detail = a.Label + "...";
        OnJobChanged(j, false);

        await _convGate.WaitAsync();
        try
        {
            j.StartTick = Environment.TickCount64;
            j.InBytes = files.Sum(x => SizeOfPath(x));
            string? output = await ConvertCore(j, a, files);

            j.OutPath = output;
            if (output != null) j.OutBytes = SizeOfPath(output);
            j.Progress = 1;
            j.Detail = a.Id == "ocr" || a.Id == "pocr" ? j.Detail : "";
            FinishJob(j, JobState.Done);
            return true;
        }
        catch (OperationCanceledException)
        {
            FinishJob(j, JobState.Canceled, "Stopped");
            return false;
        }
        catch (Exception ex)
        {
            FinishJob(j, JobState.Failed, Short(ex.Message, 90));
            return false;
        }
        finally
        {
            _convGate.Release();
        }
    }

    private async Task<string?> ConvertCore(Job j, ActionDef a, string[] files)
    {
        CancellationToken ct = j.Cts.Token;
        string f = files[0];
        string ext = Path.GetExtension(f).ToLowerInvariant();
        Action<double> prog = p => Tick(j, p);
        Action<string> status = s => { j.Detail = s; Tick(j, -1); };

        // ----- everything else Filee can make (256 formats), and archives -----
        if (a.Id.StartsWith("f:", StringComparison.Ordinal))
        {
            j.Detail = a.Label + "...";
            return await FileeEngine.ConvertAsync(f, a.Id.Substring(2), ct);
        }
        if (a.Id == "extract")
        {
            j.Detail = "Extracting...";
            return await FileeEngine.ConvertAsync(f, "folder", ct);
        }

        switch (a.Id)
        {
            // ----- pictures (helper process) -----
            case "jpg":
            {
                string o = OutFor(f, "", ".jpg");
                await Proc.RunWorkerAsync("img", new[] { f, o, "jpg", "92", "0", "0" }, prog, ct);
                return o;
            }
            case "png":
            {
                string o = OutFor(f, "", ".png");
                await Proc.RunWorkerAsync("img", new[] { f, o, "png", "100", "0", "0" }, prog, ct);
                return o;
            }
            case "bmp":
            case "tiff":
            case "gif1":
            {
                string fmt = a.Id == "gif1" ? "gif" : a.Id;
                string o = OutFor(f, "", "." + fmt);
                await Proc.RunWorkerAsync("img", new[] { f, o, fmt, "100", "0", "0" }, prog, ct);
                return o;
            }
            case "compress":
            {
                bool png = ext == ".png";
                string o = OutFor(f, "-compressed", png ? ".png" : ".jpg");
                await Proc.RunWorkerAsync("img", new[] { f, o, png ? "png" : "jpg", "72", "2560", "1" }, prog, ct);
                return o;
            }
            case "removebg":
            {
                await Tools.EnsureModelAsync(status, ct);
                string o = OutFor(f, "-cutout", ".png");
                j.Detail = "Removing background...";
                await Proc.RunWorkerAsync("removebg", new[] { f, o, Tools.Model }, prog, ct);
                return o;
            }
            case "ocr":
            {
                string text = await Proc.RunWorkerAsync("ocr", new[] { f }, prog, ct);
                return CopyRecognizedText(j, text);
            }
            case "pdf":
            {
                string o = OutFor(f, files.Length > 1 ? "-combined" : "", ".pdf");
                await Proc.RunWorkerAsync("img2pdf", new[] { o }.Concat(files), prog, ct);
                return o;
            }

            // ----- PDF (helper process, Windows' own PDF engine) -----
            case "pcompress":
            {
                string o = OutFor(f, "-compressed", ".pdf");
                await Proc.RunWorkerAsync("pdfraster", new[] { o, "62", "110", f }, prog, ct);
                if (new FileInfo(o).Length >= new FileInfo(f).Length)
                {
                    File.Delete(o);
                    throw new InvalidOperationException("Already as small as it gets");
                }
                return o;
            }
            case "merge":
            {
                string o = OutFor(f, "-merged", ".pdf");
                await Proc.RunWorkerAsync("pdfraster", new[] { o, "88", "170" }.Concat(files), prog, ct);
                return o;
            }
            case "p2img":
            {
                string dir = UniquePath(Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f) + " pages", "");
                await Proc.RunWorkerAsync("pdf2img", new[] { f, dir }, prog, ct);
                return dir;
            }
            case "pocr":
            {
                string text = await Proc.RunWorkerAsync("pdfocr", new[] { f }, prog, ct);
                return CopyRecognizedText(j, text);
            }
            case "dpdf":
            {
                string o = OutFor(f, "", ".pdf");
                await Proc.RunWorkerAsync("doc2pdf", new[] { f, o }, prog, ct);
                return o;
            }

            // ----- zip (in the island process, streamed) -----
            case "zip":
            {
                string dir = Path.GetDirectoryName(f.TrimEnd('\\'))!;
                string name = files.Length == 1 ? Path.GetFileName(f.TrimEnd('\\')) : "Archive";
                string o = UniquePath(dir, name, ".zip");
                j.Detail = "Zipping...";
                await Task.Run(() => ZipPaths(files, o, prog, ct), ct);
                return o;
            }

            // ----- video and audio (ffmpeg, downloaded once) -----
            default:
            {
                await Tools.EnsureFFmpegAsync(status, ct);
                var (args, outExt, suffix) = FfmpegPlan(a.Id, ext);
                string o = OutFor(f, suffix, outExt);
                j.Detail = a.Label + "...";
                await RunFfmpeg(j, f, args, o);
                return o;
            }
        }
    }

    private string CopyRecognizedText(Job j, string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new InvalidOperationException("No text found");
        Dispatcher.Invoke(() => SetClipboardText(text));
        j.Detail = text.Length.ToString() + " characters copied";
        return "";
    }

    private static (string[] Args, string Ext, string Suffix) FfmpegPlan(string id, string srcExt) => id switch
    {
        "vcompress" => (new[] { "-c:v", "libx264", "-crf", "28", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                                "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart" }, ".mp4", "-compressed"),
        "mp4" => (new[] { "-c:v", "libx264", "-crf", "20", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                          "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart" }, ".mp4", ""),
        "gif" => (new[] { "-t", "30", "-vf",
                          "fps=12,scale='min(480,iw)':-1:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=128[p];[s1][p]paletteuse=dither=bayer:bayer_scale=4",
                          "-loop", "0" }, ".gif", ""),
        "audio" => (new[] { "-vn", "-c:a", "aac", "-b:a", "192k" }, ".m4a", ""),
        "mute" => (new[] { "-an", "-c:v", "copy" }, srcExt, "-muted"),
        "m4a" => (new[] { "-vn", "-c:a", "aac", "-b:a", "256k" }, ".m4a", ""),
        "wav" => (new[] { "-vn", "-c:a", "pcm_s16le" }, ".wav", ""),
        "mp3" => (new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" }, ".mp3", ""),
        "flac" => (new[] { "-vn", "-c:a", "flac" }, ".flac", ""),
        "ogg" => (new[] { "-vn", "-c:a", "libvorbis", "-q:a", "5" }, ".ogg", ""),
        "opus" => (new[] { "-vn", "-c:a", "libopus", "-b:a", "128k" }, ".opus", ""),
        "aac" => (new[] { "-vn", "-c:a", "aac", "-b:a", "192k" }, ".aac", ""),
        "aiff" => (new[] { "-vn", "-c:a", "pcm_s16be" }, ".aiff", ""),
        "wma" => (new[] { "-vn", "-c:a", "wmav2", "-b:a", "192k" }, ".wma", ""),
        "mkv" => (new[] { "-map", "0", "-c", "copy" }, ".mkv", ""),
        "mov" => (new[] { "-c:v", "libx264", "-crf", "20", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k" }, ".mov", ""),
        "avi" => (new[] { "-c:v", "mpeg4", "-q:v", "4", "-c:a", "libmp3lame", "-q:a", "4" }, ".avi", ""),
        "webm" => (new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-cpu-used", "5", "-c:a", "libopus", "-b:a", "128k" }, ".webm", ""),
        "v720" => (new[] { "-vf", "scale=-2:'min(720,ih)'", "-c:v", "libx264", "-crf", "26", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                           "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart" }, ".mp4", "-720p"),
        "thumb" => (new[] { "-ss", "1", "-frames:v", "1", "-q:v", "2" }, ".jpg", "-frame"),
        "webp" => (new[] { "-frames:v", "1", "-c:v", "libwebp", "-quality", "90" }, ".webp", ""),
        "half" => (new[] { "-vf", "scale=trunc(iw/4)*2:trunc(ih/4)*2", "-frames:v", "1", "-q:v", "2" }, srcExt, "-half"),
        "rot90" => (new[] { "-vf", "transpose=1", "-frames:v", "1", "-q:v", "2" }, srcExt, "-rotated"),
        "flip" => (new[] { "-vf", "hflip", "-frames:v", "1", "-q:v", "2" }, srcExt, "-mirrored"),
        _ => throw new InvalidOperationException("Unknown action")
    };

    private async Task RunFfmpeg(Job j, string input, string[] args, string output)
    {
        double dur = 0;
        var tail = new Queue<string>();
        var all = new List<string> { "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats", "-i", input };
        all.AddRange(args);
        all.Add(output);

        int code = await Proc.RunAsync(Tools.FFmpeg, all,
            line =>
            {
                if (dur > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(12), out long us) && us >= 0)
                    Tick(j, Math.Min(0.99, us / 1e6 / dur));
            },
            line =>
            {
                lock (tail) { tail.Enqueue(line); while (tail.Count > 8) tail.Dequeue(); }
                if (dur <= 0)
                {
                    var m = Regex.Match(line, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                    if (m.Success)
                        dur = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 +
                              double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
            },
            j.Cts.Token, null, ProcessPriorityClass.Normal);

        if (code != 0)
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            string msg;
            lock (tail) msg = tail.LastOrDefault(l => l.Contains("rror", StringComparison.Ordinal)) ?? tail.LastOrDefault() ?? "Conversion failed";
            throw new InvalidOperationException(msg);
        }
    }

    private static void ZipPaths(string[] paths, string dest, Action<double> prog, CancellationToken ct)
    {
        var items = new List<(string Full, string Entry)>();
        foreach (string p in paths)
        {
            if (Directory.Exists(p))
            {
                string root = p.TrimEnd('\\');
                string parent = Path.GetDirectoryName(root) ?? root;
                foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    items.Add((f, Path.GetRelativePath(parent, f)));
            }
            else
            {
                items.Add((p, Path.GetFileName(p)));
            }
        }
        if (items.Count == 0) throw new InvalidOperationException("Nothing to zip");

        long total = Math.Max(1, items.Sum(i => new FileInfo(i.Full).Length)), done = 0;
        string[] packed = { ".zip", ".jpg", ".jpeg", ".png", ".mp4", ".mkv", ".mp3", ".m4a", ".webm", ".7z", ".rar", ".gz" };

        try
        {
            using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            using var za = new ZipArchive(fs, ZipArchiveMode.Create);
            foreach (var (full, entry) in items)
            {
                ct.ThrowIfCancellationRequested();
                bool already = packed.Contains(Path.GetExtension(full).ToLowerInvariant());
                za.CreateEntryFromFile(full, entry.Replace('\\', '/'),
                    already ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                done += new FileInfo(full).Length;
                prog(Math.Min(0.99, (double)done / total));
            }
        }
        catch
        {
            try { File.Delete(dest); } catch { }
            throw;
        }
    }

    // ---------- Small helpers ----------

    private static long SizeOfPath(string p)
    {
        try
        {
            if (File.Exists(p)) return new FileInfo(p).Length;
            if (Directory.Exists(p))
                return Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch { }
        return 0;
    }

    private static string UniquePath(string dir, string name, string ext)
    {
        string p = Path.Combine(dir, name + ext);
        int i = 1;
        while (File.Exists(p) || Directory.Exists(p)) p = Path.Combine(dir, name + " (" + (i++) + ")" + ext);
        return p;
    }

    private static string OutFor(string src, string suffix, string ext) =>
        UniquePath(Path.GetDirectoryName(src.TrimEnd('\\'))!, Path.GetFileNameWithoutExtension(src.TrimEnd('\\')) + suffix, ext);

    private static string Short(string s, int max)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s.Substring(0, Math.Max(1, max - 1)) + "…";
    }

    private void SetClipboardText(string text)
    {
        _clipSvc?.IgnoreFor(1500);
        try { Clipboard.SetDataObject(text, true); } catch { }
    }

    // ---------- Progress and the "Done" message in the island ----------

    // Called from any thread, at most ~5 times a second
    private void Tick(Job j, double p)
    {
        if (p >= 0) j.Progress = p;
        long now = Environment.TickCount64;
        if (now - j.LastUi < 200) return;
        j.LastUi = now;
        Dispatcher.InvokeAsync(() => OnJobChanged(j, false));
    }

    private void FinishJob(Job j, JobState state, string? error = null)
    {
        j.State = state;
        j.Seconds = (Environment.TickCount64 - j.StartTick) / 1000.0;
        j.Finished = DateTime.Now;
        if (error != null) j.Detail = error;

        Dispatcher.InvokeAsync(() =>
        {
            if (state == JobState.Done && j.Kind == "convert" && j.Detail.Length == 0)
                j.Detail = j.OutBytes > 0 && j.InBytes > 0 ? Size(j.InBytes) + " → " + Size(j.OutBytes) : "Done";

            OnJobChanged(j, true);

            string secs = j.Seconds < 10 ? j.Seconds.ToString("0.0", InvC) + " s" : Fmt(j.Seconds);
            if (state == JobState.Done)
            {
                string shown = string.IsNullOrEmpty(j.OutPath) ? j.Detail : Path.GetFileName(j.OutPath.TrimEnd('\\'));
                ShowToast(true, Short(shown, 24), secs, string.IsNullOrEmpty(j.OutPath) ? null : j.OutPath);
            }
            else if (state == JobState.Failed)
            {
                ShowToast(false, Short(j.Detail, 26), "", null);
            }

            _trimTimer.Stop();
            _trimTimer.Start();
        });
    }

    private void OnJobChanged(Job j, bool stateChanged)
    {
        // Downloads show their progress only on the Downloads page (the island keeps showing music etc.);
        // the island only speaks up when a download is finished or failed.
        if (j.State == JobState.Running && j.Kind != "download" && _wheel == null)
        {
            string label = Short(j.Kind == "download" ? "Downloading" : j.Title, 22);
            ShowHud("", label, j.Progress >= 0 ? j.Progress : (double?)null, j.Progress < 0 ? "…" : null, 1600);
        }
        UpdateJobRow(j, stateChanged);
    }

    private void ShowToast(bool ok, string label, string right, string? path)
    {
        ShowHud(ok ? "" : "", label, null, right.Length > 0 ? right : null, ok ? 4200 : 4800);
        _toastPath = path;
        _toastUntil = Environment.TickCount64 + 4200;
    }

    // Clicking the island right after "Done" shows the new file in Explorer
    private bool TryRevealToast()
    {
        if (_toastPath == null || Environment.TickCount64 > _toastUntil) return false;
        string p = _toastPath;
        _toastPath = null;
        if (!File.Exists(p) && !Directory.Exists(p)) return false;
        ShowInFolder(p);
        return true;
    }

    // ---------- Auto-delete of old downloads ----------

    private void SweepAutoDelete()
    {
        try
        {
            long now = DateTime.UtcNow.Ticks;
            bool changed = false;
            foreach (AutoDeleteItem it in _s.AutoDelete.ToArray())
            {
                if (it.DueUtcTicks > now) continue;
                try { if (File.Exists(it.Path)) File.Delete(it.Path); } catch { continue; }
                _s.AutoDelete.Remove(it);
                changed = true;
            }
            if (changed) _s.Save();
        }
        catch { }
    }
}
