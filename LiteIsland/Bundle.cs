using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace LiteIsland;

/// <summary>
/// LiteIsland is ONE file. The built-in tools (yt-dlp, deno, ffmpeg, the Filee converter ...) travel inside it as a zip
/// that is glued to the end of LiteIsland.exe. The first time the app runs, the zip is unpacked once into
/// %LocalAppData%\LiteIsland\bundle (nothing is downloaded, nothing is left next to the exe).
/// Updates ship a slim exe without the zip; they keep using the tools that are already unpacked.
/// </summary>
internal static class Bundle
{
    // last 24 bytes of the exe: [zip offset: 8][zip length: 8]["LIENGv01"]
    private const string Magic = "LIENGv01";
    private const int FooterSize = 24;

    private static readonly string Parent = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteIsland", "bundle");

    private static readonly object Gate = new();
    private static Task? _task;
    private static string _root = Resolve();

    /// <summary>The folder that holds "engines" and "filee". Changes once, when the first unpacking is done.</summary>
    public static string Root => _root;

    public static Task WaitAsync() => _task ?? Task.CompletedTask;

    private static string ExeDir => AppContext.BaseDirectory;

    private static string Resolve()
    {
        try
        {
            // portable folder / developer build: tools lie next to the exe
            if (Directory.Exists(Path.Combine(ExeDir, "engines")) || Directory.Exists(Path.Combine(ExeDir, "filee"))) return ExeDir;
            if (Directory.Exists(Parent))
            {
                var best = new DirectoryInfo(Parent).GetDirectories()
                    .Where(d => File.Exists(Path.Combine(d.FullName, ".ok")))
                    .OrderByDescending(d => d.CreationTimeUtc).FirstOrDefault();
                if (best != null) return best.FullName;
            }
        }
        catch { }
        return ExeDir;
    }

    /// <summary>Starts the one-time unpacking in the background (does nothing when it is not needed).</summary>
    public static void Start(Action<bool>? done = null)
    {
        lock (Gate)
        {
            if (_task != null) return;
            _task = Task.Run(() =>
            {
                bool unpacked = false;
                try { unpacked = Extract(); } catch { }
                try { done?.Invoke(unpacked); } catch { }
            });
        }
    }

    /// <summary>Synchronous variant for the --selftest of the build. Returns true when the tools are in place.</summary>
    public static bool EnsureNow()
    {
        try { Extract(); } catch { return false; }
        string r = Root;
        return File.Exists(Path.Combine(r, "engines", "yt-dlp.exe")) && File.Exists(Path.Combine(r, "filee", "filee-cli.exe"));
    }

    private static bool Extract()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
        if (Directory.Exists(Path.Combine(ExeDir, "engines"))) return false;

        using var fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        if (fs.Length < FooterSize) return false;
        var f = new byte[FooterSize];
        fs.Seek(-FooterSize, SeekOrigin.End);
        int got = 0;
        while (got < FooterSize) { int n = fs.Read(f, got, FooterSize - got); if (n <= 0) return false; got += n; }
        if (System.Text.Encoding.ASCII.GetString(f, 16, 8) != Magic) return false;     // slim exe (an update): keep the unpacked tools
        long off = BitConverter.ToInt64(f, 0), len = BitConverter.ToInt64(f, 8);
        if (off <= 0 || len <= 0 || off + len > fs.Length - FooterSize) return false;

        string dir = Path.Combine(Parent, "b" + len.ToString("x"));
        if (File.Exists(Path.Combine(dir, ".ok"))) { _root = dir; return false; }

        Directory.CreateDirectory(Parent);
        string tmp = dir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        using (var sub = new SubStream(fs, off, len))
        using (var zip = new ZipArchive(sub, ZipArchiveMode.Read))
            zip.ExtractToDirectory(tmp, true);

        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.Move(tmp, dir);
        File.WriteAllText(Path.Combine(dir, ".ok"), "1");
        _root = dir;

        // older unpacked versions are not needed any more
        try
        {
            foreach (var d in new DirectoryInfo(Parent).GetDirectories())
                if (!d.FullName.Equals(dir, StringComparison.OrdinalIgnoreCase)) { try { d.Delete(true); } catch { } }
        }
        catch { }
        return true;
    }

    /// <summary>A read-only window into a part of a bigger stream (the zip inside the exe).</summary>
    private sealed class SubStream : Stream
    {
        private readonly Stream _b;
        private readonly long _off, _len;
        private long _pos;

        public SubStream(Stream b, long off, long len) { _b = b; _off = off; _len = len; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _len;
        public override long Position { get => _pos; set => _pos = Math.Clamp(value, 0, _len); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long rem = _len - _pos;
            if (rem <= 0) return 0;
            if (buffer.Length > rem) buffer = buffer.Slice(0, (int)rem);
            _b.Position = _off + _pos;
            int n = _b.Read(buffer);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long p = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _pos + offset : _len + offset;
            Position = p;
            return _pos;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
