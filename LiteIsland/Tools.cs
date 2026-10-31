using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LiteIsland;

/// <summary>
/// The few helper programs are NOT inside the exe (it would be 100 MB bigger). They are downloaded once, the first
/// time a feature needs them, into %AppData%\LiteIsland\tools. After that everything works offline
/// (only link downloads need internet, of course).
/// </summary>
internal static class Tools
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteIsland", "tools");

    /// <summary>The folder of LiteIsland.exe. The release folder carries its engines with it (nothing is downloaded).</summary>
    public static readonly string Base = AppContext.BaseDirectory;

    // Order: the newest copy among the bundled ones and the one in AppData (yt-dlp updates itself there).
    // When none exists yet, the AppData path is returned: that is where a fallback download goes.
    private static string Pick(string downloaded, params string[] bundled)
    {
        string best = downloaded;
        DateTime bestTime = File.Exists(downloaded) ? File.GetLastWriteTimeUtc(downloaded) : DateTime.MinValue;
        foreach (string b in bundled)
        {
            if (!File.Exists(b)) continue;
            DateTime t = File.GetLastWriteTimeUtc(b);
            if (!File.Exists(best) || t > bestTime) { best = b; bestTime = t; }
        }
        return best;
    }

    public static string YtDlp => Pick(Path.Combine(Dir, "yt-dlp.exe"), Path.Combine(Base, "engines", "yt-dlp.exe"));
    public static string Deno => Pick(Path.Combine(Dir, "deno.exe"), Path.Combine(Base, "engines", "deno.exe"));
    public static string Model => Pick(Path.Combine(Dir, "u2netp.onnx"), Path.Combine(Base, "engines", "u2netp.onnx"));
    public static string FFmpeg => Pick(Path.Combine(Dir, "ffmpeg.exe"),
        Path.Combine(Base, "filee", "engines", "ffmpeg", "bin", "ffmpeg.exe"),
        Path.Combine(Base, "engines", "ffmpeg", "bin", "ffmpeg.exe"));

    /// <summary>Folder of ffmpeg.exe (yt-dlp wants the place of ffmpeg and ffprobe).</summary>
    public static string FFmpegDir => Path.GetDirectoryName(FFmpeg) ?? Dir;

    /// <summary>Hidden working folder of the downloader (parts of unfinished downloads). Emptied by itself.</summary>
    public static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteIsland", "cache");

    public static string NewJobCache()
    {
        string d = Path.Combine(CacheDir, Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(d);
        return d;
    }

    public static void DeleteCache(string? dir)
    {
        try { if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    /// <summary>At start: nothing in the cache is needed any more.</summary>
    public static void CleanCache()
    {
        try { if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, true); } catch { }
    }

    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string FFmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";
    private const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
    private const string ModelUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx";

    public static readonly HttpClient Http = MakeClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static HttpClient MakeClient()
    {
        var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 LiteIsland/1.3");
        return h;
    }

    public static bool HasFFmpeg => File.Exists(FFmpeg);

    public static async Task EnsureYtDlpAsync(Action<string>? status, CancellationToken ct)
    {
        if (File.Exists(YtDlp)) return;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(YtDlp)) return;
            await DownloadAsync(YtDlpUrl, YtDlp, "Setting up downloader", status, ct).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    /// <summary>YouTube needs a small JavaScript engine (deno) to unlock all qualities. Optional: if it cannot be fetched, downloads still try without it.</summary>
    public static async Task EnsureDenoAsync(Action<string>? status, CancellationToken ct)
    {
        if (File.Exists(Deno)) return;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(Deno)) return;
            string zip = Path.Combine(Dir, "deno.zip");
            await DownloadAsync(DenoUrl, zip, "Setting up YouTube support", status, ct).ConfigureAwait(false);
            await Task.Run(() =>
            {
                using (var z = ZipFile.OpenRead(zip))
                {
                    var e = z.Entries.FirstOrDefault(x => x.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException("deno.exe was not found");
                    e.ExtractToFile(Deno + ".new", true);
                }
                File.Move(Deno + ".new", Deno, true);
                try { File.Delete(zip); } catch { }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* optional */ }
        finally { Gate.Release(); }
    }

    public static async Task EnsureModelAsync(Action<string>? status, CancellationToken ct)
    {
        if (File.Exists(Model)) return;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(Model)) return;
            await DownloadAsync(ModelUrl, Model, "Getting background model", status, ct).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public static async Task EnsureFFmpegAsync(Action<string>? status, CancellationToken ct)
    {
        if (File.Exists(FFmpeg)) return;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(FFmpeg)) return;
            string zip = Path.Combine(Dir, "ffmpeg.zip");
            await DownloadAsync(FFmpegUrl, zip, "Setting up video tools", status, ct).ConfigureAwait(false);
            status?.Invoke("Unpacking video tools");
            await Task.Run(() =>
            {
                using (var z = ZipFile.OpenRead(zip))
                {
                    var e = z.Entries.FirstOrDefault(x => x.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException("ffmpeg.exe was not found in the download");
                    e.ExtractToFile(FFmpeg + ".new", true);
                }
                File.Move(FFmpeg + ".new", FFmpeg, true);
                try { File.Delete(zip); } catch { }
            }, ct).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Downloads a file in small pieces (a few KB of RAM), with a progress text.</summary>
    private static async Task DownloadAsync(string url, string dest, string label, Action<string>? status, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        string tmp = dest + ".part";
        status?.Invoke(label + "...");
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            long len = resp.Content.Headers.ContentLength ?? 0;
            using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            byte[] buf = new byte[81920];
            long got = 0, last = 0;
            int n;
            while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                got += n;
                long now = Environment.TickCount64;
                if (len > 0 && now - last > 300)
                {
                    last = now;
                    status?.Invoke(label + " " + (got * 100 / len).ToString() + "%");
                }
            }
        }
        File.Move(tmp, dest, true);
    }

    /// <summary>yt-dlp has to follow the video sites. Once a week it updates itself (quietly, in the background).</summary>
    /// <summary>Refreshes yt-dlp right now (used when a download failed: sites change often). Quick when it is already current.</summary>
    public static async Task UpdateYtDlpNowAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(YtDlp)) return;
            Directory.CreateDirectory(Dir);
            string own = Path.Combine(Dir, "yt-dlp.exe");
            if (!File.Exists(own)) File.Copy(YtDlp, own, true);
            File.WriteAllText(Path.Combine(Dir, "yt-dlp.checked"), DateTime.UtcNow.ToString("o"));
            await Proc.RunAsync(own, new[] { "-U", "--no-colors" }, null, null, ct, null,
                System.Diagnostics.ProcessPriorityClass.Normal).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best effort */ }
    }

    public static async Task UpdateYtDlpIfOldAsync()
    {
        try
        {
            if (!File.Exists(YtDlp)) return;
            string mark = Path.Combine(Dir, "yt-dlp.checked");
            if (File.Exists(mark) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(mark)).TotalDays < 7) return;
            Directory.CreateDirectory(Dir);
            File.WriteAllText(mark, DateTime.UtcNow.ToString("o"));

            // The bundled copy stays untouched: a private copy in AppData is the one that updates itself
            string own = Path.Combine(Dir, "yt-dlp.exe");
            if (!File.Exists(own)) File.Copy(YtDlp, own, true);
            await Proc.RunAsync(own, new[] { "-U", "--no-colors" }, null, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }
    }
}
