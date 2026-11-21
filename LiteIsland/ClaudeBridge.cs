using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LiteIsland;

/// <summary>
/// Lets Claude Code (terminal, IDE and desktop app) ask permission through the island.
/// Claude Code calls two "http hooks" on this PC: PermissionRequest (needs an answer) and Stop (task finished).
/// A tiny server on 127.0.0.1 receives them. Nothing leaves the PC, and a secret token is part of the address,
/// so web pages and other programs cannot talk to it. If the island is not running, Claude Code simply
/// shows its normal prompt.
/// </summary>
internal sealed class ClaudeBridge
{
    public const int Port = 47831;
    private readonly string _token;
    private TcpListener? _l;
    private volatile bool _stop;

    /// <summary>(tool, what it wants to do, project) → 0 = decide in Claude, 1 = allow, 2 = deny.</summary>
    public Func<string, string, string, Task<int>>? OnPermission;
    /// <summary>(project, last message) when a task finished.</summary>
    public Action<string, string>? OnDone;

    public ClaudeBridge(string token) { _token = token; }

    public void Start()
    {
        _l = new TcpListener(IPAddress.Loopback, Port);
        _l.Start(8);
        _ = Task.Run(Loop);
    }

    public void Stop()
    {
        _stop = true;
        try { _l?.Stop(); } catch { }
    }

    private async Task Loop()
    {
        while (!_stop)
        {
            TcpClient c;
            try { c = await _l!.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => Handle(c));
        }
    }

    private static int Find(byte[] b, int len, string s)
    {
        byte[] p = Encoding.ASCII.GetBytes(s);
        for (int i = 0; i + p.Length <= len; i++)
        {
            int k = 0;
            while (k < p.Length && b[i + k] == p[k]) k++;
            if (k == p.Length) return i;
        }
        return -1;
    }

    private async Task Handle(TcpClient c)
    {
        using (c)
        {
            try
            {
                var ns = c.GetStream();
                using var rd = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var buf = new byte[16384];
                int len = 0, hdrEnd = -1;
                while (hdrEnd < 0 && len < buf.Length)
                {
                    int n = await ns.ReadAsync(buf.AsMemory(len, buf.Length - len), rd.Token).ConfigureAwait(false);
                    if (n <= 0) return;
                    len += n;
                    hdrEnd = Find(buf, len, "\r\n\r\n");
                }
                if (hdrEnd < 0) return;
                string head = Encoding.ASCII.GetString(buf, 0, hdrEnd);
                string[] lines = head.Split("\r\n");
                string[] req = lines[0].Split(' ');
                if (req.Length < 2 || req[0] != "POST") { await Reply(ns, 405, "{}"); return; }

                int contentLen = 0;
                bool chunked = false, hostOk = false;
                foreach (string h in lines)
                {
                    if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(h.Substring(15).Trim(), out contentLen);
                    else if (h.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) && h.Contains("chunked", StringComparison.OrdinalIgnoreCase)) chunked = true;
                    else if (h.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase)) { await Reply(ns, 403, "{}"); return; } // a web page, never Claude Code
                    else if (h.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                    {
                        string hv = h.Substring(5).Trim();
                        hostOk = hv.StartsWith("127.0.0.1", StringComparison.Ordinal) || hv.StartsWith("localhost", StringComparison.OrdinalIgnoreCase);
                    }
                }
                string path = req[1];
                if (!hostOk || contentLen < 0 || contentLen > 8_000_000) { await Reply(ns, 400, "{}"); return; }
                string kind;
                if (path == "/perm/" + _token) kind = "perm";
                else if (path == "/done/" + _token) kind = "done";
                else { await Reply(ns, 404, "{}"); return; }
                if (chunked) { await Reply(ns, 200, "{}"); return; } // cannot be read here: Claude Code shows its normal prompt

                var body = new MemoryStream();
                int have = len - (hdrEnd + 4);
                if (have > 0) body.Write(buf, hdrEnd + 4, Math.Min(have, contentLen));
                while (body.Length < contentLen)
                {
                    int n = await ns.ReadAsync(buf.AsMemory(0, buf.Length), rd.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    body.Write(buf, 0, (int)Math.Min(n, contentLen - body.Length));
                }

                JsonNode? j = null;
                try { j = JsonNode.Parse(Encoding.UTF8.GetString(body.ToArray())); } catch { }
                string cwd = j?["cwd"]?.ToString() ?? "";
                string project = Path.GetFileName(cwd.TrimEnd('/', '\\'));
                if (project.Length == 0) project = "Claude Code";

                if (kind == "done")
                {
                    string msg = (j?["last_assistant_message"]?.ToString() ?? "").Trim();
                    try { OnDone?.Invoke(project, msg); } catch { }
                    await Reply(ns, 200, "{}");
                    return;
                }

                string tool = j?["tool_name"]?.ToString() ?? "a tool";
                string summary = Summarize(tool, j?["tool_input"]);
                int r = 0;
                try { if (OnPermission != null) r = await OnPermission(tool, summary, project).ConfigureAwait(false); } catch { }
                string outJson = r == 1
                    ? "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}"
                    : r == 2
                        ? "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"deny\",\"message\":\"Denied from LiteIsland by the user.\",\"interrupt\":false}}}"
                        : "{}";
                await Reply(ns, 200, outJson);
            }
            catch { }
        }
    }

    private static async Task Reply(NetworkStream ns, int code, string json)
    {
        byte[] b = Encoding.UTF8.GetBytes(json);
        string status = code == 200 ? "OK" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : code == 405 ? "Method Not Allowed" : "Bad Request";
        byte[] h = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + " " + status + "\r\nContent-Type: application/json\r\nContent-Length: " + b.Length + "\r\nConnection: close\r\n\r\n");
        await ns.WriteAsync(h).ConfigureAwait(false);
        await ns.WriteAsync(b).ConfigureAwait(false);
        await ns.FlushAsync().ConfigureAwait(false);
    }

    private static string Summarize(string tool, JsonNode? input)
    {
        string? s = input?["command"]?.ToString() ?? input?["file_path"]?.ToString() ?? input?["url"]?.ToString()
                    ?? input?["pattern"]?.ToString() ?? input?["query"]?.ToString() ?? input?["path"]?.ToString();
        if (string.IsNullOrWhiteSpace(s)) s = input?.ToJsonString() ?? "";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 220 ? s.Substring(0, 220) + "…" : s;
    }

    // ---------- connecting Claude Code: one entry per event in ~/.claude/settings.json ----------

    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
    private static bool Ours(JsonNode? group)
    {
        try
        {
            foreach (var h in group?["hooks"]?.AsArray() ?? new JsonArray())
                if ((h?["url"]?.ToString() ?? "").StartsWith("http://127.0.0.1:" + Port + "/", StringComparison.Ordinal)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>Adds our two hooks (replacing older ones of ours) and keeps everything else in the file untouched.</summary>
    public static bool Install(string token)
    {
        try
        {
            string p = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            JsonObject root = new();
            if (File.Exists(p))
            {
                string txt = File.ReadAllText(p);
                if (txt.Trim().Length > 0) root = JsonNode.Parse(txt, null, new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
                if (!File.Exists(p + ".liteisland-backup")) File.Copy(p, p + ".liteisland-backup");
            }
            if (root["hooks"] is not JsonObject hooks) { hooks = new JsonObject(); root["hooks"] = hooks; }
            Put(hooks, "PermissionRequest", "http://127.0.0.1:" + Port + "/perm/" + token, 120);
            Put(hooks, "Stop", "http://127.0.0.1:" + Port + "/done/" + token, 10);
            File.WriteAllText(p, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch { return false; }
    }

    private static void Put(JsonObject hooks, string ev, string url, int timeout)
    {
        var arr = hooks[ev] as JsonArray ?? new JsonArray();
        for (int i = arr.Count - 1; i >= 0; i--) if (Ours(arr[i])) arr.RemoveAt(i);
        arr.Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "http", ["url"] = url, ["timeout"] = timeout })
        });
        hooks[ev] = arr;
    }

    public static void Uninstall()
    {
        try
        {
            string p = SettingsPath;
            if (!File.Exists(p)) return;
            var root = JsonNode.Parse(File.ReadAllText(p), null, new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject();
            if (root?["hooks"] is not JsonObject hooks) return;
            foreach (string ev in new[] { "PermissionRequest", "Stop" })
            {
                if (hooks[ev] is not JsonArray arr) continue;
                for (int i = arr.Count - 1; i >= 0; i--) if (Ours(arr[i])) arr.RemoveAt(i);
                if (arr.Count == 0) hooks.Remove(ev);
            }
            if (hooks.Count == 0) root.Remove("hooks");
            File.WriteAllText(p, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
