using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LiteIsland;

internal sealed class PlTrack
{
    public string Title = "";
    public string Artist = "";
    public string? VideoId;      // YouTube playlists know the video; Spotify / Apple tracks are searched on YouTube
    public double Duration;      // seconds, 0 = unknown
    public string? SpotifyId;    // used to find the cover (Spotify's public oEmbed)
    public string? Cover;        // small cover picture (url)

    public string Query => (Artist.Length > 0 ? Artist + " - " : "") + Title;

    public string Key
    {
        get
        {
            if (!string.IsNullOrEmpty(VideoId)) return Regex.Replace(VideoId!, @"[^A-Za-z0-9_-]", "");
            byte[] h = SHA1.HashData(Encoding.UTF8.GetBytes(Query));
            return "s" + Convert.ToHexString(h, 0, 6).ToLowerInvariant();
        }
    }

    public string Target => !string.IsNullOrEmpty(VideoId) ? "https://www.youtube.com/watch?v=" + VideoId : "ytsearch1:" + Query + " (official audio)";
}

internal sealed class PlInfo
{
    public string Kind = "";
    public string Url = "";
    public string Title = "";
    public string Source = "";   // YouTube, Spotify, Apple Music
    public List<PlTrack> Tracks = new();
}

/// <summary>
/// Playlist links (YouTube, Spotify, Apple Music): reads the track list, and gets the sound of a track from YouTube
/// (yt-dlp) into a small cache, so the helper player can play it without any of those apps.
/// </summary>
internal static class PlaylistService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const int MaxTracks = 150;

    // kept between runs (a played song comes back at once); only the 30 newest files stay
    public static readonly string CacheFolder = Path.Combine(Tools.Dir, "plcache");

    /// <summary>"yt", "sp" or "am" when the text is a link to a whole playlist / album.</summary>
    public static bool TryClassify(string text, out string kind)
    {
        kind = "";
        string t = (text ?? "").Trim();
        if (t.Length < 12 || t.Length > 600 || t.Contains(' ') || t.Contains('\n')) return false;
        if (!Uri.TryCreate(t, UriKind.Absolute, out Uri? u) || (u.Scheme != "http" && u.Scheme != "https")) return false;
        string host = u.Host.ToLowerInvariant();
        string path = u.AbsolutePath.ToLowerInvariant();
        if (host.EndsWith("youtube.com"))
        {
            if (path.TrimEnd('/') == "/playlist" && u.Query.Contains("list=", StringComparison.Ordinal)) { kind = "yt"; return true; }
            return false;
        }
        if (host == "open.spotify.com" && Regex.IsMatch(path, @"/(playlist|album)/[a-z0-9]+")) { kind = "sp"; return true; }
        if (host == "music.apple.com" && (path.Contains("/playlist/") || path.Contains("/album/"))) { kind = "am"; return true; }
        return false;
    }

    // ---------- the track list ----------

    public static async Task<PlInfo> FetchAsync(string url, string kind, Action<string>? status, CancellationToken ct)
    {
        var info = new PlInfo { Url = url, Kind = kind };
        if (kind == "yt") await FetchYouTube(info, status, ct);
        else if (kind == "sp") await FetchSpotify(info, ct);
        else await FetchApple(info, ct);
        if (info.Tracks.Count == 0) throw new InvalidOperationException("No songs found in this link");
        if (info.Tracks.Count > MaxTracks) info.Tracks.RemoveRange(MaxTracks, info.Tracks.Count - MaxTracks);
        if (info.Title.Length == 0) info.Title = "Playlist";
        return info;
    }

    private static async Task FetchYouTube(PlInfo info, Action<string>? status, CancellationToken ct)
    {
        info.Source = "YouTube";
        await Tools.EnsureYtDlpAsync(status, ct);
        await Tools.EnsureDenoAsync(status, ct);
        var lines = new List<string>();
        var errors = new List<string>();
        var args = new List<string>
        {
            "--flat-playlist", "--yes-playlist", "--playlist-end", MaxTracks.ToString(Inv), "--no-warnings", "--no-colors", "--encoding", "utf-8",
            "--socket-timeout", "15",
            "--print", "%(playlist_title)s\t%(id)s\t%(title)s\t%(uploader)s\t%(duration)s"
        };
        if (File.Exists(Tools.Deno)) { args.Add("--js-runtimes"); args.Add("deno:" + Tools.Deno); }
        if (Downloader.UseAltClients) { args.Add("--extractor-args"); args.Add(Downloader.AltClients); }
        if (!string.IsNullOrEmpty(Downloader.CookieBrowser)) { args.Add("--cookies-from-browser"); args.Add(Downloader.CookieBrowser); }
        args.Add(info.Url);

        int code = await Proc.RunAsync(Tools.YtDlp, args, l => { lock (lines) lines.Add(l); }, l => { lock (errors) errors.Add(l); }, ct);
        foreach (string l in lines.ToArray())
        {
            string[] p = l.Split('\t');
            if (p.Length < 5) continue;
            string id = p[1].Trim();
            if (id.Length < 5 || id == "NA") continue;
            if (info.Title.Length == 0 && p[0] != "NA") info.Title = p[0].Trim();
            string title = p[2].Trim();
            if (title == "[Private video]" || title == "[Deleted video]") continue;
            string up = p[3].Trim();
            if (up == "NA") up = "";
            if (up.EndsWith(" - Topic", StringComparison.Ordinal)) up = up.Substring(0, up.Length - 8);
            double.TryParse(p[4], NumberStyles.Float, Inv, out double dur);
            info.Tracks.Add(new PlTrack { Title = title, Artist = up, VideoId = id, Duration = dur, Cover = "https://i.ytimg.com/vi/" + id + "/mqdefault.jpg" });
        }
        if (info.Tracks.Count == 0 && code != 0)
            throw new InvalidOperationException(Downloader.FriendlyError(errors));
    }

    private static async Task<string> GetPage(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        req.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using HttpResponseMessage r = await Tools.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException("The page answered " + (int)r.StatusCode + " (is the playlist public?)");
        return await r.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    private static IEnumerable<JsonElement> Walk(JsonElement root)
    {
        var st = new Stack<JsonElement>();
        st.Push(root);
        while (st.Count > 0)
        {
            JsonElement e = st.Pop();
            if (e.ValueKind == JsonValueKind.Object)
            {
                yield return e;
                foreach (JsonProperty p in e.EnumerateObject().Reverse()) st.Push(p.Value);
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement c in e.EnumerateArray().Reverse()) st.Push(c);
            }
        }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task FetchSpotify(PlInfo info, CancellationToken ct)
    {
        info.Source = "Spotify";
        Match m = Regex.Match(info.Url, @"/(playlist|album)/([A-Za-z0-9]+)");
        if (!m.Success) throw new InvalidOperationException("Not a Spotify playlist link");
        string html = await GetPage("https://open.spotify.com/embed/" + m.Groups[1].Value + "/" + m.Groups[2].Value, ct);
        Match js = Regex.Match(html, "<script id=\"__NEXT_DATA__\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline);
        if (!js.Success) throw new InvalidOperationException("Spotify did not give the song list (is the playlist public?)");
        using JsonDocument doc = JsonDocument.Parse(js.Groups[1].Value);
        var seen = new HashSet<string>();
        foreach (JsonElement o in Walk(doc.RootElement))
        {
            if (info.Title.Length == 0 && o.TryGetProperty("trackList", out _))
                info.Title = Str(o, "name") ?? Str(o, "title") ?? "";
            string? title = Str(o, "title"), sub = Str(o, "subtitle");
            if (title == null || sub == null) continue;
            string? uri = Str(o, "uri");
            if (uri != null && !uri.Contains(":track:", StringComparison.Ordinal)) continue;
            if (!seen.Add(title + "|" + sub)) continue;
            double dur = 0;
            if (o.TryGetProperty("duration", out JsonElement d) && d.ValueKind == JsonValueKind.Number) dur = d.GetDouble() / 1000.0;
            string? sid = null;
            if (uri != null) { Match um = Regex.Match(uri, @":track:([A-Za-z0-9]+)"); if (um.Success) sid = um.Groups[1].Value; }
            info.Tracks.Add(new PlTrack { Title = title.Trim(), Artist = sub.Replace(' ', ' ').Trim(), Duration = dur, SpotifyId = sid });
        }
        if (info.Title.Length == 0)
        {
            Match t = Regex.Match(html, "<title>(.*?)</title>", RegexOptions.Singleline);
            if (t.Success) info.Title = WebUtility.HtmlDecode(Regex.Replace(t.Groups[1].Value, @"\s*\|\s*Spotify.*$", "")).Trim();
        }
    }

    private static async Task FetchApple(PlInfo info, CancellationToken ct)
    {
        info.Source = "Apple Music";
        string html = await GetPage(info.Url, ct);
        Match og = Regex.Match(html, "<meta property=\"og:title\" content=\"([^\"]+)\"");
        if (og.Success) info.Title = WebUtility.HtmlDecode(og.Groups[1].Value).Trim();
        info.Title = Regex.Replace(info.Title, @"\s+(by|on)\s+.*$", "", RegexOptions.IgnoreCase).Trim();

        Match js = Regex.Match(html, "<script type=\"application/json\" id=\"serialized-server-data\">(.*?)</script>", RegexOptions.Singleline);
        if (!js.Success) throw new InvalidOperationException("Apple Music did not give the song list (is the playlist public?)");
        using JsonDocument doc = JsonDocument.Parse(js.Groups[1].Value);

        var strict = new List<PlTrack>();
        var loose = new List<PlTrack>();
        var seenS = new HashSet<string>();
        var seenL = new HashSet<string>();
        foreach (JsonElement o in Walk(doc.RootElement))
        {
            string? title = Str(o, "title"), artist = Str(o, "artistName");
            if (title == null || artist == null) continue;
            bool song = o.TryGetProperty("contentDescriptor", out JsonElement cd) && cd.ValueKind == JsonValueKind.Object && Str(cd, "kind") == "song";
            double dur = 0;
            bool hasDur = o.TryGetProperty("duration", out JsonElement d) && d.ValueKind == JsonValueKind.Number;
            if (hasDur) dur = d.GetDouble() / 1000.0;
            var t = new PlTrack { Title = title.Trim(), Artist = artist.Trim(), Duration = dur };
            if (o.TryGetProperty("artwork", out JsonElement aw) && aw.ValueKind == JsonValueKind.Object &&
                aw.TryGetProperty("dictionary", out JsonElement ad) && ad.ValueKind == JsonValueKind.Object && Str(ad, "url") is string au)
                t.Cover = au.Replace("{w}", "120").Replace("{h}", "120").Replace("{c}", "bb").Replace("{f}", "jpg");
            if (song && seenS.Add(title + "|" + artist)) strict.Add(t);
            else if (hasDur && seenL.Add(title + "|" + artist)) loose.Add(t);
        }
        info.Tracks.AddRange(strict.Count > 0 ? strict : loose);
    }

    // ---------- the sound of a track ----------

    private static readonly ConcurrentDictionary<string, Task<string>> Inflight = new();

    /// <summary>One download per song, even when "play" and "get ready for the next one" ask for it together.</summary>
    public static Task<string> GetAudioShared(PlTrack t, Action<string>? status, CancellationToken life) =>
        Inflight.GetOrAdd(t.Key, _ => Task.Run(async () =>
        {
            try { return await GetAudioAsync(t, status, life).ConfigureAwait(false); }
            finally { Inflight.TryRemove(t.Key, out Task<string>? _); }
        }));

    /// <summary>The sound of the track as an .m4a in the cache (downloaded once). Played by the helper player.</summary>
    public static async Task<string> GetAudioAsync(PlTrack t, Action<string>? status, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheFolder);
        string final = Path.Combine(CacheFolder, t.Key + ".m4a");
        if (File.Exists(final) && new FileInfo(final).Length > 1024) { try { File.SetLastWriteTimeUtc(final, DateTime.UtcNow); } catch { } return final; }

        await Tools.EnsureYtDlpAsync(status, ct);

        // 1) the fast way: the "android_vr" player of YouTube gives a ready .m4a with no JavaScript and no converting
        var errors = new List<string>();
        var fast = new List<string>
        {
            "--no-playlist", "--no-warnings", "--no-colors", "--no-mtime", "--force-ipv4", "--encoding", "utf-8", "--socket-timeout", "15", "--retries", "3",
            "--extractor-args", "youtube:player_client=android_vr",
            "-f", "ba[ext=m4a]",
            "-P", "home:" + CacheFolder, "-o", t.Key + ".%(ext)s", t.Target
        };
        status?.Invoke("Getting the song...");
        int code = await Proc.RunAsync(Tools.YtDlp, fast, null, l => { lock (errors) errors.Add(l); }, ct, null, System.Diagnostics.ProcessPriorityClass.Normal);
        if (File.Exists(final) && new FileInfo(final).Length > 1024) { Prune(); return final; }

        // 2) the slow way: normal players + the JavaScript engine, converted to .m4a when needed
        await Tools.EnsureFFmpegAsync(status, ct);
        await Tools.EnsureDenoAsync(status, ct);
        errors.Clear();
        var args = new List<string>
        {
            "--no-playlist", "--no-warnings", "--no-colors", "--no-mtime", "--force-ipv4", "--encoding", "utf-8", "--socket-timeout", "20", "--retries", "5",
            "-f", "ba[ext=m4a]/ba", "-x", "--audio-format", "m4a", "--audio-quality", "0",
            "--ffmpeg-location", Tools.FFmpegDir,
            "-P", "home:" + CacheFolder, "-o", t.Key + ".%(ext)s"
        };
        if (File.Exists(Tools.Deno)) { args.Add("--js-runtimes"); args.Add("deno:" + Tools.Deno); }
        if (Downloader.UseAltClients) { args.Add("--extractor-args"); args.Add(Downloader.AltClients); }
        if (!string.IsNullOrEmpty(Downloader.CookieBrowser)) { args.Add("--cookies-from-browser"); args.Add(Downloader.CookieBrowser); }
        args.Add(t.Target);
        status?.Invoke("Getting the song...");
        code = await Proc.RunAsync(Tools.YtDlp, args, null, l => { lock (errors) errors.Add(l); }, ct, null, System.Diagnostics.ProcessPriorityClass.Normal);
        if (code != 0 && !(File.Exists(final) && new FileInfo(final).Length > 1024))
        {
            List<string> copy;
            lock (errors) copy = errors.ToList();
            if (Downloader.NeedsLogin(string.Join(" ", copy)) && !Downloader.UseAltClients)
            {
                Downloader.UseAltClients = true;
                return await GetAudioAsync(t, status, ct);
            }
            throw new InvalidOperationException(Downloader.FriendlyError(copy));
        }
        if (!File.Exists(final)) throw new InvalidOperationException("The song could not be saved");
        Prune();
        return final;
    }

    /// <summary>Keeps the 30 newest cached songs / covers.</summary>
    public static void Prune()
    {
        try
        {
            var files = new DirectoryInfo(CacheFolder).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            for (int i = 60; i < files.Count; i++) { try { files[i].Delete(); } catch { } }
        }
        catch { }
    }

    // ---------- covers ----------

    /// <summary>The cover url of a track (Spotify tracks ask Spotify's public oEmbed once). Empty when there is none.</summary>
    public static async Task<string> CoverUrlAsync(PlTrack t, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(t.Cover)) return t.Cover!;
        if (!string.IsNullOrEmpty(t.SpotifyId))
        {
            try
            {
                string json = await GetPage("https://open.spotify.com/oembed?url=" + Uri.EscapeDataString("https://open.spotify.com/track/" + t.SpotifyId), ct);
                using JsonDocument d = JsonDocument.Parse(json);
                string? u = Str(d.RootElement, "thumbnail_url");
                if (!string.IsNullOrEmpty(u)) { t.Cover = u; return u!; }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        // fallback: the iTunes search (free, no key, fast) finds the album cover by title + artist
        try
        {
            string q = Uri.EscapeDataString((t.Title + " " + t.Artist).Trim());
            string json = await GetPage("https://itunes.apple.com/search?media=music&entity=song&limit=1&term=" + q, ct);
            using JsonDocument d = JsonDocument.Parse(json);
            if (d.RootElement.TryGetProperty("results", out JsonElement rs) && rs.ValueKind == JsonValueKind.Array && rs.GetArrayLength() > 0)
            {
                string? u = Str(rs[0], "artworkUrl100");
                if (!string.IsNullOrEmpty(u)) { u = u!.Replace("100x100bb", "300x300bb"); t.Cover = u; return u; }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return "";
    }

    public static async Task<byte[]?> CoverBytesAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(12));
            return await Tools.Http.GetByteArrayAsync(url, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>A cover as a small .jpg file next to the song: the helper player gives it to Windows' media controls (the island's music page).</summary>
    public static async Task<string?> CoverFileAsync(PlTrack t, CancellationToken ct)
    {
        string path = Path.Combine(CacheFolder, t.Key + ".jpg");
        if (File.Exists(path) && new FileInfo(path).Length > 200) return path;
        string url = await CoverUrlAsync(t, ct);
        if (url.Length == 0) return null;
        byte[]? b = await CoverBytesAsync(url, ct);
        if (b == null || b.Length < 200) return null;
        Directory.CreateDirectory(CacheFolder);
        await File.WriteAllBytesAsync(path, b, ct).ConfigureAwait(false);
        return path;
    }

    // ---------- saved playlists (stay after a restart) ----------

    private static readonly string LibraryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteIsland", "playlists.json");
    public static List<PlInfo> Library { get; private set; } = new();
    private static readonly JsonSerializerOptions JsonOpt = new() { IncludeFields = true, WriteIndented = false };

    public static void LoadLibrary()
    {
        try
        {
            if (File.Exists(LibraryFile))
                Library = JsonSerializer.Deserialize<List<PlInfo>>(File.ReadAllText(LibraryFile), JsonOpt) ?? new List<PlInfo>();
        }
        catch { Library = new List<PlInfo>(); }
    }

    public static void SaveLibrary()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LibraryFile)!);
            File.WriteAllText(LibraryFile + ".tmp", JsonSerializer.Serialize(Library, JsonOpt));
            File.Move(LibraryFile + ".tmp", LibraryFile, true);
        }
        catch { }
    }

    public static void AddToLibrary(PlInfo p)
    {
        Library.RemoveAll(x => string.Equals(x.Url, p.Url, StringComparison.OrdinalIgnoreCase));
        Library.Insert(0, p);
        while (Library.Count > 20) Library.RemoveAt(Library.Count - 1);
        SaveLibrary();
    }

    public static void RemoveFromLibrary(PlInfo p)
    {
        Library.RemoveAll(x => ReferenceEquals(x, p) || string.Equals(x.Url, p.Url, StringComparison.OrdinalIgnoreCase));
        SaveLibrary();
    }

    // ---------- keeping a track on the PC ----------

    public static string SafeName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Trim().TrimEnd('.');
        return s.Length > 110 ? s.Substring(0, 110) : (s.Length == 0 ? "Song" : s);
    }

    /// <summary>Saves one track as an mp3 (best quality) in the folder. Returns the file.</summary>
    public static async Task<string> SaveTrackAsync(PlTrack t, string dir, int number, Action<string>? status, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        string name = number.ToString("00", Inv) + " " + SafeName(t.Query);
        string final = Path.Combine(dir, name + ".mp3");
        if (File.Exists(final)) return final;

        await Tools.EnsureYtDlpAsync(status, ct);
        await Tools.EnsureFFmpegAsync(status, ct);
        await Tools.EnsureDenoAsync(status, ct);

        var errors = new List<string>();
        var args = new List<string>
        {
            "--no-playlist", "--no-warnings", "--no-colors", "--encoding", "utf-8", "--socket-timeout", "20", "--retries", "5",
            "--windows-filenames",
            "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0", "--embed-metadata",
            "--ffmpeg-location", Tools.FFmpegDir,
            "-P", "home:" + dir, "-o", name + ".%(ext)s"
        };
        if (File.Exists(Tools.Deno)) { args.Add("--js-runtimes"); args.Add("deno:" + Tools.Deno); }
        if (Downloader.UseAltClients) { args.Add("--extractor-args"); args.Add(Downloader.AltClients); }
        if (!string.IsNullOrEmpty(Downloader.CookieBrowser)) { args.Add("--cookies-from-browser"); args.Add(Downloader.CookieBrowser); }
        args.Add(t.Target);

        int code = await Proc.RunAsync(Tools.YtDlp, args, null, l => { lock (errors) errors.Add(l); }, ct);
        if (code != 0 && !File.Exists(final))
        {
            List<string> copy;
            lock (errors) copy = errors.ToList();
            if (Downloader.NeedsLogin(string.Join(" ", copy)) && !Downloader.UseAltClients)
            {
                Downloader.UseAltClients = true;
                return await SaveTrackAsync(t, dir, number, status, ct);
            }
            throw new InvalidOperationException(Downloader.FriendlyError(copy));
        }
        return final;
    }
}
