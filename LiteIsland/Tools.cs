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
    public static string Base => Bundle.Root;

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
    public static string Mpv => Path.Combine(Dir, "mpv", "mpv.exe");
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
    private const string YtDlpNightlyUrl = "https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest/download/yt-dlp.exe";
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
        await Bundle.WaitAsync().ConfigureAwait(false);
        if (File.Exists(YtDlp)) return;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(YtDlp)) return;
            try { await DownloadAsync(YtDlpNightlyUrl, YtDlp, "Setting up downloader", status, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { await DownloadAsync(YtDlpUrl, YtDlp, "Setting up downloader", status, ct).ConfigureAwait(false); }
        }
        finally { Gate.Release(); }
    }

    /// <summary>YouTube needs a small JavaScript engine (deno) to unlock all qualities. Optional: if it cannot be fetched, downloads still try without it.</summary>
    public static async Task EnsureDenoAsync(Action<string>? status, CancellationToken ct)
    {
        await Bundle.WaitAsync().ConfigureAwait(false);
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
        await Bundle.WaitAsync().ConfigureAwait(false);
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
        await Bundle.WaitAsync().ConfigureAwait(false);
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
            if (File.Exists(mark) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(mark)).TotalDays < 2) return;
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

    /// <summary>Live wallpaper player (mpv). Downloaded once on demand (~30 MB), unpacked with 7zr.</summary>
    public static async Task EnsureMpvAsync(Action<string>? status, CancellationToken ct)
    {
        if (File.Exists(Mpv)) return;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(Mpv)) return;
            Directory.CreateDirectory(Dir);
            status?.Invoke("Finding wallpaper engine...");
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest");
            req.Headers.UserAgent.ParseAdd("LiteIsland");
            string json;
            using (var r = await Http.SendAsync(req, ct).ConfigureAwait(false))
            {
                r.EnsureSuccessStatusCode();
                json = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            string? url = null;
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
                {
                    string n = a.GetProperty("name").GetString() ?? "";
                    if (System.Text.RegularExpressions.Regex.IsMatch(n, @"^mpv-x86_64-\d{8}-git-[0-9a-f]+\.7z$"))
                    { url = a.GetProperty("browser_download_url").GetString(); break; }
                }
            }
            if (url == null) throw new InvalidOperationException("No player build found");

            string z7 = Path.Combine(Dir, "7zr.exe");
            if (!File.Exists(z7))
            {
                try { await DownloadAsync("https://github.com/ip7z/7zip/releases/latest/download/7zr.exe", z7, "Setting up unpacker", status, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { await DownloadAsync("https://www.7-zip.org/a/7zr.exe", z7, "Setting up unpacker", status, ct).ConfigureAwait(false); }
            }
            string arc = Path.Combine(Dir, "mpv.7z");
            await DownloadAsync(url, arc, "Downloading wallpaper engine", status, ct).ConfigureAwait(false);
            status?.Invoke("Unpacking wallpaper engine...");
            string tmp = Path.Combine(Dir, "mpv.new");
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            var psi = new System.Diagnostics.ProcessStartInfo(z7) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("x"); psi.ArgumentList.Add("-y"); psi.ArgumentList.Add("-o" + tmp); psi.ArgumentList.Add(arc);
            using (var pr = System.Diagnostics.Process.Start(psi)!)
            {
                await pr.WaitForExitAsync(ct).ConfigureAwait(false);
                if (pr.ExitCode != 0) throw new InvalidOperationException("Unpack failed");
            }
            string exe = Directory.EnumerateFiles(tmp, "mpv.exe", SearchOption.AllDirectories).FirstOrDefault()
                         ?? throw new InvalidOperationException("mpv.exe was not found");
            string from = Path.GetDirectoryName(exe)!;
            string dst = Path.Combine(Dir, "mpv");
            try { if (Directory.Exists(dst)) Directory.Delete(dst, true); } catch { }
            Directory.Move(from, dst);
            try { Directory.Delete(tmp, true); } catch { }
            try { File.Delete(arc); } catch { }
        }
        finally { Gate.Release(); }
    }
}
