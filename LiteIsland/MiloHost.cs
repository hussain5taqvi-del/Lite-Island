using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace LiteIsland;

/// <summary>
/// Starts and talks to the Milo helper process (LiteIsland.exe --milo ...). The helper only runs while Milo is needed:
/// always when the wake word is on, otherwise from the first "talk" until the conversation ends.
/// </summary>
internal sealed class MiloHost
{
    private Process? _p;
    private readonly object _lk = new();
    private int _gen;

    public string State { get; private set; } = "off";
    public string LastHeard { get; private set; } = "";
    public string LastSaid { get; private set; } = "";
    public string LastTool { get; private set; } = "";
    public string LastError { get; private set; } = "";

    /// <summary>Raised on a background thread. kind: S, H, A, T, R, E.</summary>
    public event Action<string, string>? Event;

    public bool Running { get { lock (_lk) { return _p is { HasExited: false }; } } }

    public void Start(AppSettings s)
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
                foreach (string p in pre) psi.ArgumentList.Add(p);
                psi.ArgumentList.Add("--milo");
                psi.ArgumentList.Add(string.IsNullOrWhiteSpace(s.MiloName) ? "Milo" : s.MiloName.Trim());
                psi.ArgumentList.Add(s.MiloModel ?? "");
                psi.ArgumentList.Add(s.MiloVoice ?? "Puck");
                psi.ArgumentList.Add(s.MiloWake ? "1" : "0");
                psi.ArgumentList.Add(s.MiloHeadphones ? "1" : "0");
                psi.ArgumentList.Add(s.MiloGreet ? "1" : "0");
                var proc = Process.Start(psi);
                if (proc == null) return;
                _p = proc;
                int gen = ++_gen;
                proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                new Thread(() => ReadLoop(proc, gen)) { IsBackground = true, Name = "MiloRead" }.Start();
            }
            catch (Exception ex)
            {
                MiloLog.W("host start failed: " + ex.Message);
                LastError = "Could not start Milo: " + ex.Message;
                Event?.Invoke("E", LastError);
            }
        }
    }

    private void ReadLoop(Process p, int gen)
    {
        try
        {
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                int bar = line.IndexOf('|');
                if (bar < 1) continue;
                string kind = line.Substring(0, bar), val = line.Substring(bar + 1);
                switch (kind)
                {
                    case "S": State = val; break;
                    case "H": LastHeard = val; break;
                    case "A": LastSaid = val; break;
                    case "T": LastTool = val; break;
                    case "E": LastError = val; break;
                }
                Event?.Invoke(kind, val);
            }
        }
        catch { }
        lock (_lk) { if (gen == _gen) { _p = null; } }
        if (gen == _gen) { State = "off"; Event?.Invoke("S", "off"); }
    }

    public void Send(string line)
    {
        lock (_lk)
        {
            try
            {
                if (_p is { HasExited: false }) { _p.StandardInput.WriteLine(line); _p.StandardInput.Flush(); }
            }
            catch (Exception ex) { MiloLog.W("host send: " + ex.Message); }
        }
    }

    public void Quit()
    {
        Process? p;
        lock (_lk) { p = _p; }
        if (p == null) return;
        try { Send("QUIT"); if (!p.WaitForExit(1500)) p.Kill(); } catch { }
    }
}
