using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LiteIsland;

/// <summary>
/// Updates from GitHub Releases. A release carries a small "LiteIsland-update.zip" (only the files that changed,
/// normally just LiteIsland.exe), so an update is a few tens of MB and never the whole 400 MB package.
/// A running exe can be renamed on Windows, so the new files are put in place while the island is running;
/// the new version starts at the next launch (or right away with "Restart now").
/// </summary>
internal static class Updater
{
    public const string Repo = "hussain5taqvi-del/Lite-Island";
    private static readonly HttpClient Http = Make();

    public sealed record Info(Version Version, string Tag, string UpdateUrl, string? ShaUrl, string PageUrl, string Notes);

    private static HttpClient Make()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("LiteIsland-Updater");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string InstallDir
    {
        get
        {
            string? p = Environment.ProcessPath;
            string? d = string.IsNullOrEmpty(p) ? null : Path.GetDirectoryName(p);
            return d ?? AppContext.BaseDirectory;
        }
    }

    /// <summary>Removes the files an earlier update left behind (the old exe was renamed, not deleted).</summary>
    public static void CleanOld()
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(InstallDir, "*.old", SearchOption.AllDirectories))
                try { File.Delete(f); } catch { }
            string tmp = Path.Combine(Path.GetTempPath(), "LiteIsland-update");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
        catch { }
    }

    public static bool CanWriteHere()
    {
        try
        {
            string probe = Path.Combine(InstallDir, ".write-test-" + Environment.ProcessId);
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>The newest release when it is newer than this version, otherwise null.</summary>
    public static async Task<Info?> CheckAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync("https://api.github.com/repos/" + Repo + "/releases/latest", ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? v) || v is null) return null;
        if (root.TryGetProperty("prerelease", out JsonElement pre) && pre.GetBoolean()) return null;
        if (v <= Current) return null;

        string? zip = null, sha = null;
        foreach (JsonElement a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            string url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (name.Equals("LiteIsland-update.zip", StringComparison.OrdinalIgnoreCase)) zip = url;
            else if (name.Equals("LiteIsland-update.zip.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
        }
        if (zip == null) return null;
        string page = root.TryGetProperty("html_url", out JsonElement h) ? h.GetString() ?? "" : "";
        string notes = root.TryGetProperty("body", out JsonElement b) ? b.GetString() ?? "" : "";
        return new Info(v, tag, zip, sha, page, notes);
    }

    private static bool IsGitHub(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttps &&
               (u.Host == "github.com" || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Downloads, checks and puts the new files in place. Returns null when done, otherwise the reason it failed.</summary>
    public static async Task<string?> InstallAsync(Info info, Action<double>? progress, CancellationToken ct)
    {
        try
        {
            if (!IsGitHub(info.UpdateUrl)) return "Unexpected download address";
            if (!CanWriteHere()) return "This folder is read-only";

            string work = Path.Combine(Path.GetTempPath(), "LiteIsland-update", info.Tag);
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            string zipPath = Path.Combine(work, "update.zip");

            using (var resp = await Http.GetAsync(info.UpdateUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zipPath);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Invoke((double)done / total);
                }
            }

            // the checksum from the release must match the file that was downloaded
            if (info.ShaUrl != null && IsGitHub(info.ShaUrl))
            {
                string expected = (await Http.GetStringAsync(info.ShaUrl, ct)).Trim().Split(' ', '\t', '\n')[0];
                string actual;
                await using (var fs = File.OpenRead(zipPath))
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return "The download is damaged (checksum)";
            }

            string unpack = Path.Combine(work, "files");
            ZipFile.ExtractToDirectory(zipPath, unpack);

            string root = InstallDir;
            var staged = new List<(string From, string To)>();
            foreach (string f in Directory.EnumerateFiles(unpack, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(unpack, f);
                if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return "Unexpected file in the update";
                staged.Add((f, Path.GetFullPath(Path.Combine(root, rel))));
            }
            if (staged.Count == 0) return "The update is empty";

            var moved = new List<(string To, string? Backup)>();
            try
            {
                foreach (var (from, to) in staged)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    string? backup = null;
                    if (File.Exists(to))
                    {
                        backup = to + ".old";
                        if (File.Exists(backup)) File.Delete(backup);
                        File.Move(to, backup); // works for the exe that is running right now
                    }
                    File.Move(from, to);
                    moved.Add((to, backup));
                }
            }
            catch (Exception ex)
            {
                // put everything back: never leave a half-updated install
                foreach (var (to, backup) in moved)
                {
                    try { if (File.Exists(to)) File.Delete(to); if (backup != null) File.Move(backup, to); } catch { }
                }
                return "Could not replace the files: " + ex.Message;
            }
            return null;
        }
        catch (OperationCanceledException) { return "Cancelled"; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Starts the new version and closes this one.</summary>
    public static void Restart()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe == null) return;
            // a tiny delay lets this copy release the single-instance lock first
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & start \"\" \"" + exe + "\"")
            { UseShellExecute = false, CreateNoWindow = true };
            System.Diagnostics.Process.Start(psi);
            System.Windows.Application.Current.Shutdown();
        }
        catch { }
    }
}
