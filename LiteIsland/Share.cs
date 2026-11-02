using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LiteIsland;

/// <summary>
/// "Send to phone": serves the dropped files from this PC over the local network. The phone scans the QR code and the
/// browser downloads straight from the PC (no cloud, no upload, no size limit, unfinished downloads can be resumed).
/// Only the files that were dropped can be fetched, and only with the random link that the QR code contains.
/// </summary>
internal sealed class ShareServer : IDisposable
{
    public sealed record Item(string Path, string Name, long Size);

    private readonly List<Item> _items;
    private readonly string _token = RandomToken();
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(8, 8);
    private long _sent;
    private long _lastActivity = Environment.TickCount64;
    private bool _disposed;

    public string Ip { get; }
    public int Port { get; }
    public long Total { get; }
    public long Sent => Interlocked.Read(ref _sent);
    public IReadOnlyList<Item> Items => _items;
    public int Connections;

    // ---- receive mode (phone -> PC) ----
    public bool ReceiveMode { get; }
    public string ReceiveDir { get; } = "";
    private long _recvBytes, _recvExpected;
    private int _recvFiles;
    public long RecvBytes => Interlocked.Read(ref _recvBytes);
    public long RecvExpected => Interlocked.Read(ref _recvExpected);
    public int RecvFiles => Volatile.Read(ref _recvFiles);
    private const long MaxUpload = 16L * 1024 * 1024 * 1024;

    /// <summary>The address the phone has to open (the QR code contains it).</summary>
    public string Url => "http://" + Ip + ":" + Port + "/" + _token + "/";

    public bool Idle => Connections == 0 && Environment.TickCount64 - Interlocked.Read(ref _lastActivity) > 10 * 60 * 1000;

    public ShareServer(IEnumerable<string> paths)
    {
        _items = new List<Item>();
        foreach (string raw in paths)
        {
            try
            {
                string p = raw.TrimEnd('\\');
                if (File.Exists(p)) { var fi = new FileInfo(p); _items.Add(new Item(p, fi.Name, fi.Length)); }
                else if (Directory.Exists(p))
                {
                    string root = Path.GetDirectoryName(p) ?? p;
                    foreach (string f in Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System }))
                    {
                        if (_items.Count >= 300) break;
                        var fi = new FileInfo(f);
                        _items.Add(new Item(f, Path.GetRelativePath(root, f).Replace('\\', '/'), fi.Length));
                    }
                }
            }
            catch { }
        }
        if (_items.Count == 0) throw new InvalidOperationException("Nothing to share");
        Total = _items.Sum(i => i.Size);

        Ip = LanAddress();
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start(32);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>Receive mode: the phone uploads files and they are saved in <paramref name="dir"/>.</summary>
    public ShareServer(string dir, bool receive)
    {
        _items = new List<Item>();
        ReceiveMode = receive;
        ReceiveDir = dir;
        Directory.CreateDirectory(dir);
        Ip = LanAddress();
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start(32);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    private static string RandomToken()
    {
        const string c = "abcdefghijkmnpqrstuvwxyz23456789";
        var sb = new StringBuilder();
        for (int i = 0; i < 14; i++) sb.Append(c[RandomNumberGenerator.GetInt32(c.Length)]);
        return sb.ToString();
    }

    /// <summary>The address of this PC on the Wi-Fi / cable (not a virtual adapter).</summary>
    public static string LanAddress()
    {
        string? best = null;
        int bestScore = -1;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
                bool virt = d.Contains("virtual") || d.Contains("vmware") || d.Contains("vbox") || d.Contains("hyper-v") || d.Contains("vethernet") || d.Contains("wsl") || d.Contains("tap") || d.Contains("vpn");
                var props = ni.GetIPProperties();
                bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = ua.Address.GetAddressBytes();
                    if (b[0] == 169) continue;                              // no real address
                    bool priv = b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
                    int score = (gw ? 4 : 0) + (virt ? 0 : 3) + (priv ? 2 : 0) + (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0);
                    if (score > bestScore) { bestScore = score; best = ua.Address.ToString(); }
                }
            }
        }
        catch { }
        return best ?? "127.0.0.1";
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket s;
            try { s = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => Handle(s));
        }
    }

    private static readonly byte[] Crlf2 = { 13, 10, 13, 10 };

    private async Task Handle(Socket sock)
    {
        bool slot = false;
        try
        {
            sock.NoDelay = true;
            sock.SendBufferSize = 1 << 20;
            using var ns = new NetworkStream(sock, true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            // ---- request head ----
            var head = new byte[16384];
            int len = 0, end = -1;
            while (end < 0 && len < head.Length)
            {
                int n = await ns.ReadAsync(head.AsMemory(len), cts.Token).ConfigureAwait(false);
                if (n <= 0) return;
                len += n;
                end = head.AsSpan(0, len).IndexOf(Crlf2);
            }
            if (end < 0) return;
            string req = Encoding.ASCII.GetString(head, 0, end);
            string[] lines = req.Split("\r\n");
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return;
            string method = first[0].ToUpperInvariant();
            string path = first[1];
            int q = path.IndexOf('?');
            bool inline = q >= 0 && path.Substring(q).Contains("inline=1", StringComparison.Ordinal);
            if (q >= 0) path = path.Substring(0, q);
            string? range = null, xname = null;
            long contentLength = -1;
            foreach (string l in lines.Skip(1))
            {
                if (l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = l.Substring(6).Trim();
                else if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) long.TryParse(l.Substring(15).Trim(), out contentLength);
                else if (l.StartsWith("X-Name:", StringComparison.OrdinalIgnoreCase)) xname = l.Substring(7).Trim();
            }

            Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
            string prefix = "/" + _token + "/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) { await Reply(ns, 404, "Not Found", "text/plain", "Not found", cts.Token).ConfigureAwait(false); return; }
            string rest = path.Substring(prefix.Length);

            if (method == "POST" && ReceiveMode && rest == "up")
            {
                await ReceiveFile(ns, head, end + 4, len, contentLength, xname).ConfigureAwait(false);
                return;
            }
            if (method != "GET" && method != "HEAD") { await Reply(ns, 405, "Method Not Allowed", "text/plain", "", cts.Token).ConfigureAwait(false); return; }

            if (rest.Length == 0)
            {
                await Reply(ns, 200, "OK", "text/html; charset=utf-8", ReceiveMode ? ReceiveHtml() : IndexHtml(), cts.Token, method == "HEAD").ConfigureAwait(false);
                return;
            }
            if (rest.StartsWith("f/", StringComparison.Ordinal))
            {
                string[] parts = rest.Split('/');
                if (parts.Length >= 2 && int.TryParse(parts[1], out int idx) && idx >= 0 && idx < _items.Count)
                {
                    await _slots.WaitAsync(_cts.Token).ConfigureAwait(false);
                    slot = true;
                    Interlocked.Increment(ref Connections);
                    try { await SendFile(ns, _items[idx], range, method == "HEAD", inline).ConfigureAwait(false); }
                    finally { Interlocked.Decrement(ref Connections); }
                    return;
                }
            }
            await Reply(ns, 404, "Not Found", "text/plain", "Not found", cts.Token).ConfigureAwait(false);
        }
        catch { }
        finally { if (slot) _slots.Release(); Interlocked.Exchange(ref _lastActivity, Environment.TickCount64); }
    }

    private static string SafeName(string? raw)
    {
        string n = "";
        try { n = Uri.UnescapeDataString(raw ?? ""); } catch { }
        n = Path.GetFileName(n.Replace('/', '\\'));
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        n = n.Trim().TrimEnd('.');
        if (n.Length == 0) n = "file";
        if (n.Length > 120) n = n.Substring(0, 120);
        return n;
    }

    private string UniquePath(string name)
    {
        string full = Path.Combine(ReceiveDir, name);
        if (!File.Exists(full) && !File.Exists(full + ".part")) return full;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 2; i < 10000; i++)
        {
            full = Path.Combine(ReceiveDir, stem + " (" + i + ")" + ext);
            if (!File.Exists(full) && !File.Exists(full + ".part")) return full;
        }
        return Path.Combine(ReceiveDir, Guid.NewGuid().ToString("N") + ext);
    }

    private async Task ReceiveFile(NetworkStream ns, byte[] head, int bodyStart, int len, long contentLength, string? xname)
    {
        if (contentLength < 0) { await Reply(ns, 411, "Length Required", "text/plain", "Length required", _cts.Token).ConfigureAwait(false); return; }
        if (contentLength > MaxUpload) { await Reply(ns, 413, "Payload Too Large", "text/plain", "Too large", _cts.Token).ConfigureAwait(false); return; }
        string dest = UniquePath(SafeName(xname));
        string tmp = dest + ".part";
        Interlocked.Increment(ref Connections);
        Interlocked.Add(ref _recvExpected, contentLength);
        long got = 0;
        bool ok = false;
        byte[] buf = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                int have = (int)Math.Min(Math.Max(0, len - bodyStart), contentLength);
                if (have > 0)
                {
                    await fs.WriteAsync(head.AsMemory(bodyStart, have), _cts.Token).ConfigureAwait(false);
                    got += have;
                    Interlocked.Add(ref _recvBytes, have);
                }
                while (got < contentLength)
                {
                    int want = (int)Math.Min(buf.Length, contentLength - got);
                    int n = await ns.ReadAsync(buf.AsMemory(0, want), _cts.Token).ConfigureAwait(false);
                    if (n <= 0) throw new IOException("incomplete");
                    await fs.WriteAsync(buf.AsMemory(0, n), _cts.Token).ConfigureAwait(false);
                    got += n;
                    Interlocked.Add(ref _recvBytes, n);
                    Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
                }
            }
            File.Move(tmp, dest);
            ok = true;
            Interlocked.Increment(ref _recvFiles);
            await Reply(ns, 200, "OK", "text/plain", "ok", _cts.Token).ConfigureAwait(false);
        }
        catch
        {
            if (!ok) { try { File.Delete(tmp); } catch { } }
            // an unfinished upload no longer counts as expected data
            Interlocked.Add(ref _recvExpected, -(contentLength - got));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            Interlocked.Decrement(ref Connections);
        }
    }

    private string ReceiveHtml()
    {
        string host = WebUtility.HtmlEncode(Environment.MachineName);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\">");
        sb.Append("<meta name=\"color-scheme\" content=\"dark light\"><title>LiteIsland</title><style>");
        sb.Append(":root{--bg:#050507;--card:#141418;--line:#25252b;--text:#f5f5f7;--dim:#9a9aa6;--acc:#38bdf8;--on:#04121c}");
        sb.Append("@media(prefers-color-scheme:light){:root{--bg:#f2f2f6;--card:#fff;--line:#e2e2e8;--text:#17171b;--dim:#6b6b75;--on:#fff;--acc:#0a84ff}}");
        sb.Append("*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;-webkit-font-smoothing:antialiased;padding:max(18px,env(safe-area-inset-top)) 18px 40px}");
        sb.Append(".w{max-width:520px;margin:0 auto}.top{display:flex;align-items:center;gap:10px;margin:6px 2px 22px}");
        sb.Append(".logo{width:34px;height:34px;border-radius:10px;background:#000;display:grid;place-items:center;border:1px solid var(--line)}.logo i{display:block;width:16px;height:9px;border-radius:5px;background:#fff}");
        sb.Append(".top b{font-size:17px;letter-spacing:-.02em}.top span{display:block;color:var(--dim);font-size:12.5px}");
        sb.Append("h1{font-size:23px;margin:0 0 4px;letter-spacing:-.02em}p.s{color:var(--dim);margin:0 0 18px;font-size:14.5px}");
        sb.Append(".btn{display:flex;align-items:center;justify-content:center;gap:8px;background:var(--acc);color:var(--on);font-weight:700;font-size:17px;border-radius:16px;padding:17px;border:0;width:100%;font-family:inherit}");
        sb.Append(".btn:active{transform:scale(.98)}.btn svg{width:20px;height:20px;stroke:currentColor;fill:none;stroke-width:2.4;stroke-linecap:round;stroke-linejoin:round}");
        sb.Append(".row{background:var(--card);border:1px solid var(--line);border-radius:18px;padding:13px 15px;margin-top:10px}");
        sb.Append(".nm{font-weight:600;font-size:15px;word-break:break-word}.st{display:flex;justify-content:space-between;color:var(--dim);font-size:13px;margin-top:4px}");
        sb.Append(".bar{height:4px;border-radius:2px;background:var(--line);margin-top:9px;overflow:hidden}.bar i{display:block;height:100%;width:0;background:var(--acc);border-radius:2px;transition:width .15s}");
        sb.Append(".ok{color:#30d158}.bad{color:#ff453a}.note{color:var(--dim);font-size:12.5px;text-align:center;margin-top:24px;line-height:1.5}input{display:none}");
        sb.Append("</style></head><body><div class=\"w\"><div class=\"top\"><div class=\"logo\"><i></i></div><div><b>LiteIsland</b><span>Send to ").Append(host).Append("</span></div></div>");
        sb.Append("<h1>Send files to your PC</h1><p class=\"s\">Choose photos, videos or any files. They go straight to your computer.</p>");
        sb.Append("<label class=\"btn\" for=\"f\"><svg viewBox=\"0 0 24 24\"><path d=\"M12 20V9M7 13.5l5-5 5 5M5 4h14\"/></svg>Add files</label>");
        sb.Append("<input id=\"f\" type=\"file\" multiple><div id=\"l\"></div>");
        sb.Append("<p class=\"note\">Sent directly over Wi-Fi.<br>Nothing is uploaded to the internet.</p></div><script>");
        sb.Append("var inp=document.getElementById('f'),list=document.getElementById('l');");
        sb.Append("function sz(b){var u=['B','KB','MB','GB'],i=0;while(b>=1024&&i<3){b/=1024;i++}return (i?b.toFixed(b>=100?0:1):b)+' '+u[i]}");
        sb.Append("function add(f){var d=document.createElement('div');d.className='row';d.innerHTML='<div class=\"nm\"></div><div class=\"st\"><span class=\"s1\"></span><span>'+sz(f.size)+'</span></div><div class=\"bar\"><i></i></div>';");
        sb.Append("d.querySelector('.nm').textContent=f.name;d.querySelector('.s1').textContent='Waiting';list.insertBefore(d,list.firstChild);return{s:d.querySelector('.s1'),b:d.querySelector('i')}}");
        sb.Append("function up(f,r,done){r.s.textContent='Sending';var x=new XMLHttpRequest();x.open('POST','up');x.setRequestHeader('X-Name',encodeURIComponent(f.name));");
        sb.Append("x.upload.onprogress=function(e){if(e.lengthComputable)r.b.style.width=(e.loaded/e.total*100)+'%'};");
        sb.Append("x.onload=function(){if(x.status==200){r.b.style.width='100%';r.s.textContent='Sent to PC';r.s.className='s1 ok'}else{r.s.textContent='Failed';r.s.className='s1 bad'}done()};");
        sb.Append("x.onerror=function(){r.s.textContent='Failed';r.s.className='s1 bad';done()};x.send(f)}");
        sb.Append("inp.onchange=function(){var fs=Array.prototype.slice.call(inp.files),rows=fs.map(add),i=0;inp.value='';(function n(){if(i<fs.length){var k=i++;up(fs[k],rows[k],n)}})()};");
        sb.Append("</script></body></html>");
        return sb.ToString();
    }

    private static async Task Reply(NetworkStream ns, int code, string text, string type, string body, CancellationToken ct, bool headOnly = false)
    {
        byte[] b = Encoding.UTF8.GetBytes(body);
        string h = "HTTP/1.1 " + code + " " + text + "\r\nContent-Type: " + type + "\r\nContent-Length: " + b.Length +
                   "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(h), ct).ConfigureAwait(false);
        if (!headOnly && b.Length > 0) await ns.WriteAsync(b, ct).ConfigureAwait(false);
    }

    private async Task SendFile(NetworkStream ns, Item it, string? range, bool headOnly, bool inline)
    {
        FileStream fs;
        try { fs = new FileStream(it.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous); }
        catch { await Reply(ns, 404, "Not Found", "text/plain", "The file is not available any more.", _cts.Token).ConfigureAwait(false); return; }

        using (fs)
        {
            long size = fs.Length, start = 0, stop = size - 1;
            bool isRange = false;
            if (range != null && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                string r = range.Substring(6).Split(',')[0].Trim();
                int dash = r.IndexOf('-');
                bool ok = dash >= 0;
                if (ok)
                {
                    string a = r.Substring(0, dash), b = r.Substring(dash + 1);
                    if (a.Length == 0 && long.TryParse(b, out long suffix) && suffix > 0) { start = Math.Max(0, size - suffix); stop = size - 1; }
                    else if (long.TryParse(a, out long s0))
                    {
                        start = s0;
                        stop = b.Length > 0 && long.TryParse(b, out long e0) ? Math.Min(e0, size - 1) : size - 1;
                    }
                    else ok = false;
                }
                if (!ok || start >= size || stop < start)
                {
                    string h416 = "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */" + size + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await ns.WriteAsync(Encoding.ASCII.GetBytes(h416), _cts.Token).ConfigureAwait(false);
                    return;
                }
                isRange = true;
            }
            long count = stop - start + 1;
            string ascii = new string(it.Name.Select(c => c < 32 || c > 126 || c == '"' || c == '\\' || c == '%' ? '_' : c).ToArray());
            var sb = new StringBuilder();
            sb.Append(isRange ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            string ctype = inline ? ImageType(it.Name) : "";
            sb.Append("Content-Type: ").Append(ctype.Length > 0 ? ctype : "application/octet-stream").Append("\r\n");
            sb.Append("Content-Length: ").Append(count).Append("\r\n");
            if (isRange) sb.Append("Content-Range: bytes ").Append(start).Append('-').Append(stop).Append('/').Append(size).Append("\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            if (ctype.Length > 0) sb.Append("Content-Disposition: inline\r\n");
            else sb.Append("Content-Disposition: attachment; filename=\"").Append(ascii).Append("\"; filename*=UTF-8''").Append(Uri.EscapeDataString(Path.GetFileName(it.Name))).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\nConnection: close\r\n\r\n");
            await ns.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), _cts.Token).ConfigureAwait(false);
            if (headOnly) return;

            fs.Seek(start, SeekOrigin.Begin);
            byte[] buf = ArrayPool<byte>.Shared.Rent(256 * 1024);
            try
            {
                long left = count;
                while (left > 0)
                {
                    int want = (int)Math.Min(buf.Length, left);
                    int n = await fs.ReadAsync(buf.AsMemory(0, want), _cts.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    await ns.WriteAsync(buf.AsMemory(0, n), _cts.Token).ConfigureAwait(false);
                    left -= n;
                    Interlocked.Add(ref _sent, n);
                    Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }
    }

    private static string ImageType(string name)
    {
        string e = Path.GetExtension(name).ToLowerInvariant();
        return e switch
        {
            ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif",
            ".webp" => "image/webp", ".bmp" => "image/bmp", _ => ""
        };
    }

    private string IndexHtml()
    {
        string host = WebUtility.HtmlEncode(Environment.MachineName);
        bool one = _items.Count == 1;
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\">");
        sb.Append("<meta name=\"color-scheme\" content=\"dark light\"><title>LiteIsland</title><style>");
        sb.Append(":root{--bg:#050507;--card:#141418;--line:#25252b;--text:#f5f5f7;--dim:#9a9aa6;--acc:#38bdf8;--on:#04121c}");
        sb.Append("@media(prefers-color-scheme:light){:root{--bg:#f2f2f6;--card:#fff;--line:#e2e2e8;--text:#17171b;--dim:#6b6b75;--on:#fff;--acc:#0a84ff}}");
        sb.Append("*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;-webkit-font-smoothing:antialiased;padding:max(18px,env(safe-area-inset-top)) 18px 40px}");
        sb.Append(".w{max-width:520px;margin:0 auto}.top{display:flex;align-items:center;gap:10px;margin:6px 2px 22px}");
        sb.Append(".logo{width:34px;height:34px;border-radius:10px;background:#000;display:grid;place-items:center;border:1px solid var(--line)}");
        sb.Append(".logo i{display:block;width:16px;height:9px;border-radius:5px;background:#fff}");
        sb.Append(".top b{font-size:17px;letter-spacing:-.02em}.top span{display:block;color:var(--dim);font-size:12.5px}");
        sb.Append("h1{font-size:23px;margin:0 0 4px;letter-spacing:-.02em}p.s{color:var(--dim);margin:0 0 18px;font-size:14.5px}");
        sb.Append(".card{background:var(--card);border:1px solid var(--line);border-radius:22px;padding:20px}");
        sb.Append(".prev{width:100%;max-height:300px;object-fit:contain;border-radius:14px;margin-bottom:16px;background:#0002}");
        sb.Append(".ext{width:54px;height:54px;border-radius:16px;background:linear-gradient(135deg,var(--acc),#818cf8);color:var(--on);display:grid;place-items:center;font-weight:800;font-size:13px;letter-spacing:.02em;flex:none;text-transform:uppercase}");
        sb.Append(".row{display:flex;gap:14px;align-items:center}.nm{font-weight:600;font-size:16px;word-break:break-word;line-height:1.3}.sz{color:var(--dim);font-size:13.5px;margin-top:3px}");
        sb.Append(".btn{display:flex;align-items:center;justify-content:center;gap:8px;background:var(--acc);color:var(--on);font-weight:700;font-size:17px;border-radius:16px;padding:16px;text-decoration:none;margin-top:18px;border:0;width:100%;font-family:inherit}");
        sb.Append(".btn:active{transform:scale(.98)}.btn svg{width:20px;height:20px;stroke:currentColor;fill:none;stroke-width:2.4;stroke-linecap:round;stroke-linejoin:round}");
        sb.Append(".list .card{display:flex;gap:12px;align-items:center;padding:14px 16px;margin-bottom:10px}.list .ext{width:44px;height:44px;border-radius:13px;font-size:11px}");
        sb.Append(".list .grow{flex:1;min-width:0}.list .nm{font-size:15px}.mini{background:var(--acc);color:var(--on);border-radius:12px;padding:10px 14px;font-weight:700;font-size:14px;text-decoration:none;flex:none}");
        sb.Append(".note{color:var(--dim);font-size:12.5px;text-align:center;margin-top:22px;line-height:1.5}");
        sb.Append("</style></head><body><div class=\"w\"><div class=\"top\"><div class=\"logo\"><i></i></div><div><b>LiteIsland</b><span>From ").Append(host).Append("</span></div></div>");
        const string dl = "<svg viewBox=\"0 0 24 24\"><path d=\"M12 4v11M7 10.5l5 5 5-5M5 20h14\"/></svg>";
        if (one)
        {
            var it = _items[0];
            string href = "f/0/" + Uri.EscapeDataString(Path.GetFileName(it.Name));
            sb.Append("<h1>Your file is ready</h1><p class=\"s\">Tap the button to save it on this phone.</p><div class=\"card\">");
            if (ImageType(it.Name).Length > 0) sb.Append("<img class=\"prev\" alt=\"\" src=\"").Append(href).Append("?inline=1\">");
            sb.Append("<div class=\"row\"><div class=\"ext\">").Append(WebUtility.HtmlEncode(ExtLabel(it.Name))).Append("</div><div><div class=\"nm\">")
              .Append(WebUtility.HtmlEncode(it.Name)).Append("</div><div class=\"sz\">").Append(Size(it.Size)).Append("</div></div></div>");
            sb.Append("<a class=\"btn\" href=\"").Append(href).Append("\" download>").Append(dl).Append("Download</a></div>");
        }
        else
        {
            sb.Append("<h1>").Append(_items.Count).Append(" files</h1><p class=\"s\">").Append(Size(Total)).Append(" in total. Tap a file to save it.</p><div class=\"list\">");
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                sb.Append("<div class=\"card\"><div class=\"ext\">").Append(WebUtility.HtmlEncode(ExtLabel(it.Name))).Append("</div><div class=\"grow\"><div class=\"nm\">")
                  .Append(WebUtility.HtmlEncode(it.Name)).Append("</div><div class=\"sz\">").Append(Size(it.Size)).Append("</div></div><a class=\"mini\" href=\"f/").Append(i).Append('/')
                  .Append(Uri.EscapeDataString(Path.GetFileName(it.Name))).Append("\" download>Save</a></div>");
            }
            sb.Append("</div><button class=\"btn\" id=\"all\">").Append(dl).Append("Download all</button>");
            sb.Append("<script>document.getElementById('all').onclick=function(){var a=document.querySelectorAll('a.mini'),i=0;(function n(){if(i<a.length){a[i++].click();setTimeout(n,900)}})()}</script>");
        }
        sb.Append("<p class=\"note\">Sent directly from your PC over Wi-Fi.<br>Nothing is uploaded to the internet.</p></div></body></html>");
        return sb.ToString();
    }

    private static string ExtLabel(string name)
    {
        string e = Path.GetExtension(name).TrimStart('.');
        return e.Length == 0 ? "FILE" : (e.Length > 4 ? e.Substring(0, 4) : e);
    }

    public static string Size(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return (i == 0 ? v.ToString("0") : v.ToString(v >= 100 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture)) + " " + u[i];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
    }
}

internal static class ShareQr
{
    public static BitmapSource Make(string text)
    {
        var w = new ZXing.BarcodeWriterPixelData
        {
            Format = ZXing.BarcodeFormat.QR_CODE,
            Options = new ZXing.Common.EncodingOptions { Width = 464, Height = 464, Margin = 0, PureBarcode = true }
        };
        var px = w.Write(text);
        var bmp = BitmapSource.Create(px.Width, px.Height, 96, 96, PixelFormats.Bgra32, null, px.Pixels, px.Width * 4);
        bmp.Freeze();
        return bmp;
    }
}
