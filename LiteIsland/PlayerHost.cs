using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace LiteIsland;

/// <summary>Starts and talks to the player helper (LiteIsland.exe --player). It only runs while a playlist is open.</summary>
internal sealed class PlayerHost
{
    private Process? _p;
    private readonly object _lk = new();

    /// <summary>Raised on a background thread. kind: R ready, S state, T position|duration, E ended, N next, V previous, F failed.</summary>
    public event Action<string, string>? Event;

    public bool Running { get { lock (_lk) { return _p is { HasExited: false }; } } }

    public void Start()
    {
        lock (_lk)
        {
            if (_p is { HasExited: false }) return;
            try
            {
                var (file, pre) = Proc.SelfCommand();
                var psi = new ProcessStartInfo(file)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false)
                };
                foreach (string a in pre) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add("--player");
                var proc = Process.Start(psi);
                if (proc == null) return;
                _p = proc;
                new Thread(() => ReadLoop(proc)) { IsBackground = true, Name = "PlayerRead" }.Start();
            }
            catch (Exception ex) { Event?.Invoke("F", "Could not start the player: " + ex.Message); }
        }
    }

    private void ReadLoop(Process p)
    {
        try
        {
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                int bar = line.IndexOf('|');
                if (bar < 1) continue;
                Event?.Invoke(line.Substring(0, bar), line.Substring(bar + 1));
            }
        }
        catch { }
    }

    public void Send(string cmd)
    {
        lock (_lk)
        {
            try
            {
                if (_p is { HasExited: false }) { _p.StandardInput.WriteLine(cmd); _p.StandardInput.Flush(); }
            }
            catch { }
        }
    }

    public void Load(string path, string title, string artist, string thumb)
    {
        static string C(string s) => (s ?? "").Replace("|", " ").Replace('\r', ' ').Replace('\n', ' ');
        Start();
        Send("LOAD|" + C(path) + "|" + C(title) + "|" + C(artist) + "|" + C(thumb));
    }

    public void Stop()
    {
        lock (_lk)
        {
            try
            {
                if (_p is { HasExited: false })
                {
                    _p.StandardInput.WriteLine("QUIT");
                    _p.StandardInput.Flush();
                    var p = _p;
                    ThreadPool.QueueUserWorkItem(_ => { try { if (!p.WaitForExit(1500)) p.Kill(true); } catch { } });
                }
            }
            catch { }
            _p = null;
        }
    }
}
