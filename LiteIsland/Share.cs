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

    /// <summary>The address the phone has to open (the QR code contains it).</summary>
    public string Url => "http://" + Ip + ":" + Port + "/" + _token + "/" +
                         (_items.Count == 1 ? "f/0/" + Uri.EscapeDataString(_items[0].Name) : "");

    public bool Idle => Connections == 0 && Environment.TickCount64 - Interlocked.Read(ref _lastActivity) > 30 * 60 * 1000;

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
            if (q >= 0) path = path.Substring(0, q);
            string? range = null;
            foreach (string l in lines.Skip(1))
                if (l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = l.Substring(6).Trim();

            Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
            if (method != "GET" && method != "HEAD") { await Reply(ns, 405, "Method Not Allowed", "text/plain", "", cts.Token).ConfigureAwait(false); return; }

            string prefix = "/" + _token + "/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) { await Reply(ns, 404, "Not Found", "text/plain", "Not found", cts.Token).ConfigureAwait(false); return; }
            string rest = path.Substring(prefix.Length);

            if (rest.Length == 0)
            {
                await Reply(ns, 200, "OK", "text/html; charset=utf-8", IndexHtml(), cts.Token, method == "HEAD").ConfigureAwait(false);
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
                    try { await SendFile(ns, _items[idx], range, method == "HEAD").ConfigureAwait(false); }
                    finally { Interlocked.Decrement(ref Connections); }
                    return;
                }
            }
            await Reply(ns, 404, "Not Found", "text/plain", "Not found", cts.Token).ConfigureAwait(false);
        }
        catch { }
        finally { if (slot) _slots.Release(); Interlocked.Exchange(ref _lastActivity, Environment.TickCount64); }
    }

    private static async Task Reply(NetworkStream ns, int code, string text, string type, string body, CancellationToken ct, bool headOnly = false)
    {
        byte[] b = Encoding.UTF8.GetBytes(body);
        string h = "HTTP/1.1 " + code + " " + text + "\r\nContent-Type: " + type + "\r\nContent-Length: " + b.Length +
                   "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(h), ct).ConfigureAwait(false);
        if (!headOnly && b.Length > 0) await ns.WriteAsync(b, ct).ConfigureAwait(false);
    }

    private async Task SendFile(NetworkStream ns, Item it, string? range, bool headOnly)
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
            sb.Append("Content-Type: application/octet-stream\r\n");
            sb.Append("Content-Length: ").Append(count).Append("\r\n");
            if (isRange) sb.Append("Content-Range: bytes ").Append(start).Append('-').Append(stop).Append('/').Append(size).Append("\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            sb.Append("Content-Disposition: attachment; filename=\"").Append(ascii).Append("\"; filename*=UTF-8''").Append(Uri.EscapeDataString(Path.GetFileName(it.Name))).Append("\r\n");
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

    private string IndexHtml()
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>LiteIsland</title>");
        sb.Append("<style>body{margin:0;background:#0b0b0d;color:#f4f4f6;font-family:-apple-system,Segoe UI,Roboto,sans-serif;padding:20px}");
        sb.Append("h1{font-size:20px;margin:6px 0 4px}p{color:#9a9aa6;margin:0 0 18px;font-size:14px}");
        sb.Append("a{display:flex;justify-content:space-between;gap:12px;align-items:center;background:#1c1d22;border-radius:14px;padding:14px 16px;margin-bottom:10px;color:#fff;text-decoration:none;word-break:break-all}");
        sb.Append("a span{color:#9a9aa6;font-size:13px;white-space:nowrap}</style></head><body>");
        sb.Append("<h1>Files from your PC</h1><p>").Append(_items.Count).Append(" file").Append(_items.Count == 1 ? "" : "s").Append(", ").Append(Size(Total)).Append(". Tap a file to download.</p>");
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            sb.Append("<a href=\"f/").Append(i).Append('/').Append(Uri.EscapeDataString(Path.GetFileName(it.Name))).Append("\"><b style=\"font-weight:500\">")
              .Append(WebUtility.HtmlEncode(it.Name)).Append("</b><span>").Append(Size(it.Size)).Append("</span></a>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
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

/// <summary>The small window with the QR code. Closing it stops sharing.</summary>
internal sealed class ShareWindow : Window
{
    private readonly ShareServer _srv;
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly Grid _bar = new() { Height = 6, Margin = new Thickness(0, 10, 0, 0) };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Brush _good, _dim, _text;
    private long _lastSent;
    private long _lastTime = Environment.TickCount64;
    private double _speed;

    public static ShareWindow? Current;

    public static void Show(IEnumerable<string> paths, bool light, string accent)
    {
        Current?.Close();
        ShareServer srv;
        try { srv = new ShareServer(paths); }
        catch { return; }
        var w = new ShareWindow(srv, light, accent);
        Current = w;
        w.Show();
        w.Activate();
    }

    private ShareWindow(ShareServer srv, bool light, string accent)
    {
        _srv = srv;
        Color acc = Colors.DodgerBlue;
        try { acc = (Color)ColorConverter.ConvertFromString(accent); } catch { }
        Brush B(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
        Brush bg = B(light ? "#F4F4F7" : "#141418");
        _text = B(light ? "#1B1B1F" : "#F4F4F6");
        _dim = B(light ? "#6B6B75" : "#9A9AA6");
        _good = B("#3FD08A");
        Brush panel = B(light ? "#FFFFFF" : "#1F2026");
        var accB = new SolidColorBrush(acc); accB.Freeze();

        Title = "Send to phone";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Background = bg;
        Foreground = _text;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Topmost = true;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + 90;

        var root = new StackPanel { Margin = new Thickness(22, 20, 22, 20) };
        var title = new TextBlock { Text = "Send to phone", FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = _text, HorizontalAlignment = HorizontalAlignment.Center };
        root.Children.Add(title);
        string what = srv.Items.Count == 1 ? srv.Items[0].Name : srv.Items.Count + " files";
        var name = new TextBlock { Text = what, FontSize = 13, Foreground = _text, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), MaxWidth = 300 };
        root.Children.Add(name);
        root.Children.Add(new TextBlock { Text = ShareServer.Size(srv.Total), FontSize = 12, Foreground = _dim, HorizontalAlignment = HorizontalAlignment.Center });

        var qr = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(16), Padding = new Thickness(12), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        var img = new Image { Width = 232, Height = 232, SnapsToDevicePixels = true };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        img.Source = MakeQr(srv.Url);
        qr.Child = img;
        root.Children.Add(qr);

        _status.Foreground = _dim;
        _status.Text = "Scan with your phone camera";
        _status.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(_status);

        var track = new Border { Background = panel, CornerRadius = new CornerRadius(3) };
        var fill = new Border { Background = accB, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _bar.Children.Add(track);
        _bar.Children.Add(fill);
        root.Children.Add(_bar);

        var hint = new TextBlock { Text = "Your phone and this PC must be on the same Wi-Fi. If the phone cannot open it, allow LiteIsland in the Windows Firewall question.", FontSize = 11, Foreground = _dim, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        root.Children.Add(hint);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        row.Children.Add(MakeBtn("Copy link", panel, () => { try { Clipboard.SetText(srv.Url); _status.Text = "Link copied"; } catch { } }));
        row.Children.Add(MakeBtn("Stop sharing", accB, Close, true, acc));
        root.Children.Add(row);
        Content = root;

        _tick.Tick += (_, _) => Update(fill);
        _tick.Start();
        Closed += (_, _) =>
        {
            _tick.Stop();
            _srv.Dispose();
            if (ReferenceEquals(Current, this)) Current = null;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private Border MakeBtn(string text, Brush bg, Action click, bool primary = false, Color? accent = null)
    {
        Brush fg = _text;
        if (primary && accent != null)
        {
            double lum = (0.299 * accent.Value.R + 0.587 * accent.Value.G + 0.114 * accent.Value.B) / 255.0;
            fg = lum > 0.62 ? Brushes.Black : Brushes.White;
        }
        var b = new Border { Background = bg, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(4, 0, 4, 0), Cursor = Cursors.Hand };
        b.Child = new TextBlock { Text = text, Foreground = fg, FontWeight = FontWeights.SemiBold };
        b.MouseEnter += (_, _) => b.Opacity = 0.85;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    private void Update(Border fill)
    {
        long sent = Math.Min(_srv.Sent, long.MaxValue);
        long now = Environment.TickCount64;
        double dt = (now - _lastTime) / 1000.0;
        if (dt > 0.25)
        {
            double inst = (sent - _lastSent) / dt;
            _speed = _speed <= 0 ? inst : _speed * 0.7 + inst * 0.3;
            _lastSent = sent;
            _lastTime = now;
        }
        if (sent <= 0 && _srv.Connections == 0) { _status.Text = "Scan with your phone camera"; _status.Foreground = _dim; fill.Width = 0; }
        else
        {
            double frac = _srv.Total > 0 ? Math.Min(1, (double)sent / _srv.Total) : 0;
            fill.Width = Math.Max(0, (_bar.ActualWidth) * frac);
            if (frac >= 0.999 && _srv.Connections == 0) { _status.Text = "Sent  " + ShareServer.Size(_srv.Total); _status.Foreground = _good; }
            else
            {
                _status.Text = "Sending  " + ShareServer.Size(Math.Min(sent, _srv.Total)) + " of " + ShareServer.Size(_srv.Total) + (_speed > 1024 ? "   ·   " + ShareServer.Size((long)_speed) + "/s" : "");
                _status.Foreground = _text;
            }
        }
        if (_srv.Idle) Close();
    }

    private static BitmapSource MakeQr(string text)
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
