using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiteIsland;

/// <summary>Runs other programs (ffmpeg, yt-dlp, and the helper mode of this same exe) without a window.</summary>
internal static class Proc
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Runs a program and returns its exit code. Cancelling kills it (and its children).</summary>
    public static async Task<int> RunAsync(string file, IEnumerable<string> args,
        Action<string>? onOut, Action<string>? onErr, CancellationToken ct, Action<Process>? onStart = null,
        ProcessPriorityClass priority = ProcessPriorityClass.BelowNormal)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onOut?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onErr?.Invoke(e.Data); };

        ct.ThrowIfCancellationRequested();
        p.Start();
        try { p.PriorityClass = priority; } catch { }
        onStart?.Invoke(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
        });

        await p.WaitForExitAsync().ConfigureAwait(false);
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        return p.ExitCode;
    }

    /// <summary>The program that starts this exe again in helper mode (works for the single-file exe and for "dotnet run").</summary>
    private static (string File, string[] Pre) SelfCommand()
    {
        string exe = Environment.ProcessPath ?? "";
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string dll = Path.Combine(AppContext.BaseDirectory, "LiteIsland.dll");
            return (exe, new[] { dll });
        }
        return (exe, Array.Empty<string>());
    }

    /// <summary>
    /// Runs one job in a separate helper process (this same exe with --worker), so the heavy work
    /// (decoding big photos, OCR, background removal, PDF pages) never uses the island's RAM.
    /// Returns the text the helper reports (an output path, or the recognized text).
    /// </summary>
    public static async Task<string> RunWorkerAsync(string op, IEnumerable<string> args, Action<double>? progress, CancellationToken ct)
    {
        string res = Path.Combine(Path.GetTempPath(), "li_" + Guid.NewGuid().ToString("N") + ".res");
        var (file, pre) = SelfCommand();
        var all = new List<string>(pre) { "--worker", op, res };
        all.AddRange(args);

        int code = await RunAsync(file, all, line =>
        {
            if (line.StartsWith("P ", StringComparison.Ordinal) &&
                double.TryParse(line.AsSpan(2), NumberStyles.Float, Inv, out double d))
                progress?.Invoke(d);
        }, null, ct, null, ProcessPriorityClass.Normal).ConfigureAwait(false);

        string txt = "";
        try { if (File.Exists(res)) txt = File.ReadAllText(res, Encoding.UTF8); } catch { }
        try { File.Delete(res); } catch { }

        if (txt.StartsWith("OK\n", StringComparison.Ordinal)) return txt.Substring(3);
        if (txt.StartsWith("ERR\n", StringComparison.Ordinal)) throw new InvalidOperationException(txt.Substring(4).Trim());
        throw new InvalidOperationException("The helper stopped (code " + code.ToString(Inv) + ")");
    }
}
