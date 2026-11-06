using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LiteIsland;

/// <summary>
/// Helper process ("LiteIsland.exe --player"): plays the songs of a playlist with Windows' own media player. It shows up
/// like any music app (the island's music page, the keyboard media keys, the lock screen). It lives in its own process so
/// the island itself stays small. Commands come on stdin, events go out on stdout.
/// </summary>
internal static class PlayerWorker
{
    private static readonly object Lk = new();
    private static TextWriter? _out;

    private static void Emit(string kind, string val)
    {
        lock (Lk)
        {
            try { _out?.WriteLine(kind + "|" + val.Replace('\r', ' ').Replace('\n', ' ')); _out?.Flush(); } catch { }
        }
    }

    public static int Run(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        _out = stdout;
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        MediaPlayer? player = null;
        try
        {
            player = new MediaPlayer { AutoPlay = false, Volume = 1.0 };
            player.CommandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            player.CommandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            player.CommandManager.NextReceived += (_, a) => { a.Handled = true; Emit("N", ""); };
            player.CommandManager.PreviousReceived += (_, a) => { a.Handled = true; Emit("V", ""); };
            player.MediaEnded += (_, _) => Emit("E", "");
            player.MediaFailed += (_, a) => Emit("F", a.ErrorMessage ?? "Could not play this song");
            player.PlaybackSession.PlaybackStateChanged += (s, _) => Emit("S", s.PlaybackState.ToString());
        }
        catch (Exception ex)
        {
            Emit("F", "Player: " + ex.Message);
            return 2;
        }

        // position every second (only while something plays)
        var timer = new Timer(_ =>
        {
            try
            {
                var s = player.PlaybackSession;
                if (s.PlaybackState == MediaPlaybackState.Playing)
                    Emit("T", s.Position.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "|" + s.NaturalDuration.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture));
            }
            catch { }
        }, null, 1000, 1000);

        MemoryGuard.Lean(0, 6);
        Emit("R", "ready");
        string? line;
        while ((line = stdin.ReadLine()) != null) // ends when the island closes: the helper ends with it
        {
            try
            {
                string[] p = line.Split('|');
                switch (p[0])
                {
                    case "LOAD" when p.Length >= 5:
                    {
                        StorageFile f = StorageFile.GetFileFromPathAsync(p[1]).AsTask().GetAwaiter().GetResult();
                        var item = new MediaPlaybackItem(MediaSource.CreateFromStorageFile(f));
                        var props = item.GetDisplayProperties();
                        props.Type = MediaPlaybackType.Music;
                        props.MusicProperties.Title = p[2];
                        props.MusicProperties.Artist = p[3];
                        if (p[4].Length > 3 && File.Exists(p[4]))
                        {
                            try { props.Thumbnail = RandomAccessStreamReference.CreateFromFile(StorageFile.GetFileFromPathAsync(p[4]).AsTask().GetAwaiter().GetResult()); } catch { }
                        }
                        else if (p[4].StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            try { props.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(p[4])); } catch { }
                        }
                        item.ApplyDisplayProperties(props);
                        player.Source = item;
                        player.Play();
                        break;
                    }
                    case "PLAY": player.Play(); break;
                    case "PAUSE": player.Pause(); break;
                    case "TOGGLE":
                        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) player.Pause(); else player.Play();
                        break;
                    case "SEEK" when p.Length >= 2:
                        if (double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double sec))
                            player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, sec));
                        break;
                    case "STOP":
                        player.Pause();
                        player.Source = null;
                        break;
                    case "QUIT":
                        timer.Dispose();
                        player.Dispose();
                        return 0;
                }
            }
            catch (Exception ex) { Emit("F", ex.Message); }
        }
        timer.Dispose();
        try { player.Dispose(); } catch { }
        return 0;
    }
}
