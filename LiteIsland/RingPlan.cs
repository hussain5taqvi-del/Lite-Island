using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteIsland;

/// <summary>
/// Decides what the ring shows: the front page (8 slices: your pinned formats, the best formats and tools for the
/// file) and the pages behind it with every other format Filee can make from it.
/// </summary>
internal static class RingPlan
{
    public const int PerPage = 8;

    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase)
        { "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "iso", "cab", "lzh", "zst" };

    private static readonly string[] Popular =
    {
        "pdf", "png", "jpg", "docx", "txt", "html", "csv", "xlsx", "json", "mp4", "mp3", "webp",
        "epub", "md", "odt", "rtf", "pptx", "xml", "gif", "wav", "m4a"
    };

    private static readonly HashSet<string> ImgFmt = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "webp", "heic", "heif", "bmp", "tiff", "tif", "avif", "svg", "ico", "psd", "eps", "jxl", "dds", "tga" };
    private static readonly HashSet<string> VidFmt = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "mov", "mkv", "avi", "webm", "m4v", "wmv", "flv", "mpg", "mpeg", "3gp", "ts", "mts", "ogv" };
    private static readonly HashSet<string> AudFmt = new(StringComparer.OrdinalIgnoreCase)
        { "mp3", "wav", "m4a", "aac", "flac", "ogg", "opus", "wma", "aiff", "aif", "amr", "mka" };

    public static bool IsArchive(string ext) => Archives.Contains(ext);

    /// <summary>The key under which pinned formats are kept: the extension when all files share one, else the kind.</summary>
    public static string PinKey(string[] files)
    {
        var exts = files.Select(FileeEngine.ExtOf).Distinct().ToList();
        return exts.Count == 1 && exts[0].Length > 0 ? exts[0] : "*" + (files.Length == 0 ? "" : FileKinds.Of(files[0]).ToString());
    }

    private static string IconFor(string t) =>
        t == "pdf" ? "pdf" : t == "gif" ? "gif" :
        ImgFmt.Contains(t) ? "image" : VidFmt.Contains(t) ? "video" : AudFmt.Contains(t) ? "note" :
        Archives.Contains(t) ? "zip" : "file";

    // Formats LiteIsland converts itself (fast, no helper program) when the kind of file allows it
    private static string? NativeFor(FileKind kind, string t)
    {
        switch (kind)
        {
            case FileKind.Photo:
                return t switch { "jpg" => "jpg", "png" => "png", "bmp" => "bmp", "tiff" => "tiff", "gif" => "gif1", "webp" => "webp", "pdf" => "pdf", _ => null };
            case FileKind.Video:
                return t switch { "mp4" => "mp4", "gif" => "gif", "mkv" => "mkv", "mov" => "mov", "avi" => "avi", "webm" => "webm",
                                  "mp3" => "mp3", "m4a" => "m4a", "wav" => "wav", "flac" => "flac", "ogg" => "ogg", "opus" => "opus", _ => null };
            case FileKind.Audio:
                return t switch { "mp3" => "mp3", "m4a" => "m4a", "wav" => "wav", "flac" => "flac", "ogg" => "ogg", "opus" => "opus",
                                  "aac" => "aac", "aiff" => "aiff", "wma" => "wma", _ => null };
            case FileKind.Doc:
                return t == "pdf" ? "dpdf" : null;
        }
        return null;
    }

    private static ActionDef Fmt(FileKind kind, string t)
    {
        string? n = NativeFor(kind, t);
        return new ActionDef(n ?? "f:" + t, t.ToUpperInvariant(), IconFor(t), t);
    }

    private static string[] Template(FileKind kind, string ext) => kind switch
    {
        FileKind.Photo => new[] { "jpg", "png", "webp", "pdf", "T:compress", "T:removebg", "T:ocr" },
        FileKind.Video => new[] { "mp4", "gif", "mp3", "webm", "T:vcompress", "T:mute", "mkv" },
        FileKind.Audio => new[] { "mp3", "wav", "m4a", "flac", "ogg", "opus", "aac" },
        FileKind.Pdf => new[] { "T:pcompress", "T:p2img", "T:pocr", "T:merge", "docx", "txt", "html" },
        FileKind.Doc => new[] { "pdf", "docx", "txt", "html", "odt", "rtf", "md" },
        _ => IsArchive(ext) ? new[] { "T:extract" } : Array.Empty<string>()
    };

    private static ActionDef? Tool(string id, int count) => id switch
    {
        "compress" => new ActionDef("compress", "Compress", "compress"),
        "removebg" => new ActionDef("removebg", "Remove BG", "cutout"),
        "ocr" => new ActionDef("ocr", "Copy text", "text"),
        "vcompress" => new ActionDef("vcompress", "Compress", "compress"),
        "mute" => new ActionDef("mute", "Mute", "mute"),
        "pcompress" => new ActionDef("pcompress", "Compress", "compress"),
        "p2img" => new ActionDef("p2img", "To images", "image"),
        "pocr" => new ActionDef("pocr", "Copy text", "text"),
        "merge" => count > 1 ? new ActionDef("merge", "Merge", "merge") : null,
        "extract" => new ActionDef("extract", "Extract", "folder"),
        "rot90" => new ActionDef("rot90", "Rotate", "image"),
        "flip" => new ActionDef("flip", "Mirror", "image"),
        "half" => new ActionDef("half", "Half size", "compress"),
        "v720" => new ActionDef("v720", "Small 720p", "compress"),
        "thumb" => new ActionDef("thumb", "Frame as JPG", "jpg"),
        "audio" => new ActionDef("audio", "Audio only", "audio"),
        _ => null
    };

    private static readonly ActionDef ZipDef = new("zip", "Zip", "zip");
    private static readonly ActionDef PathDef = new("copypath", "Copy path", "copy");

    /// <summary>
    /// Pages of the ring. Returns null when the formats of these files are not known yet: the caller shows the
    /// quick static ring instead and the look-up (a one-off, saved on disk) runs in the background.
    /// </summary>
    public static List<List<ActionDef>>? Build(string[] files, IList<string> pins)
    {
        if (!FileeEngine.Available) return null;

        FileKind kind = files.Length == 0 ? FileKind.Other : FileKinds.Of(files[0]);
        if (files.Any(f => FileKinds.Of(f) != kind)) kind = FileKind.Other;

        var exts = files.Select(FileeEngine.ExtOf).Distinct().ToList();
        HashSet<string>? targets = null;
        foreach (string e in exts)
        {
            string[]? t = FileeEngine.Cached(e);
            if (t == null) return null;
            targets = targets == null ? new HashSet<string>(t, StringComparer.OrdinalIgnoreCase) : new HashSet<string>(targets.Intersect(t), StringComparer.OrdinalIgnoreCase);
        }
        targets ??= new HashSet<string>();
        string ext = exts.Count == 1 ? exts[0] : "";

        // order of the targets: Filee's own order
        var ordered = new List<string>();
        if (exts.Count > 0)
        {
            foreach (string t in FileeEngine.Cached(exts[0])!)
                if (targets.Contains(t)) ordered.Add(t);
        }
        bool Same(string t) => exts.Contains(t, StringComparer.OrdinalIgnoreCase) || (t == "jpg" && exts.Contains("jpeg")) || (t == "jpeg" && exts.Contains("jpg"));
        bool Avail(string t) => !Same(t) && (targets.Contains(t) || NativeFor(kind, t) != null);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var front = new List<ActionDef>();
        var overflow = new List<ActionDef>();

        void Add(ActionDef? a, bool toFront)
        {
            if (a == null) return;
            string key = a.Fmt ?? a.Id;
            if (!used.Add(key)) return;
            if (toFront && front.Count < PerPage - 1) front.Add(a); else overflow.Add(a);
        }

        foreach (string p in pins) if (Avail(p)) Add(Fmt(kind, p), true);
        foreach (string tok in Template(kind, ext))
        {
            if (tok.StartsWith("T:", StringComparison.Ordinal)) Add(Tool(tok.Substring(2), files.Length), true);
            else if (Avail(tok)) Add(Fmt(kind, tok), true);
        }
        foreach (string t in Popular) if (front.Count < PerPage - 1 && Avail(t) && ordered.Contains(t)) Add(Fmt(kind, t), true);
        foreach (string t in ordered) if (front.Count < PerPage - 1 && Avail(t)) Add(Fmt(kind, t), true);

        front.Add(ZipDef);

        // the pages behind
        var rest = new List<ActionDef>(overflow);
        foreach (string t in ordered) if (Avail(t)) Add2(rest, Fmt(kind, t), used);
        if (kind == FileKind.Photo) foreach (string id in new[] { "rot90", "flip", "half" }) Add2(rest, Tool(id, 1), used);
        if (kind == FileKind.Video) foreach (string id in new[] { "v720", "thumb", "audio" }) Add2(rest, Tool(id, 1), used);
        rest.Add(PathDef);

        var pages = new List<List<ActionDef>> { front };
        for (int i = 0; i < rest.Count; i += PerPage) pages.Add(rest.Skip(i).Take(PerPage).ToList());
        return pages;
    }

    private static void Add2(List<ActionDef> list, ActionDef? a, HashSet<string> used)
    {
        if (a == null) return;
        if (!used.Add(a.Fmt ?? a.Id)) return;
        list.Add(a);
    }
}
