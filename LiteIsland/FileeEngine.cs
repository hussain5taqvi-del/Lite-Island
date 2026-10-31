using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LiteIsland;

/// <summary>
/// The converter behind the ring: Filee's command line (MIT, 256 formats, works offline) that ships in the
/// "filee" folder next to LiteIsland.exe. It runs only while a conversion (or a first look-up) is going on.
/// </summary>
internal static class FileeEngine
{
    public static string Exe => Path.Combine(Tools.Base, "filee", "filee-cli.exe");
    public static bool Available => File.Exists(Exe);

    private static readonly object Gate = new();
    private static Dictionary<string, string[]>? _targets;     // extension -> target format ids (in Filee's own order)
    private static readonly SemaphoreSlim Look = new(1, 1);    // one look-up process at a time
    private static string CacheFile => Path.Combine(Path.GetDirectoryName(Tools.Dir.TrimEnd('\\'))!, "filee-targets.json");

    private static string Stamp() => Available ? File.GetLastWriteTimeUtc(Exe).Ticks.ToString() : "0";

    private sealed class Store
    {
        public string Stamp { get; set; } = "";
        public Dictionary<string, string[]> Targets { get; set; } = new();
    }

    private static Dictionary<string, string[]> Load()
    {
        lock (Gate)
        {
            if (_targets != null) return _targets;
            _targets = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(CacheFile))
                {
                    var s = JsonSerializer.Deserialize<Store>(File.ReadAllText(CacheFile));
                    if (s != null && s.Stamp == Stamp())
                        foreach (var kv in s.Targets) _targets[kv.Key] = kv.Value;
                }
            }
            catch { }
            return _targets;
        }
    }

    private static void Save()
    {
        try
        {
            Store s;
            lock (Gate) s = new Store { Stamp = Stamp(), Targets = new Dictionary<string, string[]>(Load()) };
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(s));
        }
        catch { }
    }

    public static string ExtOf(string path) => Path.GetExtension(path.TrimEnd('\\')).TrimStart('.').ToLowerInvariant();

    /// <summary>The targets of an extension if they are already known (instant, no process).</summary>
    public static string[]? Cached(string ext)
    {
        lock (Gate) return Load().TryGetValue(ext, out var t) ? t : null;
    }

    /// <summary>What an extension converts to. Asked once, then remembered on disk.</summary>
    public static async Task<string[]> TargetsAsync(string ext, CancellationToken ct = default)
    {
        await Bundle.WaitAsync().ConfigureAwait(false);
        if (!Available || ext.Length == 0) return Array.Empty<string>();
        var c = Cached(ext);
        if (c != null) return c;

        await Look.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            c = Cached(ext);
            if (c != null) return c;

            var lines = new List<string>();
            int code = await Proc.RunAsync(Exe, new[] { "formats", ext }, l => { lock (lines) lines.Add(l); }, null, ct, null,
                ProcessPriorityClass.BelowNormal).ConfigureAwait(false);

            var list = new List<string>();
            if (code == 0)
            {
                bool inNow = false;
                foreach (string raw in lines.ToArray())
                {
                    if (raw.Contains("converts to:", StringComparison.Ordinal)) { inNow = true; continue; }
                    if (!inNow) continue;
                    if (!raw.StartsWith("  ", StringComparison.Ordinal)) break;      // the optional-engine part starts a new paragraph
                    if (raw.Contains("(nothing", StringComparison.Ordinal)) break;
                    foreach (string t in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (t.Length > 0 && !list.Contains(t)) list.Add(t);
                }
            }
            else if (code == 2) { /* unknown format: nothing to offer */ }
            else return Array.Empty<string>(); // a real failure is not remembered

            var arr = list.ToArray();
            lock (Gate) Load()[ext] = arr;
            Save();
            return arr;
        }
        finally { Look.Release(); }
    }

    private static readonly string[] Common =
    {
        "jpg","jpeg","png","webp","heic","gif","bmp","tiff","avif","svg","pdf","docx","doc","txt","rtf","odt","md","html",
        "xlsx","xls","csv","pptx","epub","mp4","mov","mkv","avi","webm","mp3","wav","m4a","flac","ogg","zip","7z","rar","json","xml"
    };

    /// <summary>Once, in the background and gently, so the ring is already full the first time it opens.</summary>
    public static void Prewarm(IEnumerable<string>? extra = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Bundle.WaitAsync().ConfigureAwait(false);
                if (!Available) return;
                await Task.Delay(15000).ConfigureAwait(false);
                foreach (string e in Common.Concat(extra ?? Array.Empty<string>()))
                {
                    if (Cached(e) == null) await TargetsAsync(e).ConfigureAwait(false);
                    await Task.Delay(300).ConfigureAwait(false);
                }
            }
            catch { }
        });
    }

    /// <summary>Converts one file. Returns the new file (or folder when several files were made).</summary>
    public static async Task<string> ConvertAsync(string file, string target, CancellationToken ct, string? outDir = null)
    {
        await Bundle.WaitAsync().ConfigureAwait(false);
        if (!Available) throw new InvalidOperationException("The converter is still being set up (first start takes a minute). Try again shortly.");

        var args = new List<string> { "convert", file, "--to", target, "--json" };
        if (!string.IsNullOrEmpty(outDir)) { args.Add("-o"); args.Add(outDir); }

        var sb = new System.Text.StringBuilder();
        var err = new List<string>();
        int code = await Proc.RunAsync(Exe, args, l => { lock (sb) sb.AppendLine(l); }, l => { lock (err) err.Add(l); }, ct, null,
            ProcessPriorityClass.Normal).ConfigureAwait(false);

        string? error = null;
        var outs = new List<string>();
        try
        {
            string text;
            lock (sb) text = sb.ToString();
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("files", out var files))
                foreach (var f in files.EnumerateArray())
                {
                    if (f.TryGetProperty("outputs", out var o))
                        foreach (var p in o.EnumerateArray())
                            if (p.GetString() is { Length: > 0 } s) outs.Add(s);
                    if (f.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } es) error ??= es;
                }
        }
        catch
        {
            lock (err) error = err.LastOrDefault();
        }

        if (outs.Count == 0)
            throw new InvalidOperationException(error ?? (code == 0 ? "Nothing was made" : "Conversion failed"));
        return outs.Count == 1 ? outs[0] : (Path.GetDirectoryName(outs[0]) ?? outs[0]);
    }
}
