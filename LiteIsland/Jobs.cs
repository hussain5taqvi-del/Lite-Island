using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace LiteIsland;

internal enum JobState { Running, Paused, Done, Failed, Canceled }

/// <summary>One conversion or one download (shown on the Downloads page).</summary>
internal sealed class Job
{
    public int Id;
    public string Title = "";
    public string Kind = "";          // "convert" or "download"
    public string Icon = "file";
    public JobState State = JobState.Running;
    public double Progress = -1;      // 0..1, -1 = unknown
    public string Detail = "";
    public string? OutPath;
    public long InBytes, OutBytes;
    public long StartTick = Environment.TickCount64;
    public double Seconds;
    public DateTime Finished;
    public CancellationTokenSource Cts = new();
    public Func<Job, Task>? Restart;  // used by "resume"
    public ImageSource? Thumb;
    public long LastUi;               // throttles screen updates
    public string? Cache;             // hidden cache folder of a download (kept while paused)
}

internal sealed record ActionDef(string Id, string Label, string Icon, string? Fmt = null, ImageSource? Img = null);

internal enum FileKind { Photo, Video, Pdf, Audio, Doc, Other }

internal static class FileKinds
{
    private static readonly HashSet<string> Photos = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".jfif", ".ico" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".flv", ".mpg", ".mpeg", ".3gp", ".ts", ".mts" };
    private static readonly HashSet<string> Audios = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".aiff", ".aif" };
    private static readonly HashSet<string> Docs = new(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".docx", ".rtf", ".txt", ".odt", ".md", ".log", ".csv" };

    public static FileKind Of(string path)
    {
        if (Directory.Exists(path)) return FileKind.Other;
        string e = Path.GetExtension(path);
        if (Photos.Contains(e)) return FileKind.Photo;
        if (Videos.Contains(e)) return FileKind.Video;
        if (Audios.Contains(e)) return FileKind.Audio;
        if (e.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return FileKind.Pdf;
        if (Docs.Contains(e)) return FileKind.Doc;
        return FileKind.Other;
    }

    public static string Label(string path)
    {
        if (Directory.Exists(path)) return "DIR";
        string e = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        return e.Length == 0 ? "FILE" : (e.Length > 5 ? e.Substring(0, 5) : e);
    }

    /// <summary>The actions in the ring, in clockwise order starting at 12 o'clock.</summary>
    public static List<ActionDef> ActionsFor(string[] files, bool more = false)
    {
        var l = new List<ActionDef>();
        FileKind kind = files.Length == 0 ? FileKind.Other : Of(files[0]);
        // The kind is only used when every selected item is of that kind
        if (files.Any(f => Of(f) != kind)) kind = FileKind.Other;

        if (more)
        {
            switch (kind)
            {
                case FileKind.Photo:
                    l.Add(new ActionDef("bmp", "To BMP", "image"));
                    l.Add(new ActionDef("tiff", "To TIFF", "image"));
                    l.Add(new ActionDef("gif1", "To GIF", "gif"));
                    l.Add(new ActionDef("webp", "To WebP", "image"));
                    l.Add(new ActionDef("half", "Half size", "compress"));
                    l.Add(new ActionDef("rot90", "Rotate", "image"));
                    l.Add(new ActionDef("flip", "Mirror", "image"));
                    break;
                case FileKind.Video:
                    l.Add(new ActionDef("mkv", "To MKV", "video"));
                    l.Add(new ActionDef("mov", "To MOV", "video"));
                    l.Add(new ActionDef("avi", "To AVI", "video"));
                    l.Add(new ActionDef("webm", "To WebM", "video"));
                    l.Add(new ActionDef("v720", "Small 720p", "compress"));
                    l.Add(new ActionDef("thumb", "Frame as JPG", "jpg"));
                    break;
                case FileKind.Audio:
                    l.Add(new ActionDef("flac", "To FLAC", "audio"));
                    l.Add(new ActionDef("ogg", "To OGG", "note"));
                    l.Add(new ActionDef("opus", "To OPUS", "note"));
                    l.Add(new ActionDef("aac", "To AAC", "note"));
                    l.Add(new ActionDef("aiff", "To AIFF", "audio"));
                    l.Add(new ActionDef("wma", "To WMA", "note"));
                    break;
            }
            l.Add(new ActionDef("back", "Back", "back"));
            return l;
        }

        switch (kind)
        {
            case FileKind.Photo:
                l.Add(new ActionDef("jpg", "To JPG", "jpg"));
                l.Add(new ActionDef("png", "To PNG", "png"));
                l.Add(new ActionDef("compress", "Compress", "compress"));
                l.Add(new ActionDef("removebg", "Remove BG", "cutout"));
                l.Add(new ActionDef("ocr", "Copy text", "text"));
                l.Add(new ActionDef("pdf", "To PDF", "pdf"));
                break;
            case FileKind.Video:
                l.Add(new ActionDef("vcompress", "Compress", "compress"));
                l.Add(new ActionDef("mp4", "To MP4", "video"));
                l.Add(new ActionDef("gif", "To GIF", "gif"));
                l.Add(new ActionDef("audio", "Audio only", "audio"));
                l.Add(new ActionDef("mute", "Mute", "mute"));
                break;
            case FileKind.Pdf:
                l.Add(new ActionDef("pcompress", "Compress", "compress"));
                l.Add(new ActionDef("p2img", "To images", "image"));
                l.Add(new ActionDef("pocr", "Copy text", "text"));
                if (files.Length > 1) l.Add(new ActionDef("merge", "Merge", "merge"));
                break;
            case FileKind.Audio:
                l.Add(new ActionDef("silence", "Remove silence", "audio"));
                l.Add(new ActionDef("m4a", "To M4A", "note"));
                l.Add(new ActionDef("wav", "To WAV", "audio"));
                l.Add(new ActionDef("mp3", "To MP3", "note"));
                break;
            case FileKind.Doc:
                l.Add(new ActionDef("dpdf", "To PDF", "pdf"));
                break;
        }

        if (kind == FileKind.Photo || kind == FileKind.Video || kind == FileKind.Audio)
            l.Add(new ActionDef("more", "More", "chevron"));
        l.Add(new ActionDef("zip", "Zip", "zip"));
        l.Add(new ActionDef("copypath", "Copy path", "copy"));
        return l;
    }
}
