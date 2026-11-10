using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LiteIsland;

/// <summary>
/// Live wallpapers (videos) from MoeWalls: reads the public listing pages (name, picture, page). The wallpaper's page holds a
/// download token (the "Download Wallpaper" button's data-url); the file itself comes from go.moewalls.com/download.php?video=token.
/// Page data is cached and read ahead, so a click starts the download at once.
/// </summary>
internal static class LiveService
{
    public static readonly string LiveDir = Path.Combine(WallpaperService.Root, "Live");
    private static readonly string[] VideoExt = { ".mp4", ".webm", ".mkv", ".mov", ".avi", ".m4v" };

    public static bool IsVideo(string path) => VideoExt.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static List<string> LiveFiles()
    {
        try
        {
            if (!Directory.Exists(LiveDir)) return new List<string>();
            return Directory.EnumerateFiles(LiveDir).Where(IsVideo).OrderByDescending(f => File.GetLastWriteTime(f)).Take(200).ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>Newest video in the Downloads folder (last 3 days). Used after a manual download from the site.</summary>
    public static string? LatestDownload()
    {
        try
        {
            string dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(dl)) return null;
            return Directory.EnumerateFiles(dl).Where(IsVideo)
                .Select(f => new FileInfo(f))
                .Where(f => f.Length > 1_000_000 && f.LastWriteTime > DateTime.Now.AddDays(-3))
                .OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    // ---------- browsing ----------

    private const string Host = "https://moewalls.com";
    private const string DownloadBase = "https://go.moewalls.com/download.php?video=";

    public static async Task<List<WallItem>> BrowseAsync(string query, int page, CancellationToken ct)
    {
        string q = Uri.EscapeDataString((query ?? "").Trim());
        int p = Math.Max(1, page);
        string url = q.Length == 0 ? (p <= 1 ? Host + "/" : Host + "/page/" + p + "/")
                                   : (p <= 1 ? Host + "/?s=" + q : Host + "/page/" + p + "/?s=" + q);
        string html = await WallpaperService.GetHtmlAsync(url, ct).ConfigureAwait(false);
        return Parse(html);
    }

    private static readonly Regex AnchorRx = new(@"<a\b[^>]*?href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex ImgRx = new(@"(?:data-src|data-lazy-src|data-original|src)\s*=\s*[""']([^""']+\.(?:jpe?g|png|webp)[^""']*)[""']", RegexOptions.IgnoreCase);

    private static List<WallItem> Parse(string html)
    {
        var map = new Dictionary<string, WallItem>();
        var order = new List<string>();
        foreach (Match m in AnchorRx.Matches(html))
        {
            string href = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            string abs = href.StartsWith("//") ? "https:" + href : href.StartsWith("/") ? Host + href : href;
            if (!IsDetail(abs)) continue;
            string key = abs.Split('?')[0].TrimEnd('/');
            if (!map.TryGetValue(key, out WallItem? item))
            {
                item = new WallItem { Source = WallSource.MoeWalls, Title = Humanize(key), PageUrl = key + "/" };
                map[key] = item;
                order.Add(key);
            }
            if (item.ThumbUrl == null)
            {
                foreach (Match im in ImgRx.Matches(m.Groups[2].Value))
                {
                    string u = WebUtility.HtmlDecode(im.Groups[1].Value);
                    if (Regex.IsMatch(u, "logo|avatar|gravatar|icon|default_share|sprite", RegexOptions.IgnoreCase)) continue;
                    item.ThumbUrl = u.StartsWith("//") ? "https:" + u : u.StartsWith("/") ? Host + u : u;
                    break;
                }
            }
        }
        return order.Select(k => map[k]).ToList();
    }

    private static bool IsDetail(string abs)
    {
        if (!Uri.TryCreate(abs, UriKind.Absolute, out Uri? u)) return false;
        string[] seg = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return u.Host.EndsWith("moewalls.com") && seg.Length == 2 && seg[1].EndsWith("live-wallpaper", StringComparison.OrdinalIgnoreCase);
    }

    private static string Humanize(string pageUrl)
    {
        string slug = pageUrl.TrimEnd('/');
        slug = slug.Substring(slug.LastIndexOf('/') + 1);
        int i = slug.IndexOf("-live-wallpaper", StringComparison.OrdinalIgnoreCase);
        if (i > 0) slug = slug.Substring(0, i);
        var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? "Live wallpaper" : string.Join(" ", words.Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
    }

    // ---------- the wallpaper's page: download token + preview clip ----------

    internal sealed record LivePage(string? Token, string? PreviewUrl);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<LivePage>> Pages = new();
    private static readonly Regex TagRx = new(@"<a\b[^>]*>", RegexOptions.IgnoreCase);
    private static readonly Regex DataUrlRx = new(@"data-url\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
    private static readonly Regex PreviewRx = new(@"[""']((?:https?:)?(?://moewalls\.com)?/wp-content/uploads/[^""']+\.(?:webm|mp4))[""']", RegexOptions.IgnoreCase);

    /// <summary>Reads (once) the page of a wallpaper. Later calls, hover or click, get the same result at once.</summary>
    public static Task<LivePage> GetPageAsync(string pageUrl)
    {
        return Pages.GetOrAdd(pageUrl, u => Task.Run(async () =>
        {
            try
            {
                string html = await WallpaperService.GetHtmlAsync(u, CancellationToken.None).ConfigureAwait(false);
                // every download button of the page; when there are several sizes (HD / 2K / 4K) take the sharpest one
                string? token = null;
                int bestScore = int.MinValue;
                foreach (Match t in TagRx.Matches(html))
                {
                    string tag = t.Value;
                    bool isBtn = tag.Contains("moe-download", StringComparison.OrdinalIgnoreCase)
                                 || (tag.Contains("data-url", StringComparison.OrdinalIgnoreCase) && tag.Contains("data-id", StringComparison.OrdinalIgnoreCase));
                    if (!isBtn) continue;
                    Match d = DataUrlRx.Match(tag);
                    if (!d.Success) continue;
                    string v = WebUtility.HtmlDecode(d.Groups[1].Value).Trim();
                    if (v.Length < 8 || v.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !v.Contains("download", StringComparison.OrdinalIgnoreCase)) continue;
                    int end = Math.Min(html.Length, t.Index + t.Length + 160);
                    string ctx = (tag + " " + html.Substring(t.Index + t.Length, end - t.Index - t.Length)).ToLowerInvariant();
                    int sc = 0;
                    if (Regex.IsMatch(ctx, @"4k|2160|uhd")) sc = 40;
                    else if (Regex.IsMatch(ctx, @"2k|1440|qhd")) sc = 30;
                    else if (Regex.IsMatch(ctx, @"1080|full ?hd")) sc = 20;
                    else if (Regex.IsMatch(ctx, @"720|480|360|low|mobile")) sc = -10;
                    if (tag.Contains("moe-download", StringComparison.OrdinalIgnoreCase)) sc += 1;
                    if (sc > bestScore) { bestScore = sc; token = v; }
                }
                string? preview = null;
                foreach (Match m in PreviewRx.Matches(html.Replace("\\/", "/")))
                {
                    string v = m.Groups[1].Value;
                    if (v.StartsWith("//")) v = "https:" + v; else if (v.StartsWith("/")) v = Host + v;
                    if (v.Contains("preview", StringComparison.OrdinalIgnoreCase)) { preview = v; break; }
                    preview ??= v;
                }
                var page = new LivePage(string.IsNullOrEmpty(token) ? null : token, preview);
                if (page.Token == null && page.PreviewUrl == null) Pages.TryRemove(u, out _);
                return page;
            }
            catch
            {
                Pages.TryRemove(u, out _);
                return new LivePage(null, null);
            }
        }));
    }

    /// <summary>Reads the pages of the first cards in the background (3 at a time) so that a click finds everything ready.</summary>
    public static async Task PrefetchAsync(IEnumerable<WallItem> items, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(3);
        var tasks = items.Take(12).Select(async it =>
        {
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try { await GetPageAsync(it.PageUrl).ConfigureAwait(false); } finally { gate.Release(); }
            }
            catch { }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>The direct download of a wallpaper, or null when its page has no download token.</summary>
    public static async Task<DownloadInfo?> ResolveAsync(WallItem it, CancellationToken ct)
    {
        LivePage page = await GetPageAsync(it.PageUrl).WaitAsync(ct).ConfigureAwait(false);
        if (page.Token == null) return null;
        string slug = it.PageUrl.TrimEnd('/');
        slug = Regex.Replace(slug.Substring(slug.LastIndexOf('/') + 1).ToLowerInvariant(), @"[^a-z0-9-]+", "-").Trim('-');
        return new DownloadInfo(DownloadBase + page.Token, Host + "/", (slug.Length == 0 ? "wallpaper" : slug) + ".mp4");
    }
}
