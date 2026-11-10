using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiteIsland;

/// <summary>Pictures out of videos with mpv (the live wallpaper player): hover preview frames and library covers.</summary>
public static class LiveFrames
{
    private static readonly SemaphoreSlim Gate = new(2);
    private static readonly Dictionary<string, Task<List<string>>> Previews = new();

    private static string Key(string s)
    {
        byte[] h = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(h, 0, 8);
    }

    private static async Task<bool> RunMpvAsync(string input, string outDir, string[] extra, CancellationToken ct)
    {
        try
        {
            await Tools.EnsureMpvAsync(null, ct).ConfigureAwait(false);
            Directory.CreateDirectory(outDir);
            var a = new List<string>
            {
                input, "--no-config", "--no-audio", "--ao=null", "--hwdec=no", "--really-quiet", "--no-terminal",
                "--vo=image", "--vo-image-format=jpg", "--vo-image-jpeg-quality=72", "--vo-image-outdir=" + outDir,
                "--user-agent=Mozilla/5.0", "--network-timeout=15"
            };
            a.AddRange(extra);
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try { await Proc.RunAsync(Tools.Mpv, a, null, null, ct).ConfigureAwait(false); }
            finally { Gate.Release(); }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>~3 seconds of small frames of a preview clip (cached on disk, shared by all hovers).</summary>
    public static Task<List<string>> PreviewFramesAsync(string clipUrl)
    {
        lock (Previews)
        {
            if (Previews.TryGetValue(clipUrl, out var t) && !(t.IsCompleted && t.Result.Count == 0)) return t;
            return Previews[clipUrl] = DoPreview(clipUrl);
        }
    }

    private static async Task<List<string>> DoPreview(string url)
    {
        string dir = Path.Combine(Tools.CacheDir, "livepv", Key(url));
        List<string> Files() => Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jpg").OrderBy(x => x).ToList() : new List<string>();
        var have = Files();
        if (have.Count >= 8) return have;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        await RunMpvAsync(url, dir, new[] { "--vf=lavfi=[fps=8,scale=400:-2]", "--frames=24", "--vo-image-jpeg-quality=65" }, CancellationToken.None).ConfigureAwait(false);
        return Files();
    }

    /// <summary>One picture from a downloaded video (cached next to the library), or null.</summary>
    public static async Task<string?> CoverAsync(string video)
    {
        try
        {
            string cov = Path.Combine(LiveService.LiveDir, ".covers");
            string dest = Path.Combine(cov, Path.GetFileNameWithoutExtension(video) + ".jpg");
            if (File.Exists(dest) && new FileInfo(dest).Length > 0) return dest;
            string tmp = Path.Combine(Tools.CacheDir, "livecv", Key(video));
            foreach (string start in new[] { "2", "0" })
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
                await RunMpvAsync(video, tmp, new[] { "--start=" + start, "--frames=1", "--vf=lavfi=[scale=640:-2]" }, CancellationToken.None).ConfigureAwait(false);
                string? f = Directory.Exists(tmp) ? Directory.GetFiles(tmp, "*.jpg").FirstOrDefault() : null;
                if (f != null)
                {
                    Directory.CreateDirectory(cov);
                    File.Copy(f, dest, true);
                    try { Directory.Delete(tmp, true); } catch { }
                    return dest;
                }
            }
        }
        catch { }
        return null;
    }
}
