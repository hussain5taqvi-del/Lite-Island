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
/// Live wallpapers (videos) from MoeWalls and DesktopHut: reads their public listing pages, finds the cards (name, picture, page)
/// and, on a click, the direct video link of the wallpaper's page. When a site hides the link behind a button/script,
/// nothing is guessed: the page opens in the browser (see "Use latest download" in the Wallpapers window).
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

    public static async Task<List<WallItem>> BrowseAsync(WallSource src, string query, int page, CancellationToken ct)
    {
        string q = Uri.EscapeDataString((query ?? "").Trim());
        int p = Math.Max(1, page);
        string host, url;
        if (src == WallSource.MoeWalls)
        {
            host = "https://moewalls.com";
            url = q.Length == 0 ? (p <= 1 ? host + "/" : host + "/page/" + p + "/")
                                : (p <= 1 ? host + "/?s=" + q : host + "/page/" + p + "/?s=" + q);
        }
        else
        {
            host = "https://www.desktophut.com";
            url = q.Length == 0 ? (p <= 1 ? host + "/" : host + "/?page=" + p)
                                : host + "/search/" + q + (p <= 1 ? "" : "?page=" + p);
        }
        string html = await WallpaperService.GetHtmlAsync(url, ct).ConfigureAwait(false);
        return Parse(src, host, html);
    }

    private static readonly Regex AnchorRx = new(@"<a\b[^>]*?href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex ImgRx = new(@"(?:data-src|data-lazy-src|data-original|src)\s*=\s*[""']([^""']+\.(?:jpe?g|png|webp)[^""']*)[""']", RegexOptions.IgnoreCase);

    private static List<WallItem> Parse(WallSource src, string host, string html)
    {
        var map = new Dictionary<string, WallItem>();
        var order = new List<string>();
        foreach (Match m in AnchorRx.Matches(html))
        {
            string href = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            string abs = href.StartsWith("//") ? "https:" + href : href.StartsWith("/") ? host + href : href;
            if (!IsDetail(src, abs)) continue;
            string key = abs.Split('?')[0].TrimEnd('/');
            if (!map.TryGetValue(key, out WallItem? item))
            {
                item = new WallItem { Source = src, Title = Humanize(key), PageUrl = key + (src == WallSource.MoeWalls ? "/" : "") };
                map[key] = item;
                order.Add(key);
            }
            if (item.ThumbUrl == null)
            {
                foreach (Match im in ImgRx.Matches(m.Groups[2].Value))
                {
                    string u = WebUtility.HtmlDecode(im.Groups[1].Value);
                    if (Regex.IsMatch(u, "logo|avatar|gravatar|icon|default_share|sprite", RegexOptions.IgnoreCase)) continue;
                    item.ThumbUrl = u.StartsWith("//") ? "https:" + u : u.StartsWith("/") ? host + u : u;
                    break;
                }
            }
        }
        return order.Select(k => map[k]).ToList();
    }

    private static bool IsDetail(WallSource src, string abs)
    {
        if (!Uri.TryCreate(abs, UriKind.Absolute, out Uri? u)) return false;
        string[] seg = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (src == WallSource.MoeWalls)
            return u.Host.EndsWith("moewalls.com") && seg.Length == 2 && seg[1].EndsWith("live-wallpaper", StringComparison.OrdinalIgnoreCase);
        return u.Host.EndsWith("desktophut.com") && seg.Length == 1 && seg[0].Contains("live-wallpaper", StringComparison.OrdinalIgnoreCase);
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

    // ---------- the real file ----------

    private static readonly Regex AbsVideoRx = new(@"(?:https?:)?//[^""'\s<>()\\]+?\.(?:mp4|webm|mkv|mov)(?:\?[^""'\s<>()\\]*)?", RegexOptions.IgnoreCase);
    private static readonly Regex RelVideoRx = new(@"[""'](/[^""'\s<>()\\]+?\.(?:mp4|webm|mkv|mov))[""']", RegexOptions.IgnoreCase);
    private static readonly Regex DlRx = new(@"(?:https?:)?(?://[^""'\s<>()\\]+)?/[^""'\s<>()\\]*download[^""'\s<>()\\]*[?&](?:video|file|id|v)=[^""'\s<>()\\]+", RegexOptions.IgnoreCase);
    private static readonly Regex BadRx = new("preview|thumb|trailer|sample|logo|banner", RegexOptions.IgnoreCase);

    /// <summary>The direct video link of a wallpaper page, or null when the site does not show one.</summary>
    public static async Task<DownloadInfo?> ResolveAsync(WallItem it, CancellationToken ct)
    {
        string html = (await WallpaperService.GetHtmlAsync(it.PageUrl, ct).ConfigureAwait(false)).Replace("\\/", "/");
        Uri page = new(it.PageUrl);
        string origin = page.Scheme + "://" + page.Host;
        var cands = new List<string>();
        foreach (Match m in AbsVideoRx.Matches(html)) cands.Add(m.Value);
        foreach (Match m in RelVideoRx.Matches(html)) cands.Add(origin + m.Groups[1].Value);
        foreach (Match m in DlRx.Matches(html)) cands.Add(m.Value.StartsWith("/") ? origin + m.Value : m.Value);

        foreach (string raw in cands)
        {
            string u = WebUtility.HtmlDecode(raw);
            if (u.StartsWith("//")) u = "https:" + u;
            if (BadRx.IsMatch(u)) continue;
            if (!Uri.TryCreate(u, UriKind.Absolute, out Uri? uri)) continue;
            string name = Path.GetFileName(uri.AbsolutePath);
            Match vq = Regex.Match(uri.Query, @"(?:video|file)=([^&]+)");
            if (vq.Success) name = Uri.UnescapeDataString(vq.Groups[1].Value);
            name = Regex.Replace(name, @"[^A-Za-z0-9._-]+", "-").Trim('-');
            if (!IsVideo(name)) name = (name.Length > 0 && !name.Contains('.') ? name : Slug(it.PageUrl)) + ".mp4";
            return new DownloadInfo(u, it.PageUrl, name);
        }
        return null;
    }

    private static string Slug(string pageUrl)
    {
        string s = pageUrl.TrimEnd('/');
        s = s.Substring(s.LastIndexOf('/') + 1);
        return Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9-]+", "-").Trim('-');
    }
}
