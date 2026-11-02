using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace LiteIsland;

public enum WallSource { FourK, Wallhaven }

/// <summary>One wallpaper shown as a card in the Wallpapers window.</summary>
public sealed class WallItem
{
    public WallSource Source { get; init; }
    public string Title { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string? ThumbUrl { get; set; }
    public string FullUrl { get; set; } = "";     // Wallhaven: the direct link to the full size picture
    public string Resolution { get; set; } = "";  // Wallhaven: for example 3840x2160
}

public sealed record DownloadInfo(string Url, string Referer, string FileName);

/// <summary>
/// Wallpapers: reads the public pages of 4kwallpapers.com (wallhaven.cc wallhaven.cc is used automatically when 4kwallpapers.com refuses),
/// downloads the chosen file into %AppData%\LiteIsland\Wallpapers and sets it.
/// Nothing runs until the Wallpapers window is opened. Thumbnails are small and only kept in memory.
/// </summary>
public static class WallpaperService
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteIsland", "Wallpapers");
    public static string StaticDir => Path.Combine(Root, "Static");

    private static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".bmp" };

    public static bool IsImage(string path) => ImageExt.Contains(Path.GetExtension(path).ToLowerInvariant());

    // ---------- HTTP ----------

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        c.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        // Look like a normal Chrome page visit (some sites answer 403 Forbidden to anything that does not)
        c.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"124\", \"Google Chrome\";v=\"124\", \"Not-A.Brand\";v=\"99\"");
        c.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        c.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        return c;
    }

    private static async Task<string> GetHtmlAsync(string url, CancellationToken ct)
    {
        // Second try with another browser identity if the site refuses the first one (403 / 429 / 503)
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (attempt == 1)
            {
                req.Headers.UserAgent.Clear();
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:125.0) Gecko/20100101 Firefox/125.0");
                req.Headers.Referrer = new Uri("https://www.google.com/");
            }
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (attempt == 0 && (code == 403 || code == 429 || code == 503))
            {
                await Task.Delay(700, ct).ConfigureAwait(false);
                continue;
            }
            resp.EnsureSuccessStatusCode();
        }
        throw new HttpRequestException("The wallpaper site did not answer.");
    }

    // ---------- Never show an error if there is any other way: 4kwallpapers -> Wallhaven -> last saved list ----------

    private static DateTime _fourKDownUntil = DateTime.MinValue;

    public static async Task<List<WallItem>> BrowseSmartAsync(int page, string catPath, CancellationToken ct)
    {
        string key = (catPath ?? "") + "|" + page;
        Exception? err = null;

        if (DateTime.UtcNow >= _fourKDownUntil)
        {
            try
            {
                var l = await BrowseAsync(WallSource.FourK, "", page, ct, catPath).ConfigureAwait(false);
                if (l.Count > 0) SaveCache("4k|" + key, l);
                return l;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                err = ex;
                _fourKDownUntil = DateTime.UtcNow.AddMinutes(10); // the site refuses us: go straight to the other source for a while
            }
        }

        try
        {
            var l = await BrowseWallhavenAsync(catPath ?? "", page, ct).ConfigureAwait(false);
            if (l.Count > 0) SaveCache("wh|" + key, l);
            return l;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { err ??= ex; }

        var cached = LoadCache("wh|" + key) ?? LoadCache("4k|" + key);
        if (cached != null && cached.Count > 0) return cached;
        throw err ?? new HttpRequestException("No wallpapers could be loaded.");
    }

    private static (string q, string cats, string sort, string range) WhMap(string slug)
    {
        switch (slug)
        {
            case "": return ("", "111", "date_added", "");
            case "most-popular-4k-wallpapers": return ("", "111", "toplist", "1M");
            case "best-4k-wallpapers": return ("", "111", "favorites", "");
            case "anime": return ("", "010", "toplist", "1y");
            case "people": return ("", "001", "toplist", "1y");
            case "black-dark": return ("dark", "111", "toplist", "1y");
            case "graphics-cgi": return ("cgi", "111", "toplist", "1y");
            case "celebrations": return ("celebration", "111", "toplist", "1y");
            case "gradients": return ("gradient", "111", "toplist", "1y");
            case "world": return ("world", "111", "toplist", "1y");
            default: return (slug.Replace('-', ' '), "111", "toplist", "1y");
        }
    }

    // wallhaven.cc public API (safe-for-work pictures only, no key needed)
    private static async Task<List<WallItem>> BrowseWallhavenAsync(string catPath, int page, CancellationToken ct)
    {
        var (q, cats, sort, range) = WhMap((catPath ?? "").Trim('/'));
        string url = "https://wallhaven.cc/api/v1/search?q=" + Uri.EscapeDataString(q) +
                     "&categories=" + cats + "&purity=100&sorting=" + sort + "&order=desc&page=" + Math.Max(1, page) +
                     "&atleast=1920x1080&ratios=landscape" + (range.Length > 0 ? "&topRange=" + range : "");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var list = new List<WallItem>();
        foreach (var d in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            string id = d.GetProperty("id").GetString() ?? "";
            string full = d.GetProperty("path").GetString() ?? "";
            string res = d.TryGetProperty("resolution", out var r) ? r.GetString() ?? "" : "";
            string thumb = d.GetProperty("thumbs").TryGetProperty("large", out var t) ? t.GetString() ?? "" : "";
            if (id.Length == 0 || full.Length == 0) continue;

            list.Add(new WallItem
            {
                Source = WallSource.Wallhaven,
                Title = res.Length > 0 ? "Wallpaper \u00B7 " + res.Replace("x", " \u00D7 ") : "Wallpaper",
                PageUrl = "https://wallhaven.cc/w/" + id,
                ThumbUrl = thumb.Length > 0 ? thumb : null,
                FullUrl = full,
                Resolution = res
            });
        }
        return list;
    }

    // ---------- Last good list, kept on disk (used only when nothing can be loaded) ----------

    private sealed class CacheItem
    {
        public int Source { get; set; }
        public string Title { get; set; } = "";
        public string PageUrl { get; set; } = "";
        public string ThumbUrl { get; set; } = "";
        public string FullUrl { get; set; } = "";
        public string Resolution { get; set; } = "";
    }

    private static string CacheFile(string key) =>
        Path.Combine(Root, "cache2", Regex.Replace(key, @"[^A-Za-z0-9]+", "_") + ".json");

    private static void SaveCache(string key, List<WallItem> items)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(Root, "cache"));
            var dto = items.Select(i => new CacheItem
            {
                Source = (int)i.Source, Title = i.Title, PageUrl = i.PageUrl, ThumbUrl = i.ThumbUrl ?? "",
                FullUrl = i.FullUrl, Resolution = i.Resolution
            }).ToList();
            File.WriteAllText(CacheFile(key), JsonSerializer.Serialize(dto));
        }
        catch { }
    }

    private static List<WallItem>? LoadCache(string key)
    {
        try
        {
            string f = CacheFile(key);
            if (!File.Exists(f)) return null;
            var dto = JsonSerializer.Deserialize<List<CacheItem>>(File.ReadAllText(f));
            return dto?.Select(c => new WallItem
            {
                Source = (WallSource)c.Source, Title = c.Title, PageUrl = c.PageUrl,
                ThumbUrl = c.ThumbUrl.Length > 0 ? c.ThumbUrl : null, FullUrl = c.FullUrl, Resolution = c.Resolution
            }).ToList();
        }
        catch { return null; }
    }

    // ---------- Browsing ----------

    public static async Task<List<WallItem>> BrowseAsync(WallSource src, string query, int page, CancellationToken ct, string? fourKPath = null)
    {
        string q = Uri.EscapeDataString((query ?? "").Trim());
        bool has = q.Length > 0;

        if (src == WallSource.FourK)
        {
            // 4kwallpapers.com: the search page has used both ?text= and ?q=, so try both
            var urls = new List<string>();
            if (has)
            {
                string pg = page > 1 ? $"&page={page}" : "";
                urls.Add($"https://4kwallpapers.com/search/?text={q}{pg}");
                urls.Add($"https://4kwallpapers.com/search/?q={q}{pg}");
            }
            else
            {
                // A category / list such as "anime" or "most-popular-4k-wallpapers" ("" = recent)
                string path = (fourKPath ?? "").Trim('/');
                string baseUrl = path.Length == 0 ? "https://4kwallpapers.com/" : $"https://4kwallpapers.com/{path}/";
                urls.Add(page > 1 ? $"{baseUrl}?page={page}" : baseUrl);
            }

            Exception? last = null;
            foreach (var u in urls)
            {
                try
                {
                    var found = ParseFourK(await GetHtmlAsync(u, ct).ConfigureAwait(false));
                    if (found.Count > 0) return found;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { last = ex; }
            }
            if (last != null && !has) throw last;
            return new List<WallItem>();
        }

        return new List<WallItem>();
    }

    private static readonly Regex AnchorRx = new(@"<a\b([^>]*)>(.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ImgRx = new(@"<img\b[^>]*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, @"(?<![\w-])" + name + @"\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
    }

    private static string? ImgUrl(string imgTag)
    {
        foreach (var n in new[] { "data-src", "data-lazy-src", "data-original", "src" })
        {
            var v = Attr(imgTag, n);
            if (!string.IsNullOrWhiteSpace(v) && !v.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return v.Trim();
        }
        var ss = Attr(imgTag, "srcset") ?? Attr(imgTag, "data-srcset");
        if (!string.IsNullOrWhiteSpace(ss))
        {
            var first = ss.Split(',')[0].Trim().Split(' ')[0];
            if (first.Length > 0 && !first.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return first;
        }
        return null;
    }

    private static string? Abs(string baseUrl, string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return null;
        try { return new Uri(new Uri(baseUrl), rel.Trim()).AbsoluteUri; }
        catch { return null; }
    }

    // 4kwallpapers.com: <a href="/cat/name-1234.html"><img src=".../images/walls/thumbs/1234.jpg"></a>
    private static List<WallItem> ParseFourK(string html)
    {
        int cut = html.IndexOf("Trending Wallpapers", StringComparison.OrdinalIgnoreCase);
        if (cut > 3000) html = html.Substring(0, cut); // skip the "trending" strip under the list

        var list = new List<WallItem>();
        var seen = new HashSet<string>();
        foreach (Match m in AnchorRx.Matches(html))
        {
            string attrs = m.Groups[1].Value;
            string inner = m.Groups[2].Value;
            string? href = Attr(attrs, "href");
            if (href == null || !href.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) continue;
            var im = ImgRx.Match(inner);
            if (!im.Success) continue;
            string? thumb = ImgUrl(im.Value);
            if (thumb == null || thumb.IndexOf("/thumbs/", StringComparison.OrdinalIgnoreCase) < 0) continue;

            string? page = Abs("https://4kwallpapers.com/", href);
            string? t = Abs("https://4kwallpapers.com/", thumb);
            if (page == null || t == null || !seen.Add(page)) continue;

            string title = Attr(attrs, "title") ?? "";
            if (title.Length == 0)
            {
                string alt = Attr(im.Value, "alt") ?? "Wallpaper";
                title = alt.Split(',')[0].Trim();
            }
            title = Regex.Replace(title, @"\s*Wallpapers?$", "", RegexOptions.IgnoreCase).Trim();
            if (title.Length == 0) title = "Wallpaper";

            list.Add(new WallItem { Source = WallSource.FourK, Title = title, PageUrl = page, ThumbUrl = t });
        }
        return list;
    }

    // ---------- Thumbnails ----------

    /// <summary>Downloads a small thumbnail (decoded at 320 px wide, so it stays tiny in memory).</summary>
    public static async Task<BitmapImage?> LoadThumbAsync(WallItem it, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(it.ThumbUrl))
                it.ThumbUrl = await ResolveThumbFromPageAsync(it.PageUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(it.ThumbUrl)) return null;

            using var req = new HttpRequestMessage(HttpMethod.Get, it.ThumbUrl);
            req.Headers.Referrer = new Uri(it.PageUrl);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            byte[] data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            using var ms = new MemoryStream(data);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 320;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ResolveThumbFromPageAsync(string pageUrl, CancellationToken ct)
    {
        string html = await GetHtmlAsync(pageUrl, ct).ConfigureAwait(false);
        var m = Regex.Match(html, @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(html, @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']", RegexOptions.IgnoreCase);
        return m.Success ? Abs(pageUrl, WebUtility.HtmlDecode(m.Groups[1].Value)) : null;
    }

    // ---------- Finding the real file ----------

    private static string SlugOf(string pageUrl)
    {
        string last = pageUrl.TrimEnd('/');
        int i = last.LastIndexOf('/');
        if (i >= 0) last = last.Substring(i + 1);
        int q = last.IndexOf('?');
        if (q >= 0) last = last.Substring(0, q);
        last = Regex.Replace(last.ToLowerInvariant(), @"[^a-z0-9\-]+", "-").Trim('-');
        if (last.Length == 0) last = "wallpaper";
        return last.Length > 80 ? last.Substring(0, 80) : last;
    }

    /// <summary>
    /// Opens the wallpaper's page and looks for the direct file link. Returns null if the site does not
    /// give one (then the app opens the page in the browser instead).
    /// </summary>
    public static async Task<DownloadInfo?> ResolveDownloadAsync(WallItem it, bool lite, int screenWidth, CancellationToken ct)
    {
        if (it.Source == WallSource.Wallhaven)
        {
            if (string.IsNullOrEmpty(it.FullUrl)) return null;
            string wext = Path.GetExtension(new Uri(it.FullUrl).AbsolutePath);
            if (string.IsNullOrEmpty(wext)) wext = ".jpg";
            string wres = it.Resolution.Length > 0 ? "-" + it.Resolution : "";
            return new DownloadInfo(it.FullUrl, "https://wallhaven.cc/", "wh-" + SlugOf(it.PageUrl) + wres + wext.ToLowerInvariant());
        }

        string html = await GetHtmlAsync(it.PageUrl, ct).ConfigureAwait(false);
        string slug = SlugOf(it.PageUrl);

        if (it.Source == WallSource.FourK)
        {
            var cands = Regex.Matches(html,
                    @"(?:href|src|content|data-src)\s*=\s*[""']([^""']*?/images/wallpapers/[^""']+?\.(?:jpe?g|png))[""']",
                    RegexOptions.IgnoreCase)
                .Select(x => x.Groups[1].Value).Distinct().ToList();
            if (cands.Count == 0) return null;

            // sizes written in the file name, e.g. name-3840x2160-1234.jpg
            var sized = new List<(string url, int w, int h)>();
            foreach (var c in cands)
            {
                var rm = Regex.Match(c, @"-(\d{3,5})x(\d{3,5})-\d+\.", RegexOptions.IgnoreCase);
                int w = rm.Success ? int.Parse(rm.Groups[1].Value) : 0;
                int h = rm.Success ? int.Parse(rm.Groups[2].Value) : 0;
                sized.Add((c, w, h));
            }
            var land = sized.Where(s => s.w == 0 || s.w >= s.h).ToList();
            if (land.Count == 0) land = sized;

            // always the sharpest file the site offers (4K or bigger), never a smaller copy
            var best = land.OrderByDescending(s => s.w).ThenByDescending(s => s.h).First();

            string? url = Abs(it.PageUrl, best.url);
            if (url == null) return null;
            string ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            // the size is part of the name, so an older low quality copy is never reused
            string res = best.w > 0 ? $"-{best.w}x{best.h}" : "";
            return new DownloadInfo(url, it.PageUrl, "4k-" + slug + res + ext.ToLowerInvariant());
        }

        return null;
    }

    // ---------- Downloading ----------

    public static async Task DownloadAsync(string url, string referer, string destPath, IProgress<double> progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(referer)) req.Headers.Referrer = new Uri(referer);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        string type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The site did not give a file.");

        long total = resp.Content.Headers.ContentLength ?? -1;
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        string part = destPath + ".part";
        try
        {
            using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buf = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total > 0) progress.Report((double)read / total);
                }
            }
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(part, destPath);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }

    // ---------- Local library ----------

    public static List<string> LibraryFiles()
    {
        var all = new List<string>();
        try
        {
            if (Directory.Exists(StaticDir))
                all.AddRange(Directory.EnumerateFiles(StaticDir).Where(IsImage));
        }
        catch { }
        return all.OrderByDescending(f => File.GetLastWriteTime(f)).Take(300).ToList();
    }

    // ---------- Setting a still picture as the Windows wallpaper ----------

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(int action, int param, string? value, int flags);

    public static void SetStatic(string path)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
            key?.SetValue("WallpaperStyle", "10"); // Fill
            key?.SetValue("TileWallpaper", "0");
        }
        catch { }
        SystemParametersInfo(0x0014, 0, path, 0x01 | 0x02); // SPI_SETDESKWALLPAPER, update + broadcast
    }
}
