using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace LiteIsland;

/// <summary>
/// Optional timing log (Settings → General → "Record animation timing"). While on, every animation (island open / close,
/// page change, activity bar, ring) is measured frame by frame and one line is written to perf.log. It costs nothing
/// when off and only runs for about a second after each animation when on. No content of any file or text is ever recorded.
/// </summary>
internal static class PerfLog
{
    public static bool Enabled;

    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    private static readonly List<double> Gaps = new();
    private static bool _active, _header;
    private static string _label = "";
    private static double _start, _end, _last;
    private static TimeSpan _cpuStart;
    private static DispatcherTimer? _timer;

    public static string FilePath => Path.Combine(
        Path.GetDirectoryName(Tools.Dir.TrimEnd('\\'))!, "perf.log");

    public static void Mark(string name, int ms = 1000)
    {
        if (!Enabled) return;
        try
        {
            double now = Sw.Elapsed.TotalMilliseconds;
            if (_active)
            {
                if (!_label.Contains(name, StringComparison.Ordinal)) _label += " + " + name;
                _end = Math.Max(_end, now + ms);
                return;
            }
            WriteHeader();
            _active = true;
            _label = name;
            _start = now;
            _end = now + ms;
            _last = -1;
            Gaps.Clear();
            _cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
            CompositionTarget.Rendering += OnFrame;

            _timer ??= new DispatcherTimer();
            _timer.Tick -= OnTimer;
            _timer.Tick += OnTimer;
            _timer.Interval = TimeSpan.FromMilliseconds(ms + 150);
            _timer.Start();
        }
        catch { _active = false; }
    }

    private static void OnTimer(object? s, EventArgs e)
    {
        if (Sw.Elapsed.TotalMilliseconds < _end) { _timer!.Interval = TimeSpan.FromMilliseconds(Math.Max(50, _end - Sw.Elapsed.TotalMilliseconds + 100)); return; }
        Finish();
    }

    private static void OnFrame(object? s, EventArgs e)
    {
        double now = Sw.Elapsed.TotalMilliseconds;
        if (_last >= 0) Gaps.Add(now - _last);
        _last = now;
        if (now >= _end) Finish();
    }

    private static void Finish()
    {
        if (!_active) return;
        _active = false;
        CompositionTarget.Rendering -= OnFrame;
        _timer?.Stop();
        try
        {
            double wall = Math.Max(1, Sw.Elapsed.TotalMilliseconds - _start);
            double cpu = (Process.GetCurrentProcess().TotalProcessorTime - _cpuStart).TotalMilliseconds / wall / Environment.ProcessorCount * 100;
            string line;
            if (Gaps.Count == 0)
                line = $"{DateTime.Now:HH:mm:ss} {_label,-34} no frames drawn";
            else
            {
                var sorted = Gaps.OrderBy(x => x).ToList();
                double p95 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))];
                line = $"{DateTime.Now:HH:mm:ss} {_label,-34} frames {Gaps.Count,3} | avg {Gaps.Average():0.0} ms | p95 {p95:0.0} | max {sorted[^1]:0.0}" +
                       $" | >20ms {Gaps.Count(x => x > 20)} | >33ms {Gaps.Count(x => x > 33)} | >50ms {Gaps.Count(x => x > 50)}" +
                       $" | cpu {cpu:0}% | ram {MemoryGuard.WorkingSetMb():0} MB";
            }
            Append(line);
        }
        catch { }
    }

    private static void WriteHeader()
    {
        if (_header) return;
        _header = true;
        int tier = RenderCapability.Tier >> 16;
        string text =
            Environment.NewLine + "=== LiteIsland " + (typeof(PerfLog).Assembly.GetName().Version?.ToString(3) ?? "?") + " perf log " + DateTime.Now + " ===" + Environment.NewLine +
            $"render tier {tier} (2 = full GPU, 1 = partly, 0 = software: slow) | cores {Environment.ProcessorCount} | {Environment.OSVersion.VersionString}" + Environment.NewLine +
            $"RenderMode {RenderOptions.ProcessRenderMode} | max texture {RenderCapability.MaxHardwareTextureSize.Width}px | video RAM-friendly: {(tier >= 2 ? "yes" : "no")}";
        Append(text);
    }

    private static void Append(string line)
    {
        try
        {
            string p = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            if (File.Exists(p) && new FileInfo(p).Length > 300_000) File.Delete(p);
            File.AppendAllText(p, line + Environment.NewLine);
        }
        catch { }
    }
}
