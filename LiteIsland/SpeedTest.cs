using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LiteIsland;

/// <summary>
/// Internet speed test (ping, download, upload) against Cloudflare's public speed test servers.
/// It really transfers data for a few seconds, so it only runs when the user presses the button.
/// </summary>
public sealed class SpeedTest
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const string Down = "https://speed.cloudflare.com/__down?bytes=";
    private const string Up = "https://speed.cloudflare.com/__up";

    public enum Phase { Ping, Download, Upload, Done, Failed }

    public sealed record Progress(Phase Phase, double PingMs, double DownMbps, double UpMbps);

    /// <summary>Runs the test. onProgress is called from background threads, often.</summary>
    public async Task<Progress> RunAsync(Action<Progress> onProgress, CancellationToken ct)
    {
        double ping = 0, down = 0, up = 0;
        try
        {
            // ----- Ping: best of 5 small requests (the first one also opens the connection) -----
            onProgress(new Progress(Phase.Ping, 0, 0, 0));
            double best = double.MaxValue;
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                using var resp = await Http.GetAsync(Down + "0", HttpCompletionOption.ResponseContentRead, ct);
                resp.EnsureSuccessStatusCode();
                sw.Stop();
                if (i > 0) best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            ping = best == double.MaxValue ? 0 : best;

            // ----- Download: 4 parallel streams for up to 6 seconds -----
            onProgress(new Progress(Phase.Download, ping, 0, 0));
            long total = 0;
            var swD = Stopwatch.StartNew();
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                Task[] jobs = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
                {
                    try
                    {
                        using var resp = await Http.GetAsync(Down + "25000000", HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                        var buf = new byte[81920];
                        int n;
                        while ((n = await stream.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                        {
                            long t = Interlocked.Add(ref total, n);
                            double sec = swD.Elapsed.TotalSeconds;
                            if (sec > 0.4) onProgress(new Progress(Phase.Download, ping, t * 8.0 / sec / 1e6, 0));
                        }
                    }
                    catch (OperationCanceledException) { }
                })).ToArray();

                try { await Task.WhenAll(jobs); } catch (OperationCanceledException) { }
            }
            ct.ThrowIfCancellationRequested();
            swD.Stop();
            down = swD.Elapsed.TotalSeconds > 0 ? Interlocked.Read(ref total) * 8.0 / swD.Elapsed.TotalSeconds / 1e6 : 0;

            // ----- Upload: 3 parallel posts of 4 MB, a few rounds, up to 6 seconds -----
            onProgress(new Progress(Phase.Upload, ping, down, 0));
            long sent = 0;
            var swU = Stopwatch.StartNew();
            var payload = new byte[4 * 1024 * 1024];
            new Random(7).NextBytes(payload);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                Task[] jobs = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
                {
                    try
                    {
                        while (!cts.IsCancellationRequested && swU.Elapsed.TotalSeconds < 5.5)
                        {
                            using var content = new ByteArrayContent(payload);
                            using var resp = await Http.PostAsync(Up, content, cts.Token);
                            resp.EnsureSuccessStatusCode();
                            long t = Interlocked.Add(ref sent, payload.Length);
                            double sec = swU.Elapsed.TotalSeconds;
                            if (sec > 0.4) onProgress(new Progress(Phase.Upload, ping, down, t * 8.0 / sec / 1e6));
                        }
                    }
                    catch (OperationCanceledException) { }
                })).ToArray();

                try { await Task.WhenAll(jobs); } catch (OperationCanceledException) { }
            }
            ct.ThrowIfCancellationRequested();
            swU.Stop();
            up = swU.Elapsed.TotalSeconds > 0 ? Interlocked.Read(ref sent) * 8.0 / swU.Elapsed.TotalSeconds / 1e6 : 0;

            var done = new Progress(down > 0 ? Phase.Done : Phase.Failed, ping, down, up);
            onProgress(done);
            return done;
        }
        catch
        {
            var failed = new Progress(Phase.Failed, ping, down, up);
            onProgress(failed);
            return failed;
        }
    }
}
