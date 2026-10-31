using System;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace LiteIsland;

/// <summary>
/// Reads the current song, play state, album art and progress from Windows' built-in media API.
/// No polling, only events, so it uses no CPU when idle.
/// Works with Spotify, YouTube (browser), VLC and most other players.
/// </summary>
public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    // Cached so the album art of one song is not read again and again
    private string _artKey = "";
    private byte[]? _artBytes;

    /// <summary>title, artist, isPlaying, albumArt (same array reference for the same song)</summary>
    public event Action<string, string, bool, byte[]?>? Changed;

    /// <summary>Shuffle / repeat state of the player changed (or the player does not offer them).</summary>
    public event Action? ModesChanged;
    public bool ShuffleSupported { get; private set; }
    public bool ShuffleOn { get; private set; }
    public bool RepeatSupported { get; private set; }
    /// <summary>0 = off, 1 = repeat the song, 2 = repeat the list</summary>
    public int RepeatMode { get; private set; }

    private bool _lastPlaying;
    private int _errTries;
    private System.Threading.Timer? _watch;

    /// <summary>Position or length changed (after a seek, for example)</summary>
    public event Action? TimelineChanged;

    /// <summary>Friendly name of the app that plays the current song (Spotify, Apple Music, ...).</summary>
    public string CurrentApp
    {
        get
        {
            try { return Friendly(_session?.SourceAppUserModelId); }
            catch { return ""; }
        }
    }

    private static string Friendly(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        string l = id.ToLowerInvariant();
        if (l.Contains("spotify")) return "Spotify";
        if (l.Contains("applemusic") || l.Contains("itunes") || l.Contains("apple")) return "Apple Music";
        if (l.Contains("msedge")) return "Edge";
        if (l.Contains("chrome")) return "Chrome";
        if (l.Contains("firefox")) return "Firefox";
        if (l.Contains("brave")) return "Brave";
        if (l.Contains("vlc")) return "VLC";
        if (l.Contains("zune") || l.Contains("media.player") || l.Contains("mediaplayer")) return "Media Player";
        if (l.Contains("youtube")) return "YouTube Music";
        string n = id;
        int bang = n.IndexOf('!');
        if (bang > 0) n = n.Substring(0, bang);
        int dot = n.LastIndexOf('.');
        if (dot > 0 && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, dot);
        return n;
    }

    public async Task InitAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => Attach();
        Attach();

        // Safety net: when a headset is plugged in or out, players and Windows switch sessions and states in
        // ways that do not always raise an event. Every 3 seconds the real state is compared with what is shown.
        _watch = new System.Threading.Timer(_ =>
        {
            try
            {
                var cur = _manager?.GetCurrentSession();
                if (!ReferenceEquals(cur, _session)) { Attach(); return; }
                if (_session == null) return;
                bool now = _session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                if (now != _lastPlaying) _ = RefreshAsync();
            }
            catch { }
        }, null, 3000, 3000);
    }

    private void Attach()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        _session = _manager?.GetCurrentSession();

        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
        }

        _ = RefreshAsync();
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs a) => _ = RefreshAsync();
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs a) => _ = RefreshAsync();
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs a) => TimelineChanged?.Invoke();

    // Newest refresh wins: an older, slower refresh must never overwrite a newer one
    private int _gen;
    private int _artTries;
    private const int MaxArtTries = 12;

    private async Task RefreshAsync()
    {
        int gen = ++_gen;
        var s = _session;
        if (s == null)
        {
            _artKey = "";
            _artBytes = null;
            _artTries = 0;
            _lastPlaying = false;
            SetModes(false, false, false, 0);
            Changed?.Invoke("", "", false, null);
            return;
        }

        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            if (gen != _gen) return;

            var info = s.GetPlaybackInfo();
            bool playing = info.PlaybackStatus ==
                           GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            string title = props.Title ?? "";
            string artist = props.Artist ?? "";
            bool hasSong = title.Length > 0;

            // New song: forget the old cover right away (so it never shows the wrong one)
            string key = title + "|" + artist;
            if (key != _artKey)
            {
                _artKey = key;
                _artBytes = null;
                _artTries = 0;
            }

            // Many players announce the new title first and the cover a moment later.
            // So a missing cover is NOT remembered: it is tried again (up to MaxArtTries times).
            if (_artBytes == null && hasSong && _artTries < MaxArtTries)
            {
                _artTries++;
                var bytes = await ReadThumbnailAsync(props.Thumbnail);
                if (gen != _gen) return;
                _artBytes = bytes;
            }

            _errTries = 0;
            _lastPlaying = playing;
            SetModes(info.Controls.IsShuffleEnabled, info.IsShuffleActive == true,
                     info.Controls.IsRepeatEnabled, info.AutoRepeatMode == MediaPlaybackAutoRepeatMode.Track ? 1 : info.AutoRepeatMode == MediaPlaybackAutoRepeatMode.List ? 2 : 0);
            Changed?.Invoke(title, artist, playing, _artBytes);

            // Still no cover: look again shortly (stops by itself when a newer event arrives)
            if (_artBytes == null && hasSong && _artTries < MaxArtTries)
            {
                await Task.Delay(700);
                if (gen == _gen) _ = RefreshAsync();
            }
        }
        catch
        {
            // The session changed in the middle of reading (headset plugged in, player closed ...): look again shortly
            if (_errTries++ < 4)
            {
                await Task.Delay(600);
                if (gen == _gen) _ = RefreshAsync();
            }
        }
    }

    private void SetModes(bool shufSup, bool shuf, bool repSup, int rep)
    {
        if (shufSup == ShuffleSupported && shuf == ShuffleOn && repSup == RepeatSupported && rep == RepeatMode) return;
        ShuffleSupported = shufSup;
        ShuffleOn = shuf;
        RepeatSupported = repSup;
        RepeatMode = rep;
        ModesChanged?.Invoke();
    }

    public async Task ShuffleAsync()
    {
        if (_session != null && ShuffleSupported) await _session.TryChangeShuffleActiveAsync(!ShuffleOn);
    }

    /// <summary>off -> repeat list -> repeat song -> off</summary>
    public async Task RepeatAsync()
    {
        if (_session == null || !RepeatSupported) return;
        var next = RepeatMode == 0 ? MediaPlaybackAutoRepeatMode.List : RepeatMode == 2 ? MediaPlaybackAutoRepeatMode.Track : MediaPlaybackAutoRepeatMode.None;
        await _session.TryChangeAutoRepeatModeAsync(next);
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? thumb)
    {
        if (thumb == null) return null;
        try
        {
            using IRandomAccessStreamWithContentType stream = await thumb.OpenReadAsync();
            uint size = (uint)stream.Size;
            if (size == 0 || size > 8 * 1024 * 1024) return null;

            var buffer = new byte[size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync(size);
            reader.ReadBytes(buffer);
            return buffer;
        }
        catch
        {
            return null;
        }
    }

    // ---------- Progress / seek ----------

    /// <summary>Current position and total length in seconds. Duration 0 means seeking is not supported.</summary>
    public (double Position, double Duration) GetProgress(bool playing)
    {
        var s = _session;
        if (s == null) return (0, 0);

        try
        {
            var t = s.GetTimelineProperties();
            double dur = (t.EndTime - t.StartTime).TotalSeconds;
            if (dur <= 0) return (0, 0);

            double pos = (t.Position - t.StartTime).TotalSeconds;

            // Windows only updates the position now and then, so add the time since the last update
            if (playing && t.LastUpdatedTime.Year > 2000)
            {
                double delta = (DateTimeOffset.Now - t.LastUpdatedTime).TotalSeconds;
                if (delta > 0 && delta < dur) pos += delta;
            }

            return (Math.Clamp(pos, 0, dur), dur);
        }
        catch
        {
            return (0, 0);
        }
    }

    public async Task SeekAsync(double seconds)
    {
        var s = _session;
        if (s == null) return;

        try
        {
            var t = s.GetTimelineProperties();
            long ticks = t.StartTime.Ticks + (long)(seconds * TimeSpan.TicksPerSecond);
            await s.TryChangePlaybackPositionAsync(ticks);
        }
        catch
        {
            // This app does not support seeking
        }
    }

    // ---------- Buttons ----------

    public async Task TogglePlayPauseAsync()
    {
        if (_session != null) await _session.TryTogglePlayPauseAsync();
    }

    public async Task NextAsync()
    {
        if (_session != null) await _session.TrySkipNextAsync();
    }

    public async Task PreviousAsync()
    {
        if (_session != null) await _session.TrySkipPreviousAsync();
    }
}
