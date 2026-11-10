using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LiteIsland;

/// <summary>
/// Everything Milo can do on the PC. Every tool returns a short plain sentence that Milo reads back to decide what to say.
/// Risky actions (shutdown, delete, closing apps) only run when the user has confirmed them out loud (confirmed = true).
/// </summary>
internal static class MiloTools
{
    public static Action? RemindersChanged;
    /// <summary>Raised when a background job (an install) ends. The worker shows it and tells the user.</summary>
    public static Action<string>? Notify;

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    // ---------- declarations ----------
    private static JsonObject P(string type, string desc, params string[]? values)
    {
        var o = new JsonObject { ["type"] = type, ["description"] = desc };
        if (values != null && values.Length > 0) o["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        return o;
    }

    private static JsonObject Fn(string name, string desc, JsonObject? props = null, params string[] required)
    {
        var o = new JsonObject { ["name"] = name, ["description"] = desc };
        if (props != null)
        {
            var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
            if (required.Length > 0) schema["required"] = new JsonArray(required.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            o["parameters"] = schema;
        }
        return o;
    }

    public static JsonArray Declarations(bool blocking)
    {
        var a = new JsonArray();
        a.Add(Fn("open_app", "Open an application on the PC by its name (e.g. Spotify, Chrome, Notepad, Calculator, WhatsApp). If the app is not installed this opens its website instead when it can.",
            new JsonObject { ["name"] = P("string", "App name") }, "name"));
        a.Add(Fn("open_url", "Open a web address in the default browser (e.g. gmail.com, https://youtube.com).",
            new JsonObject { ["url"] = P("string", "Web address") }, "url"));
        a.Add(Fn("web_search", "Search Google in the browser.",
            new JsonObject { ["query"] = P("string", "What to search") }, "query"));
        a.Add(Fn("play_youtube", "PLAY a song or video on YouTube right now (opens the best matching video, which starts playing). Use the exact words of the song/video/channel the user said.",
            new JsonObject { ["query"] = P("string", "Song or video") }, "query"));
        a.Add(Fn("media_control", "Control the music or video that is playing.",
            new JsonObject { ["action"] = P("string", "What to do", "play_pause", "next", "previous", "stop") }, "action"));
        a.Add(Fn("set_volume", "Change the PC volume. For up/down amount is a percentage (default 10). For set, amount is the target 0-100.",
            new JsonObject { ["action"] = P("string", "What to do", "up", "down", "mute", "set"), ["amount"] = P("integer", "Percent") }, "action"));
        a.Add(Fn("set_brightness", "Set screen brightness from 0 to 100 (laptops / supported screens).",
            new JsonObject { ["level"] = P("integer", "0-100") }, "level"));
        a.Add(Fn("power_action", "Lock, sleep, shut down, restart or sign out. For shutdown, restart and sign_out you MUST first ask the user to confirm out loud, then call again with confirmed=true.",
            new JsonObject { ["action"] = P("string", "What to do", "lock", "sleep", "shutdown", "restart", "sign_out", "cancel_shutdown"), ["confirmed"] = P("boolean", "True only after the user said yes") }, "action"));
        a.Add(Fn("window_control", "Control windows on the desktop.",
            new JsonObject { ["action"] = P("string", "What to do", "minimize_current", "maximize_current", "minimize_all", "restore_all", "close_current", "switch_window", "snap_left", "snap_right", "task_view", "screenshot", "lock_screen_off"), ["confirmed"] = P("boolean", "True only after the user said yes (close_current)") }, "action"));
        a.Add(Fn("press_keys", "Press a keyboard shortcut, e.g. ctrl+c, ctrl+v, alt+tab, win+d, ctrl+shift+t, enter, f5.",
            new JsonObject { ["keys"] = P("string", "Keys joined with +") }, "keys"));
        a.Add(Fn("type_text", "Type (paste) text into the window that is active. Works for long texts such as a prompt for another AI. With submit=true it also presses Enter, but only after the user confirmed out loud (confirmed=true).",
            new JsonObject { ["text"] = P("string", "Text to type"), ["submit"] = P("boolean", "Press Enter afterwards"), ["confirmed"] = P("boolean", "True only after the user said yes to sending") }, "text"));
        a.Add(Fn("focus_window", "Bring an open window to the front by part of its title (e.g. 'Chrome', 'Claude', 'Notepad'). Use it before typing.",
            new JsonObject { ["title"] = P("string", "Part of the window title") }, "title"));
        a.Add(Fn("wait", "Wait a few seconds (1-10), e.g. for a page or app to finish opening before typing.",
            new JsonObject { ["seconds"] = P("integer", "Seconds") }, "seconds"));
        a.Add(Fn("install_app", "Install a program on this PC from the official package source (winget). Ask the user to confirm first, then call with confirmed=true. It runs in the background and you are told when it is done. Pass id when you know the exact winget id (e.g. Spotify.Spotify, Google.Chrome, Discord.Discord).",
            new JsonObject { ["name"] = P("string", "Program name"), ["id"] = P("string", "Exact winget package id, optional"), ["confirmed"] = P("boolean", "True only after the user said yes") }, "name"));
        a.Add(Fn("run_powershell", "Run a PowerShell command for anything the other tools cannot do (settings, services, downloads, system tasks). ALWAYS tell the user what it will do and get a clear yes first (confirmed=true). Never run destructive commands unless the user asked for exactly that.",
            new JsonObject { ["script"] = P("string", "PowerShell script"), ["confirmed"] = P("boolean", "True only after the user said yes") }, "script", "confirmed"));
        a.Add(Fn("clipboard", "Read, write or clear the clipboard text.",
            new JsonObject { ["action"] = P("string", "What to do", "read", "write", "clear"), ["text"] = P("string", "Text for write") }, "action"));
        a.Add(Fn("file_operation", "Work with files and folders. Paths may start with Desktop, Downloads, Documents, Pictures, Music or Videos (e.g. 'Downloads\\report.pdf'). delete moves to the Recycle Bin and needs confirmed=true after the user agreed.",
            new JsonObject
            {
                ["action"] = P("string", "What to do", "open", "copy", "move", "delete", "rename", "create_folder", "list", "reveal"),
                ["path"] = P("string", "Source path"),
                ["destination"] = P("string", "Destination path or new name"),
                ["confirmed"] = P("boolean", "True only after the user said yes (delete)")
            }, "action", "path"));
        a.Add(Fn("find_files", "Search for files by name part inside a folder (default: Documents, Desktop and Downloads).",
            new JsonObject { ["query"] = P("string", "Part of the file name"), ["folder"] = P("string", "Optional folder") }, "query"));
        a.Add(Fn("empty_recycle_bin", "Empty the Recycle Bin. Ask the user to confirm first, then use confirmed=true.",
            new JsonObject { ["confirmed"] = P("boolean", "True only after the user said yes") }, "confirmed"));
        a.Add(Fn("clear_cache", "Clean temporary files and caches on the PC to free space."));
        a.Add(Fn("close_app", "Close an application politely by name. Ask the user first, then confirmed=true.",
            new JsonObject { ["name"] = P("string", "App or process name"), ["confirmed"] = P("boolean", "True after the user said yes") }, "name"));
        a.Add(Fn("set_reminder", "Remind the user about something later. Give the exact local date-time when (format yyyy-MM-ddTHH:mm, 24h) OR in_minutes. Optionally a website to open when the reminder fires (e.g. https://mail.google.com for an email task).",
            new JsonObject
            {
                ["text"] = P("string", "What to remind"),
                ["when"] = P("string", "Local time yyyy-MM-ddTHH:mm"),
                ["in_minutes"] = P("integer", "Minutes from now"),
                ["open_url"] = P("string", "Website to open at that time")
            }, "text"));
        a.Add(Fn("list_reminders", "List the pending reminders."));
        a.Add(Fn("cancel_reminder", "Cancel a reminder by id or part of its text.",
            new JsonObject { ["match"] = P("string", "Id or text") }, "match"));
        a.Add(Fn("remember", "Save a fact about the user for the future (names, preferences, habits). Use it whenever the user tells you something worth keeping.",
            new JsonObject { ["fact"] = P("string", "Short fact") }, "fact"));
        a.Add(Fn("forget", "Forget remembered facts that contain this text.",
            new JsonObject { ["match"] = P("string", "Text to match") }, "match"));
        a.Add(Fn("system_info", "Battery, memory, disk space and uptime of this PC."));
        a.Add(Fn("get_time", "Current date and time."));
        return a;
    }

    public static string? Label(string name, JsonObject? args)
    {
        string S(string k) => args?[k]?.ToString() ?? "";
        return name switch
        {
            "open_app" => "Opening " + S("name"),
            "open_url" => "Opening website",
            "web_search" => "Searching: " + S("query"),
            "play_youtube" => "YouTube: " + S("query"),
            "media_control" => "Media: " + S("action").Replace('_', ' '),
            "set_volume" => "Volume",
            "set_brightness" => "Brightness " + S("level") + "%",
            "power_action" => "Power: " + S("action").Replace('_', ' '),
            "window_control" => "Windows: " + S("action").Replace('_', ' '),
            "press_keys" => "Keys " + S("keys"),
            "type_text" => "Typing",
            "clipboard" => "Clipboard",
            "file_operation" => "Files: " + S("action"),
            "find_files" => "Finding files",
            "empty_recycle_bin" => "Recycle Bin",
            "clear_cache" => "Cleaning PC",
            "close_app" => "Closing " + S("name"),
            "set_reminder" => "Reminder set",
            "remember" => "Remembered",
            _ => null
        };
    }

    // ---------- runner ----------
    public static string Run(string name, JsonObject? a)
    {
        string S(string k) => a?[k]?.ToString()?.Trim() ?? "";
        int I(string k, int d) => int.TryParse(a?[k]?.ToString(), out int v) ? v : d;
        bool Yes() => string.Equals(a?["confirmed"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);

        switch (name)
        {
            case "open_app": return OpenApp(S("name"));
            case "open_url": return OpenUrl(S("url"));
            case "web_search": return OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString(S("query")));
            case "play_youtube": return PlayYoutube(S("query"));
            case "media_control":
                switch (S("action"))
                {
                    case "play_pause": Key(0xB3); return "Done.";
                    case "next": Key(0xB0); return "Next track.";
                    case "previous": Key(0xB1); return "Previous track.";
                    case "stop": Key(0xB2); return "Stopped.";
                }
                return "Unknown media action.";
            case "set_volume": return Volume(S("action"), I("amount", 10));
            case "set_brightness": return Brightness(Math.Clamp(I("level", 50), 0, 100));
            case "power_action": return Power(S("action"), Yes());
            case "window_control": return Windows(S("action"), Yes());
            case "press_keys": return Hotkey(S("keys")) ? "Pressed " + S("keys") + "." : "I did not understand those keys.";
            case "type_text": return TypeText(S("text"), string.Equals(a?["submit"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase), Yes());
            case "focus_window": return Focus(S("title"));
            case "wait": Thread.Sleep(Math.Clamp(I("seconds", 2), 1, 10) * 1000); return "Waited.";
            case "install_app": return Install(S("name"), S("id"), Yes());
            case "run_powershell":
                {
                    if (!Yes()) return "Not done. Tell the user what you will run and ask for a clear yes first.";
                    MiloLog.W("powershell: " + S("script"));
                    string o = Ps(S("script"), 60000);
                    return o.Length == 0 ? "Done (no output)." : Trim(o, 1500);
                }
            case "clipboard": return Clip(S("action"), S("text"));
            case "file_operation": return FileOp(S("action"), S("path"), S("destination"), Yes());
            case "find_files": return Find(S("query"), S("folder"));
            case "empty_recycle_bin":
                if (!Yes()) return "Not done. Ask the user to confirm first.";
                Ps("Clear-RecycleBin -Force -ErrorAction SilentlyContinue", 30000);
                return "Recycle Bin emptied.";
            case "clear_cache":
                {
                    var r = Cleaner.RunAsync().GetAwaiter().GetResult();
                    return "Cleaned " + (r.Bytes / 1048576) + " MB (" + r.Files + " files).";
                }
            case "close_app": return CloseApp(S("name"), Yes());
            case "set_reminder": return SetReminder(S("text"), S("when"), I("in_minutes", 0), S("open_url"));
            case "list_reminders":
                {
                    var l = MiloReminders.Load();
                    if (l.Count == 0) return "No reminders.";
                    return string.Join("; ", l.Select(r => r.Id + ": " + r.Text + " at " + r.Due.ToString("ddd HH:mm")));
                }
            case "cancel_reminder":
                if (!MiloReminders.Remove(S("match"))) return "No such reminder.";
                RemindersChanged?.Invoke();
                return "Reminder cancelled.";
            case "remember": return MiloMemory.Add(S("fact"));
            case "forget": return MiloMemory.Forget(S("match"));
            case "system_info": return SysInfo();
            case "get_time": return DateTime.Now.ToString("dddd, d MMMM yyyy, HH:mm");
        }
        return "Unknown tool " + name + ".";
    }

    // ---------- helpers ----------
    private static void Key(byte vk, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 2, UIntPtr.Zero);
        }
    }

    private static string Ps(string script, int timeoutMs = 15000)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
            using var p = Process.Start(psi);
            if (p == null) return "";
            string o = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
            return o.Trim();
        }
        catch (Exception ex) { MiloLog.W("ps: " + ex.Message); return ""; }
    }

    private static bool Shell(string target, string? args = null)
    {
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (args != null) psi.Arguments = args;
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex) { MiloLog.W("shell " + target + ": " + ex.Message); return false; }
    }

    private static readonly System.Net.Http.HttpClient Web = MakeWeb();
    private static System.Net.Http.HttpClient MakeWeb()
    {
        var c = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.8");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "CONSENT=YES+1; SOCS=CAI");
        return c;
    }

    /// <summary>Finds the first real video for the words and opens it (so it plays at once); falls back to the search page.</summary>
    private static string PlayYoutube(string q)
    {
        q = q.Trim();
        if (q.Length == 0) return "What should I play?";
        string search = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(q);
        try
        {
            string html = Web.GetStringAsync(search + "&sp=EgIQAQ%253D%253D").GetAwaiter().GetResult(); // videos only
            var m = System.Text.RegularExpressions.Regex.Match(html, "\"videoRenderer\":\\{\"videoId\":\"([A-Za-z0-9_-]{11})\"");
            if (m.Success)
            {
                string title = "";
                var tm = System.Text.RegularExpressions.Regex.Match(html.Substring(m.Index, Math.Min(2500, html.Length - m.Index)), "\"title\":\\{\"runs\":\\[\\{\"text\":\"([^\"]{1,120})\"");
                if (tm.Success) title = tm.Groups[1].Value;
                string r = OpenUrl("https://www.youtube.com/watch?v=" + m.Groups[1].Value);
                return r == "Opened." ? "Playing the first result on YouTube" + (title.Length > 0 ? ": " + title : "") + "." : r;
            }
        }
        catch (Exception ex) { MiloLog.W("youtube lookup: " + ex.Message); }
        string o = OpenUrl(search);
        return o == "Opened." ? "Opened the YouTube search results (could not pick a video automatically)." : o;
    }

    private static string OpenUrl(string url)
    {
        if (url.Length == 0) return "No address given.";
        if (!url.Contains("://")) url = "https://" + url;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return "Only web addresses are allowed.";
        return Shell(url) ? "Opened." : "Could not open the browser.";
    }

    // ---------- apps ----------
    private static readonly Dictionary<string, (string Exe, string? Web)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["notepad"] = ("notepad.exe", null), ["calculator"] = ("calc.exe", null), ["calc"] = ("calc.exe", null),
        ["paint"] = ("mspaint.exe", null), ["explorer"] = ("explorer.exe", null), ["file explorer"] = ("explorer.exe", null),
        ["files"] = ("explorer.exe", null), ["task manager"] = ("taskmgr.exe", null), ["settings"] = ("ms-settings:", null),
        ["command prompt"] = ("cmd.exe", null), ["cmd"] = ("cmd.exe", null), ["terminal"] = ("wt.exe", null),
        ["control panel"] = ("control.exe", null), ["snipping tool"] = ("snippingtool.exe", null),
        ["camera"] = ("microsoft.windows.camera:", null), ["clock"] = ("ms-clock:", null), ["store"] = ("ms-windows-store:", null),
        ["spotify"] = ("spotify.exe", "https://open.spotify.com"),
        ["chrome"] = ("chrome.exe", null), ["google chrome"] = ("chrome.exe", null), ["edge"] = ("msedge.exe", null),
        ["firefox"] = ("firefox.exe", null), ["brave"] = ("brave.exe", null),
        ["whatsapp"] = ("whatsapp:", "https://web.whatsapp.com"), ["telegram"] = ("telegram.exe", "https://web.telegram.org"),
        ["discord"] = ("discord.exe", "https://discord.com/app"), ["youtube"] = ("", "https://www.youtube.com"),
        ["gmail"] = ("", "https://mail.google.com"), ["google"] = ("", "https://www.google.com"),
        ["instagram"] = ("", "https://www.instagram.com"), ["facebook"] = ("", "https://www.facebook.com"),
        ["netflix"] = ("", "https://www.netflix.com"), ["chatgpt"] = ("", "https://chatgpt.com"),
        ["word"] = ("winword.exe", null), ["excel"] = ("excel.exe", null), ["powerpoint"] = ("powerpnt.exe", null),
        ["vs code"] = ("code.exe", null), ["vscode"] = ("code.exe", null), ["steam"] = ("steam.exe", null),
        ["zoom"] = ("zoom.exe", "https://zoom.us"), ["teams"] = ("msteams:", "https://teams.microsoft.com"),
        ["outlook"] = ("outlook.exe", "https://outlook.live.com"), ["maps"] = ("", "https://maps.google.com"),
    };

    private static List<string>? _links;
    private static long _linksAt;
    private static readonly object LinksLock = new();

    /// <summary>Start menu shortcuts. Scanned once and kept for 10 minutes: opening an app or starting a chat never waits for the disk.</summary>
    private static IEnumerable<string> StartMenuLinks()
    {
        lock (LinksLock)
        {
            if (_links != null && Environment.TickCount64 - _linksAt < 600000) return _links;
            var l = new List<string>();
            string[] roots =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            };
            foreach (string r in roots)
            {
                try { if (Directory.Exists(r)) l.AddRange(Directory.EnumerateFiles(r, "*.lnk", SearchOption.AllDirectories)); }
                catch { }
            }
            _links = l; _linksAt = Environment.TickCount64;
            return l;
        }
    }

    /// <summary>Reads the Start menu in the background (called early, so the first chat starts fast).</summary>
    public static void Prewarm() { Task.Run(() => { try { InstalledAppNames(70); } catch { } }); }

    /// <summary>Names of the programs in the Start menu (used to help Milo match what the user says).</summary>
    public static List<string> InstalledAppNames(int max)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string f in StartMenuLinks())
            {
                string n = Path.GetFileNameWithoutExtension(f);
                string l = n.ToLowerInvariant();
                if (l.Contains("uninstall") || l.Contains("readme") || l.Contains("license") || l.Contains("help") || l.Contains("documentation")) continue;
                if (n.Length > 1 && n.Length < 40) set.Add(n);
                if (set.Count >= max * 3) break;
            }
        }
        catch { }
        return set.OrderBy(x => x).Take(max).ToList();
    }

    private static bool ExeExists(string exe)
    {
        if (exe.Length == 0) return false;
        if (exe.EndsWith(":")) return true; // protocol: cannot be checked
        string? paths = Environment.GetEnvironmentVariable("PATH");
        if (paths != null)
            foreach (string d in paths.Split(';'))
            {
                try { if (d.Length > 0 && File.Exists(Path.Combine(d.Trim('"'), exe))) return true; } catch { }
            }
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe)
                          ?? Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
            if (k != null) return true;
        }
        catch { }
        return false;
    }

    private static string OpenApp(string name)
    {
        if (name.Length == 0) return "Which app?";
        string key = name.Trim().ToLowerInvariant();
        if (key.StartsWith("the ")) key = key.Substring(4);
        if (key.EndsWith(" app")) key = key.Substring(0, key.Length - 4);

        Known.TryGetValue(key, out var known);

        // 1) a real, installed program (Start menu shortcut)
        string? best = null;
        int bestScore = 0;
        foreach (string lnk in StartMenuLinks())
        {
            string n = Path.GetFileNameWithoutExtension(lnk).ToLowerInvariant();
            int score = n == key ? 100 : n.StartsWith(key) ? 80 : n.Contains(key) ? 60 : 0;
            if (score > bestScore) { bestScore = score; best = lnk; }
        }
        if (best != null && Shell(best)) return "Opened " + Path.GetFileNameWithoutExtension(best) + ".";

        // 2) known system program or protocol
        if (known.Exe != null && known.Exe.Length > 0 && ExeExists(known.Exe) && Shell(known.Exe))
            return "Opened " + name + ".";

        // 3) Store / UWP apps
        string safe = key.Replace("'", "''");
        string app = Ps("(Get-StartApps | Where-Object { $_.Name -like '*" + safe + "*' } | Select-Object -First 1).AppID", 12000);
        if (app.Length > 0 && Shell("explorer.exe", "shell:AppsFolder\\" + app)) return "Opened " + name + ".";

        // 4) not installed: use the website
        if (known.Web != null)
        {
            Shell(known.Web);
            return name + " is not installed on this PC, so I opened its website instead.";
        }
        if (known.Exe != null && known.Exe.Length > 0 && Shell(known.Exe)) return "Opened " + name + ".";
        return "'" + name + "' is not installed on this PC. Offer the user two choices: install it (install_app) or use its website (open_url).";
    }

    private static string CloseApp(string name, bool confirmed)
    {
        if (!confirmed) return "Not done. Ask the user to confirm first.";
        string n = name.Trim().ToLowerInvariant();
        if (n.EndsWith(".exe")) n = n.Substring(0, n.Length - 4);
        string[] protectedNames = { "explorer", "lite island", "liteisland", "winlogon", "csrss", "system", "svchost", "dwm" };
        if (protectedNames.Contains(n)) return "I will not close that one.";
        int closed = 0;
        foreach (Process p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                if (p.ProcessName.ToLowerInvariant().Contains(n) || p.MainWindowTitle.ToLowerInvariant().Contains(n))
                {
                    if (p.Id == Environment.ProcessId) continue;
                    if (p.CloseMainWindow()) closed++;
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        return closed > 0 ? "Asked " + closed + " window(s) of " + name + " to close." : "I did not find an open window for " + name + ".";
    }

    // ---------- volume / brightness / power ----------
    private static string Volume(string action, int amount)
    {
        amount = Math.Clamp(amount, 0, 100);
        switch (action)
        {
            case "up": Key(0xAF, Math.Max(1, amount / 2)); return "Volume up.";
            case "down": Key(0xAE, Math.Max(1, amount / 2)); return "Volume down.";
            case "mute": Key(0xAD); return "Mute toggled.";
            case "set":
                Key(0xAE, 50);
                if (amount > 0) Key(0xAF, amount / 2);
                return "Volume set to about " + amount + "%.";
        }
        return "Unknown volume action.";
    }

    private static string Brightness(int level)
    {
        string o = Ps("try { (Get-WmiObject -Namespace root/WMI -Class WmiMonitorBrightnessMethods -ErrorAction Stop).WmiSetBrightness(1," + level + "); 'ok' } catch { 'fail' }", 12000);
        return o.Contains("ok") ? "Brightness set to " + level + "%." : "This screen does not allow brightness control from software.";
    }

    private static string Power(string action, bool confirmed)
    {
        switch (action)
        {
            case "lock": Shell("rundll32.exe", "user32.dll,LockWorkStation"); return "Locked.";
            case "sleep": Shell("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"); return "Going to sleep.";
            case "cancel_shutdown": Shell("shutdown.exe", "/a"); return "Cancelled.";
            case "shutdown":
                if (!confirmed) return "Not done. Ask the user to confirm the shutdown first.";
                Shell("shutdown.exe", "/s /t 10"); return "Shutting down in 10 seconds. Say cancel to stop it.";
            case "restart":
                if (!confirmed) return "Not done. Ask the user to confirm the restart first.";
                Shell("shutdown.exe", "/r /t 10"); return "Restarting in 10 seconds. Say cancel to stop it.";
            case "sign_out":
                if (!confirmed) return "Not done. Ask the user to confirm first.";
                Shell("shutdown.exe", "/l"); return "Signing out.";
        }
        return "Unknown power action.";
    }

    // ---------- windows / keys ----------
    private static string Windows(string action, bool confirmed)
    {
        IntPtr h = GetForegroundWindow();
        switch (action)
        {
            case "minimize_current": ShowWindow(h, 6); return "Minimized.";
            case "maximize_current": ShowWindow(h, 3); return "Maximized.";
            case "minimize_all": Hotkey("win+d"); return "Showing the desktop.";
            case "restore_all": Hotkey("win+d"); return "Windows restored.";
            case "close_current":
                if (!confirmed) return "Not done. Ask the user to confirm first.";
                Hotkey("alt+f4"); return "Closed.";
            case "switch_window": Hotkey("alt+tab"); return "Switched.";
            case "snap_left": Hotkey("win+left"); return "Snapped left.";
            case "snap_right": Hotkey("win+right"); return "Snapped right.";
            case "task_view": Hotkey("win+tab"); return "Task view.";
            case "screenshot": Hotkey("win+shift+s"); return "Screenshot tool opened.";
            case "lock_screen_off": return "Not supported.";
        }
        return "Unknown window action.";
    }

    private static readonly Dictionary<string, byte> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B, ["windows"] = 0x5B,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20,
        ["delete"] = 0x2E, ["del"] = 0x2E, ["backspace"] = 0x08, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pagedown"] = 0x22, ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["printscreen"] = 0x2C, ["plus"] = 0xBB, ["minus"] = 0xBD
    };

    private static bool Hotkey(string keys)
    {
        var codes = new List<byte>();
        foreach (string raw in keys.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string k = raw.ToLowerInvariant();
            if (Names.TryGetValue(k, out byte v)) codes.Add(v);
            else if (k.Length == 1 && k[0] >= 'a' && k[0] <= 'z') codes.Add((byte)(k[0] - 'a' + 0x41));
            else if (k.Length == 1 && k[0] >= '0' && k[0] <= '9') codes.Add((byte)k[0]);
            else if (k.Length >= 2 && k[0] == 'f' && int.TryParse(k.AsSpan(1), out int f) && f >= 1 && f <= 12) codes.Add((byte)(0x6F + f));
            else return false;
        }
        if (codes.Count == 0 || codes.Count > 5) return false;
        foreach (byte c in codes) keybd_event(c, 0, 0, UIntPtr.Zero);
        for (int i = codes.Count - 1; i >= 0; i--) keybd_event(codes[i], 0, 2, UIntPtr.Zero);
        return true;
    }

    private static string TypeText(string text, bool submit, bool confirmed)
    {
        if (text.Length == 0) return "Nothing to type.";
        Clip("write", text);
        Thread.Sleep(150);
        Hotkey("ctrl+v");
        if (!submit) return "Typed. It is not sent yet: read the gist to the user, ask if you should press Enter, then call press_keys with enter.";
        if (!confirmed) return "Typed, but NOT sent because the user did not confirm. Ask the user, then call press_keys with enter.";
        Thread.Sleep(300);
        Hotkey("enter");
        return "Typed and sent.";
    }

    private static string Focus(string title)
    {
        if (title.Length == 0) return "Which window?";
        IntPtr found = IntPtr.Zero;
        string foundTitle = "";
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            string t = sb.ToString();
            if (t.Length > 0 && t.Contains(title, StringComparison.OrdinalIgnoreCase) && !t.Contains("LiteIsland", StringComparison.OrdinalIgnoreCase))
            {
                found = h; foundTitle = t;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero) return "No open window with '" + title + "' in its title.";
        ShowWindow(found, 9);
        // Windows only lets a program take the focus after an Alt key press
        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(found);
        Thread.Sleep(250);
        return "Brought '" + Trim(foundTitle, 60) + "' to the front.";
    }

    private static string Install(string name, string id, bool confirmed)
    {
        if (name.Length == 0 && id.Length == 0) return "Which program?";
        if (!confirmed) return "Not done. Ask the user to confirm installing " + (name.Length > 0 ? name : id) + " first.";
        if (!Regex.IsMatch(name + id, @"^[\w .+\-]*$")) return "That name has odd characters; ask the user to repeat it.";
        string args = id.Length > 0
            ? "install --id " + id + " -e"
            : "install --name \"" + name + "\"";
        args += " --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
        string label = name.Length > 0 ? name : id;
        Task.Run(() =>
        {
            try
            {
                string wg = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
                var psi = System.IO.File.Exists(wg) ? new ProcessStartInfo(wg, args) : new ProcessStartInfo("cmd.exe", "/c winget " + args);
                psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using var p = Process.Start(psi);
                if (p == null) { Notify?.Invoke("Install of " + label + " failed: winget not available."); return; }
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15 * 60 * 1000);
                MiloLog.W("winget " + args + " -> exit " + p.ExitCode + " " + Trim(o.Replace("\r", " ").Replace("\n", " "), 400));
                if (p.ExitCode == 0) Notify?.Invoke(label + " is installed.");
                else if (o.Contains("Multiple packages", StringComparison.OrdinalIgnoreCase) || o.Contains("more than one", StringComparison.OrdinalIgnoreCase))
                    Notify?.Invoke("Several packages match " + label + ". Ask the user which one, then use install_app with its exact id.");
                else if (o.Contains("No package found", StringComparison.OrdinalIgnoreCase))
                    Notify?.Invoke("No package named " + label + " in the official source. Offer to open its website instead.");
                else Notify?.Invoke("Install of " + label + " did not finish (code " + p.ExitCode + ").");
            }
            catch (Exception ex)
            {
                MiloLog.W("install: " + ex.Message);
                Notify?.Invoke("Install of " + label + " failed: " + ex.Message);
            }
        });
        return "Installing " + label + " in the background from winget. It may ask Windows for permission. Say you will tell the user when it is done.";
    }

    private static string Clip(string action, string text)
    {
        string result = "Clipboard unavailable.";
        var t = new Thread(() =>
        {
            try
            {
                switch (action)
                {
                    case "read":
                        result = System.Windows.Clipboard.ContainsText()
                            ? "Clipboard text: " + Trim(System.Windows.Clipboard.GetText(), 1500)
                            : "The clipboard has no text.";
                        break;
                    case "write":
                        System.Windows.Clipboard.SetText(text);
                        result = "Copied to the clipboard.";
                        break;
                    case "clear":
                        System.Windows.Clipboard.Clear();
                        result = "Clipboard cleared.";
                        break;
                    default: result = "Unknown clipboard action."; break;
                }
            }
            catch (Exception ex) { result = "Clipboard busy: " + ex.Message; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(4000);
        return result;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";

    // ---------- files ----------
    private static string Resolve(string p)
    {
        p = p.Trim().Trim('"');
        if (p.Length == 0) return p;
        string[] parts = p.Replace('/', '\\').Split('\\', 2);
        string head = parts[0].ToLowerInvariant();
        string? root = head switch
        {
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            _ => null
        };
        if (root == null) return p;
        return parts.Length > 1 && parts[1].Length > 0 ? Path.Combine(root, parts[1]) : root;
    }

    private static bool IsSystemPath(string full)
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (full.Length <= 3) return true; // a drive root
        foreach (string s in new[] { win, pf, pf86, pd })
            if (s.Length > 0 && full.StartsWith(s, StringComparison.OrdinalIgnoreCase)) return true;
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.Equals(full.TrimEnd('\\'), user, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string FileOp(string action, string path, string dest, bool confirmed)
    {
        try
        {
            string src = Path.GetFullPath(Resolve(path));
            bool isDir = Directory.Exists(src), isFile = File.Exists(src);
            switch (action)
            {
                case "open":
                    if (!isDir && !isFile) return "That path does not exist.";
                    return Shell(src) ? "Opened." : "Could not open it.";
                case "reveal":
                    if (!isDir && !isFile) return "That path does not exist.";
                    Shell("explorer.exe", "/select,\"" + src + "\""); return "Shown in Explorer.";
                case "list":
                    {
                        if (!isDir) return "That folder does not exist.";
                        var items = Directory.EnumerateFileSystemEntries(src).Take(40).Select(Path.GetFileName);
                        return string.Join(", ", items);
                    }
                case "create_folder":
                    if (IsSystemPath(src)) return "I will not create that in a system location.";
                    Directory.CreateDirectory(src); return "Folder created.";
                case "copy":
                case "move":
                case "rename":
                    {
                        if (!isDir && !isFile) return "That path does not exist.";
                        if (dest.Length == 0) return "Where to?";
                        string d = Resolve(dest);
                        if (action == "rename" && !d.Contains('\\') && !d.Contains('/')) d = Path.Combine(Path.GetDirectoryName(src) ?? "", d);
                        d = Path.GetFullPath(d);
                        if (Directory.Exists(d)) d = Path.Combine(d, Path.GetFileName(src));
                        if (IsSystemPath(src) || IsSystemPath(d)) return "I will not touch system locations.";
                        if (File.Exists(d) || Directory.Exists(d)) return "Something with that name is already there.";
                        if (action == "copy")
                        {
                            if (isFile) File.Copy(src, d);
                            else CopyDir(src, d);
                            return "Copied.";
                        }
                        if (isFile) File.Move(src, d); else Directory.Move(src, d);
                        return action == "move" ? "Moved." : "Renamed.";
                    }
                case "delete":
                    {
                        if (!isDir && !isFile) return "That path does not exist.";
                        if (IsSystemPath(src)) return "I will not delete system locations.";
                        if (!confirmed) return "Not done. Ask the user to confirm the delete of " + Path.GetFileName(src) + " first.";
                        if (isFile) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(src, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        else Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(src, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        return "Moved to the Recycle Bin.";
                    }
            }
            return "Unknown file action.";
        }
        catch (Exception ex) { return "File action failed: " + ex.Message; }
    }

    private static void CopyDir(string s, string d)
    {
        Directory.CreateDirectory(d);
        foreach (string f in Directory.GetFiles(s)) File.Copy(f, Path.Combine(d, Path.GetFileName(f)));
        foreach (string sub in Directory.GetDirectories(s)) CopyDir(sub, Path.Combine(d, Path.GetFileName(sub)));
    }

    private static string Find(string query, string folder)
    {
        if (query.Length == 0) return "What should I look for?";
        var roots = new List<string>();
        if (folder.Length > 0) roots.Add(Path.GetFullPath(Resolve(folder)));
        else
        {
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        }
        var found = new List<string>();
        var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        DateTime end = DateTime.UtcNow.AddSeconds(6);
        foreach (string r in roots)
        {
            if (!Directory.Exists(r)) continue;
            try
            {
                foreach (string f in Directory.EnumerateFiles(r, "*" + query + "*", opt))
                {
                    found.Add(f);
                    if (found.Count >= 8 || DateTime.UtcNow > end) break;
                }
            }
            catch { }
            if (found.Count >= 8 || DateTime.UtcNow > end) break;
        }
        return found.Count == 0 ? "No files found." : "Found: " + string.Join(" | ", found);
    }

    // ---------- reminders / info ----------
    private static string SetReminder(string text, string when, int inMin, string url)
    {
        if (text.Length == 0) return "What should I remind you about?";
        DateTime due;
        if (inMin > 0) due = DateTime.Now.AddMinutes(inMin);
        else if (!DateTime.TryParse(when, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out due))
            return "I need a time for the reminder.";
        if (due <= DateTime.Now.AddSeconds(5)) return "That time has already passed. Ask the user for a future time.";
        string? u = null;
        if (url.Length > 0) { u = url.Contains("://") ? url : "https://" + url; if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) u = null; }
        MiloReminders.Add(text, due, u);
        RemindersChanged?.Invoke();
        return "Reminder saved for " + due.ToString("dddd HH:mm") + ".";
    }

    private static string SysInfo()
    {
        var sb = new StringBuilder();
        try
        {
            string bat = Ps("$b=Get-CimInstance Win32_Battery; if($b){ \"$($b.EstimatedChargeRemaining)% (status $($b.BatteryStatus))\" } else { 'no battery' }", 8000);
            sb.Append("Battery: ").Append(bat.Length > 0 ? bat : "unknown").Append(". ");
        }
        catch { }
        try
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var di = new DriveInfo(root);
            sb.Append("Disk ").Append(root).Append(": ").Append(di.AvailableFreeSpace / 1073741824).Append(" GB free of ").Append(di.TotalSize / 1073741824).Append(" GB. ");
        }
        catch { }
        sb.Append("Uptime: ").Append((long)TimeSpan.FromMilliseconds(Environment.TickCount64).TotalHours).Append(" hours.");
        return sb.ToString();
    }
}
