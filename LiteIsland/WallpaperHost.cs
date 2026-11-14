using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace LiteIsland;

/// <summary>Starts and stops the live wallpaper helper (LiteIsland.exe --wallpaper). Only runs while the wallpaper is on.</summary>
internal sealed class WallpaperHost
{
    private Process? _p;
    private string _key = "";
    private readonly object _lk = new();

    public string Status { get; private set; } = "";
    public event Action? StatusChanged;
    public bool Running { get { lock (_lk) { return _p is { HasExited: false }; } } }

    /// <summary>Starts (or restarts when the video/options changed) the helper.</summary>
    public void Apply(bool on, string video, bool pause)
    {
        lock (_lk)
        {
            string key = video + "|" + (pause ? "1" : "0");
            bool alive = _p is { HasExited: false };
            if (!on || string.IsNullOrWhiteSpace(video)) { StopLocked(); SetStatus(""); return; }
            if (alive && key == _key) return;
            StopLocked();
            _key = key;
            try
            {
                var (file, pre) = Proc.SelfCommand();
                var psi = new ProcessStartInfo(file)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false)
                };
                // a lean .NET runtime for this helper: no background GC thread, small first generation, no tiered profiling
                psi.Environment["DOTNET_gcConcurrent"] = "0";
                psi.Environment["DOTNET_GCgen0size"] = "0x200000";
                psi.Environment["DOTNET_GCConserveMemory"] = "9";
                psi.Environment["DOTNET_TieredPGO"] = "0";
                psi.Environment["DOTNET_TieredCompilation"] = "1";
                foreach (string a in pre) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add("--wallpaper");
                psi.ArgumentList.Add(video);
                psi.ArgumentList.Add(pause ? "1" : "0");
                psi.ArgumentList.Add(pause ? "1" : "0");
                var proc = Process.Start(psi);
                if (proc == null) return;
                _p = proc;
                SetStatus("Starting...");
                new Thread(() => ReadLoop(proc)) { IsBackground = true, Name = "WpRead" }.Start();
            }
            catch (Exception ex) { SetStatus("Could not start: " + ex.Message); }
        }
    }

    private void ReadLoop(Process p)
    {
        try
        {
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                if (line.Length < 3) continue;
                if (line[0] == 'S') SetStatus(line.Substring(2));
                else if (line[0] == 'E') SetStatus("Error: " + line.Substring(2));
            }
        }
        catch { }
    }

    private void SetStatus(string s)
    {
        Status = s;
        try { StatusChanged?.Invoke(); } catch { }
    }

    private void StopLocked()
    {
        try
        {
            if (_p is { HasExited: false })
            {
                try { _p.StandardInput.WriteLine("QUIT"); _p.StandardInput.Flush(); } catch { }
                if (!_p.WaitForExit(3000)) { try { _p.Kill(true); } catch { } }
            }
        }
        catch { }
        _p = null;
        _key = "";
    }

    public void Stop() { lock (_lk) { StopLocked(); } }
}
