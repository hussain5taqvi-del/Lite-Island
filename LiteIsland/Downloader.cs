using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LiteIsland;

/// <summary>What is known about a copied link.</summary>
internal sealed class LinkInfo
{
    public string Url = "";
    public string Title = "";
    public string Uploader = "";
    public double Duration;          // seconds, 0 = unknown (photos, live streams)
    public string ThumbUrl = "";
    public string Extractor = "";
    public string Id = "";
    public int MaxHeight;            // 0 = unknown
    public ImageSource? Thumb;
    public string Host => Downloader.HostOf(Url);
}

/// <summary>The choices on the download card.</summary>
internal sealed class DlOptions
{
    public int Height = 1080;
    public bool AudioOnly;
    public bool Subs;
    public string Dir = "";
    public string Start = "", End = "";   // optional clip, "m:ss" or "h:mm:ss"
    public int AutoDeleteMinutes;         // 0 = keep
    public int LimitMBps;                 // 0 = no limit
    public string TempDir = "";           // hidden cache folder of this download
    public int Attempt;                   // 0 = normal, 1 = after refreshing yt-dlp, 2 = last resort settings
}

internal static class Downloader
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Sites where a copied link opens the download card by itself (turn on "any link" in Settings for everything else)
    private static readonly string[] KnownHosts =
    {
        "youtube.com", "youtu.be", "instagram.com", "twitter.com", "x.com", "facebook.com", "fb.watch", "fb.com",
        "tiktok.com", "vimeo.com", "reddit.com", "redd.it", "pinterest.com", "pin.it", "twitch.tv", "dailymotion.com",
        "soundcloud.com", "snapchat.com", "threads.net", "linkedin.com", "bilibili.com", "ok.ru", "vk.com", "rumble.com",
        "streamable.com", "bsky.app", "tumblr.com", "imgur.com", "music.youtube.com", "youtube-nocookie.com",
        "instagr.am", "m.facebook.com", "mobile.twitter.com", "vm.tiktok.com", "kick.com", "odysee.com", "bitchute.com",
        "mixcloud.com", "bandcamp.com", "9gag.com", "likee.video", "dai.ly", "weibo.com", "douyin.com", "kuaishou.com",
        "coub.com", "ted.com", "twitch.tv", "clips.twitch.tv", "teams.microsoft.com", "loom.com", "vimeo.com"
    };

    /// <summary>The browser whose cookies unlocked a login-only video (remembered while the app runs).</summary>
    public static string? CookieBrowser;

    public static bool IsKnownHost(string url)
    {
        string host = HostOf(url);
        return KnownHosts.Any(k => host == k || host.EndsWith("." + k));
    }

    public static string HostOf(string url)
    {
        try
        {
            string h = new Uri(url).Host.ToLowerInvariant();
            return h.StartsWith("www.") ? h.Substring(4) : h;
        }
        catch { return url; }
    }

    /// <summary>A single http(s) link on one line?</summary>
    public static bool TryGetLink(string text, bool anySite, out string url)
    {
        url = "";
        string t = text.Trim();
        if (t.Length < 10 || t.Length > 600 || t.Contains('\n') || t.Contains(' ')) return false;
        if (!Uri.TryCreate(t, UriKind.Absolute, out Uri? u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        string host = HostOf(t);
        bool known = KnownHosts.Any(k => host == k || host.EndsWith("." + k));
        if (!known && !anySite) return false;
        // a bare home page is not a video
        if (u.AbsolutePath.Length <= 1 && string.IsNullOrEmpty(u.Query)) return false;
        url = t;
        return true;
    }

    public static string DefaultDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LiteIsland");

    // ---------- Look up ----------

    /// <summary>
    /// YouTube: title, channel and picture in a fraction of a second (the public oEmbed page), so the card can open at once.
    /// The slower yt-dlp lookup then only adds the length and the best quality. Null = not available (use yt-dlp).
    /// </summary>
    public static async Task<LinkInfo?> QuickInfoAsync(string url, CancellationToken ct)
    {
        string h = HostOf(url);
        if (!(h == "youtu.be" || h == "youtube.com" || h.EndsWith(".youtube.com")) || h == "music.youtube.com") return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(4000);
            using var resp = await Tools.Http.GetAsync("https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString(url), cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            JsonElement r = doc.RootElement;
            string title = r.TryGetProperty("title", out JsonElement t) ? t.GetString() ?? "" : "";
            if (title.Length == 0) return null;
            return new LinkInfo
            {
                Url = url,
                Title = title,
                Uploader = r.TryGetProperty("author_name", out JsonElement a) ? a.GetString() ?? "" : "",
                ThumbUrl = r.TryGetProperty("thumbnail_url", out JsonElement th) ? th.GetString() ?? "" : "",
                Extractor = "Youtube",
                Id = YoutubeId(url)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static string YoutubeId(string url)
    {
        try
        {
            var u = new Uri(url);
            if (u.Host.Contains("youtu.be")) return u.AbsolutePath.Trim('/');
            foreach (string part in u.Query.TrimStart('?').Split('&'))
                if (part.StartsWith("v=", StringComparison.Ordinal)) return part.Substring(2);
            string[] seg = u.AbsolutePath.Trim('/').Split('/');
            if (seg.Length >= 2 && (seg[0] == "shorts" || seg[0] == "live" || seg[0] == "embed")) return seg[1];
        }
        catch { }
        return "";
    }

    // Clients that work without a sign-in or an extra token (tried before any browser cookies are needed)
    internal const string AltClients = "youtube:player_client=tv,tv_simply,web_embedded,android_vr";
    /// <summary>Set once a lookup needed the alternative clients: the download then uses them as well.</summary>
    public static bool UseAltClients;

    private static string[] CookieBrowsers()
    {
        string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var l = new List<string>();
        if (Directory.Exists(Path.Combine(la, "BraveSoftware", "Brave-Browser", "User Data"))) l.Add("brave");
        if (Directory.Exists(Path.Combine(la, "Microsoft", "Edge", "User Data"))) l.Add("edge");
        if (Directory.Exists(Path.Combine(la, "Google", "Chrome", "User Data"))) l.Add("chrome");
        if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox"))) l.Add("firefox");
        return l.ToArray();
    }

    public static bool NeedsLogin(string msg) =>
        msg.Contains("sign in", StringComparison.OrdinalIgnoreCase) || msg.Contains("log in", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("login", StringComparison.OrdinalIgnoreCase) || msg.Contains("not a bot", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("cookies", StringComparison.OrdinalIgnoreCase) || msg.Contains("age", StringComparison.OrdinalIgnoreCase) && msg.Contains("confirm", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("rate-limit", StringComparison.OrdinalIgnoreCase) || msg.Contains("members-only", StringComparison.OrdinalIgnoreCase);

    private static async Task<(int Code, List<string> Lines, List<string> Errors)> RunFetch(string url, string? browser, bool alt, CancellationToken ct)
    {
        var lines = new List<string>();
        var errors = new List<string>();
        var args = new List<string>
        {
            "--no-playlist", "--skip-download", "--no-warnings", "--no-colors", "--encoding", "utf-8",
            "--socket-timeout", "15",
            "--print", "%(title)j", "--print", "%(uploader)j", "--print", "%(duration)j", "--print", "%(thumbnail)j",
            "--print", "%(extractor_key)j", "--print", "%(id)j", "--print", "%(formats.:.height)j"
        };
        if (File.Exists(Tools.Deno)) { args.Add("--js-runtimes"); args.Add("deno:" + Tools.Deno); }
        if (alt && HostOf(url).Contains("youtu")) { args.Add("--extractor-args"); args.Add(AltClients); }
        if (!string.IsNullOrEmpty(browser)) { args.Add("--cookies-from-browser"); args.Add(browser); }
        args.Add(url);

        int code = await Proc.RunAsync(Tools.YtDlp, args, l => lines.Add(l), l => errors.Add(l), ct);
        return (code, lines, errors);
    }

    public static async Task<LinkInfo> FetchAsync(string url, Action<string>? status, CancellationToken ct)
    {
        await Tools.EnsureYtDlpAsync(status, ct);
        if (HostOf(url).Contains("youtu")) await Tools.EnsureDenoAsync(status, ct);
        status?.Invoke("Looking up the link...");

        var (code, lines, errors) = await RunFetch(url, CookieBrowser, UseAltClients, ct);

        // A login / "are you a bot" wall: first another way in (no sign-in needed), then the cookies of a browser
        if ((code != 0 || lines.Count < 7) && !UseAltClients && CookieBrowser == null && HostOf(url).Contains("youtu") && NeedsLogin(string.Join(" ", errors)))
        {
            status?.Invoke("Trying another way in...");
            var r0 = await RunFetch(url, null, true, ct);
            if (r0.Code == 0 && r0.Lines.Count >= 7)
            {
                UseAltClients = true;
                (code, lines, errors) = r0;
            }
        }
        if ((code != 0 || lines.Count < 7) && CookieBrowser == null && NeedsLogin(string.Join(" ", errors)))
        {
            foreach (string b in CookieBrowsers())
            {
                status?.Invoke("Trying with your " + b + " sign-in...");
                var r = await RunFetch(url, b, UseAltClients, ct);
                if (r.Code == 0 && r.Lines.Count >= 7)
                {
                    CookieBrowser = b;
                    (code, lines, errors) = r;
                    break;
                }
            }
        }

        if (code != 0 || lines.Count < 7)
            throw new InvalidOperationException(FriendlyError(errors));

        string[] v = lines.Skip(lines.Count - 7).ToArray();
        var info = new LinkInfo
        {
            Url = url,
            Title = JStr(v[0]),
            Uploader = JStr(v[1]),
            Duration = double.TryParse(v[2], NumberStyles.Float, Inv, out double d) ? d : 0,
            ThumbUrl = JStr(v[3]),
            Extractor = JStr(v[4]),
            Id = JStr(v[5])
        };
        try
        {
            var hs = JsonSerializer.Deserialize<List<int?>>(v[6]);
            if (hs != null && hs.Count > 0) info.MaxHeight = hs.Max() ?? 0;
        }
        catch { }

        if (info.Title.Length == 0) info.Title = info.Host;
        return info;
    }

    private static string JStr(string s)
    {
        try { return JsonSerializer.Deserialize<string>(s) ?? ""; }
        catch { return s == "NA" || s == "null" ? "" : s; }
    }

    /// <summary>Worth another try (site changed, temporary block, flaky connection)? Not for private videos, sign-in walls, offline PCs.</summary>
    public static bool Retryable(string friendly)
    {
        return !(friendly.Contains("private", StringComparison.OrdinalIgnoreCase) ||
                 friendly.Contains("sign-in", StringComparison.OrdinalIgnoreCase) ||
                 friendly.Contains("No video found", StringComparison.OrdinalIgnoreCase) ||
                 friendly.Contains("No internet", StringComparison.OrdinalIgnoreCase));
    }

    public static string FriendlyError(List<string> errors)
    {
        string e = errors.LastOrDefault(x => x.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) ?? errors.LastOrDefault() ?? "";
        e = e.Replace("ERROR:", "").Trim();
        if (e.Contains("private video", StringComparison.OrdinalIgnoreCase)) return "This video is private";
        if (NeedsLogin(e)) return "YouTube is asking for a sign-in for this video (age or bot check). Sign in to YouTube in Firefox (or Brave/Edge/Chrome), close it, then retry";
        if (e.Contains("is empty", StringComparison.OrdinalIgnoreCase)) return "The site sent an empty file, try another quality";
        if (e.Contains("not available", StringComparison.OrdinalIgnoreCase) && e.Contains("format", StringComparison.OrdinalIgnoreCase))
            return "This quality is not available, pick another one";
        if (e.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)) return "No video found at this link";
        if (e.Contains("urlopen", StringComparison.OrdinalIgnoreCase) || e.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase) ||
            e.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "No internet connection";
        if (e.Length == 0) return "Could not read this link";
        return e.Length > 90 ? e.Substring(0, 90) + "..." : e;
    }

    /// <summary>Small picture of the video (decoded at 2x of the card size: a few KB of RAM).</summary>
    public static async Task<ImageSource?> LoadThumbAsync(LinkInfo info, CancellationToken ct)
    {
        string url = info.ThumbUrl;
        if (info.Extractor.StartsWith("Youtube", StringComparison.OrdinalIgnoreCase) && info.Id.Length > 0)
            url = "https://i.ytimg.com/vi/" + info.Id + "/hqdefault.jpg"; // jpg (the default one is often webp)
        if (url.Length == 0) return null;

        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            using var resp = await Tools.Http.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            long len = resp.Content.Headers.ContentLength ?? 0;
            if (len > 3_000_000) return null;
            byte[] data = await resp.Content.ReadAsByteArrayAsync(ct);

            using var ms = new MemoryStream(data);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 270;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // ---------- Quality list ----------

    /// <summary>720p up to the best the video has (4K / 8K when it exists).</summary>
    public static int[] Qualities(int maxHeight)
    {
        int[] all = { 720, 1080, 1440, 2160, 4320 };
        if (maxHeight <= 0) return new[] { 720, 1080, 1440, 2160 };
        if (maxHeight < 720) return new[] { maxHeight };
        var l = all.Where(h => h <= maxHeight + 20).ToList();
        return l.Count == 0 ? new[] { 720 } : l.ToArray();
    }

    public static string QualityLabel(int h) => h >= 4320 ? "8K" : h >= 2160 ? "4K" : h.ToString(Inv) + "p";

    public static int PickQuality(int wanted, int[] choices)
    {
        int best = choices[0];
        foreach (int c in choices) if (c <= wanted) best = c;
        return best;
    }

    // ---------- Download arguments ----------

    public static List<string> BuildArgs(LinkInfo info, DlOptions o)
    {
        string outTpl = "%(title).120B [%(id)s].%(ext)s";
        var a = new List<string>
        {
            "--no-playlist", "--no-colors", "--no-warnings", "--encoding", "utf-8", "--newline", "--progress",
            "--windows-filenames", "--retries", "8", "--fragment-retries", "12", "--extractor-retries", "3",
            "--socket-timeout", "25", "--concurrent-fragments", "2",
            "--ffmpeg-location", Tools.FFmpegDir,
            "--progress-template",
            "download:[dl] %(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s %(progress.speed)s %(progress.eta)s",
            "--print", "after_move:[file] %(filepath)s",
            "-P", "home:" + o.Dir,
            "-o", outTpl
        };
        // unfinished parts live in a hidden cache folder and are removed when the job ends
        if (o.TempDir.Length > 0) a.AddRange(new[] { "-P", "temp:" + o.TempDir });
        if (File.Exists(Tools.Deno)) a.AddRange(new[] { "--js-runtimes", "deno:" + Tools.Deno });
        if (!string.IsNullOrEmpty(CookieBrowser)) a.AddRange(new[] { "--cookies-from-browser", CookieBrowser });
        if (UseAltClients && o.Attempt < 2 && HostOf(info.Url).Contains("youtu")) a.AddRange(new[] { "--extractor-args", AltClients });
        if (o.Attempt >= 2)
        {
            // last resort: plain IPv4 and the clients that work without extra tokens
            a.Add("--force-ipv4");
            if (HostOf(info.Url).Contains("youtu")) a.AddRange(new[] { "--extractor-args", "youtube:player_client=default,mweb" });
        }
        if (o.LimitMBps > 0)
            a.AddRange(new[] { "--limit-rate", (o.LimitMBps * 1024 * 1024).ToString(Inv) });

        if (o.AudioOnly)
        {
            a.AddRange(new[] { "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0" });
        }
        else
        {
            int h = o.Height;
            string f = o.Attempt == 0
                ? "bv*[height<=" + h + "][ext=mp4]+ba[ext=m4a]/bv*[height<=" + h + "]+ba/b[height<=" + h + "]/bv*+ba/b"
                : "bv*[height<=" + h + "]+ba/b[height<=" + h + "]/bv*+ba/b/best"; // nothing fussy: whatever the site really offers
            a.AddRange(new[] { "-f", f, "--merge-output-format", "mp4" });
        }

        if (o.Subs && !o.AudioOnly)
            a.AddRange(new[] { "--write-subs", "--write-auto-subs", "--sub-langs", "en", "--embed-subs" });

        string s = o.Start.Trim(), e = o.End.Trim();
        if (s.Length > 0 || e.Length > 0)
            a.AddRange(new[] { "--download-sections", "*" + (s.Length > 0 ? s : "0:00") + "-" + (e.Length > 0 ? e : "inf") });

        a.Add(info.Url);
        return a;
    }
}
