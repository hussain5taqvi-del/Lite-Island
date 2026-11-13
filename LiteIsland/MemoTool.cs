using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LiteIsland;

/// <summary>Records the microphone to a WAV file (44.1 kHz, mono, 16 bit). Raw winmm, nothing extra to load.</summary>
internal sealed class WavRecorder
{
    private const int Bufs = 6, Bytes = 8820; // 100 ms each
    private IntPtr _h;
    private readonly IntPtr[] _hdr = new IntPtr[Bufs];
    private readonly IntPtr[] _dat = new IntPtr[Bufs];
    private Thread? _th;
    private volatile bool _run;
    private FileStream? _fs;
    private long _bytes;
    private volatile float _level;

    public float Level => _level;
    public double Seconds => _bytes / 88200.0;

    public int Start(string path)
    {
        var f = MiloWave.Mono16(44100);
        int rc = MiloWave.waveInOpen(out _h, MiloWave.Mapper, ref f, IntPtr.Zero, IntPtr.Zero, 0);
        if (rc != 0) return rc;
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _fs.Write(new byte[44], 0, 44); // header is filled in when recording stops
        for (int i = 0; i < Bufs; i++)
        {
            _hdr[i] = MiloWave.NewHeader(Bytes, out _dat[i]);
            MiloWave.waveInPrepareHeader(_h, _hdr[i], MiloWave.HdrSize);
            MiloWave.waveInAddBuffer(_h, _hdr[i], MiloWave.HdrSize);
        }
        MiloWave.waveInStart(_h);
        _run = true;
        _th = new Thread(Loop) { IsBackground = true, Name = "MemoRec" };
        _th.Start();
        return 0;
    }

    private void Drain(bool requeue)
    {
        for (int i = 0; i < Bufs; i++)
        {
            if (_hdr[i] == IntPtr.Zero) continue;
            if ((((uint)Marshal.ReadInt32(_hdr[i], MiloWave.FlagsOff)) & MiloWave.WHDR_DONE) == 0) continue;
            int rec = Marshal.ReadInt32(_hdr[i], MiloWave.RecOff);
            if (rec > 1)
            {
                var b = new byte[rec];
                Marshal.Copy(_dat[i], b, 0, rec);
                if (requeue) MiloWave.waveInAddBuffer(_h, _hdr[i], MiloWave.HdrSize);
                _fs?.Write(b, 0, rec);
                _bytes += rec;
                double sum = 0;
                int n = rec / 2;
                for (int k = 0; k < n; k++)
                {
                    short v = (short)(b[2 * k] | (b[2 * k + 1] << 8));
                    sum += (double)v * v;
                }
                double rms = n > 0 ? Math.Sqrt(sum / n) / 32768.0 : 0;
                _level = (float)Math.Min(1.0, Math.Sqrt(rms * 5.0));
            }
            else if (requeue) MiloWave.waveInAddBuffer(_h, _hdr[i], MiloWave.HdrSize);
        }
    }

    private void Loop()
    {
        while (_run)
        {
            Drain(true);
            Thread.Sleep(20);
        }
    }

    public void Stop()
    {
        _run = false;
        try { _th?.Join(1500); } catch { }
        if (_h != IntPtr.Zero)
        {
            MiloWave.waveInStop(_h);
            MiloWave.waveInReset(_h); // hands back the buffers (the last, partly filled one too)
            Drain(false);
            for (int i = 0; i < Bufs; i++)
            {
                if (_hdr[i] == IntPtr.Zero) continue;
                MiloWave.waveInUnprepareHeader(_h, _hdr[i], MiloWave.HdrSize);
                Marshal.FreeHGlobal(_dat[i]);
                Marshal.FreeHGlobal(_hdr[i]);
                _hdr[i] = IntPtr.Zero;
            }
            MiloWave.waveInClose(_h);
            _h = IntPtr.Zero;
        }
        if (_fs != null)
        {
            try
            {
                _fs.Seek(0, SeekOrigin.Begin);
                using var w = new BinaryWriter(_fs, System.Text.Encoding.ASCII, true);
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                w.Write((int)(36 + _bytes));
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16);
                w.Write((short)1); w.Write((short)1);
                w.Write(44100); w.Write(88200);
                w.Write((short)2); w.Write((short)16);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                w.Write((int)_bytes);
            }
            catch { }
            try { _fs.Dispose(); } catch { }
            _fs = null;
        }
    }
}

/// <summary>Voice memo: one big record button, a live waveform, and your latest recordings.</summary>
internal sealed class MemoWindow : ToolShell
{
    private const int BarCount = 46;
    private static string Dir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LiteIsland", "Voice Memos");

    private readonly WavRecorder _rec = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly float[] _lv = new float[BarCount];
    private readonly Canvas _wave = new() { Height = 72 };
    private readonly TextBlock _time, _status;
    private readonly Rectangle _dot;
    private readonly StackPanel _list = new();
    private bool _recording;
    private string _file = "";
    private MediaPlayer? _player;
    private string _playing = "";
    private long _tickCount;
    private int _phase;

    public MemoWindow() : base("Voice Memo", 400)
    {
        for (int i = 0; i < BarCount; i++)
        {
            var r = new Rectangle { Width = 4, Height = 4, RadiusX = 2, RadiusY = 2, Fill = Kit.Faint };
            _bars[i] = r;
            _wave.Children.Add(r);
        }
        _wave.SizeChanged += (_, _) => Layout();

        _time = Kit.T("0:00", 40, true);
        _time.HorizontalAlignment = HorizontalAlignment.Center;
        _time.Margin = new Thickness(0, 4, 0, 0);
        _status = Kit.T("Tap to record", 12.5, false, Kit.Dim);
        _status.HorizontalAlignment = HorizontalAlignment.Center;

        // record button: red disc inside a thin ring; becomes a rounded square while recording
        var ring = new Ellipse { Width = 74, Height = 74, StrokeThickness = 3.5, Stroke = Kit.Dim };
        _dot = new Rectangle { Width = 58, Height = 58, RadiusX = 29, RadiusY = 29, Fill = Kit.Hex("#FF453A") };
        var rb = new Grid { Width = 80, Height = 80, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 8), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        rb.Children.Add(ring);
        rb.Children.Add(_dot);
        _dot.HorizontalAlignment = HorizontalAlignment.Center;
        _dot.VerticalAlignment = VerticalAlignment.Center;
        ring.HorizontalAlignment = HorizontalAlignment.Center;
        ring.VerticalAlignment = VerticalAlignment.Center;
        Kit.Press(rb, 0.93);
        rb.MouseLeftButtonUp += (_, e) => { e.Handled = true; Toggle(); };

        var stack = new StackPanel();
        stack.Children.Add(_wave);
        stack.Children.Add(_time);
        stack.Children.Add(rb);
        stack.Children.Add(_status);
        var head = Kit.T("Recordings", 12, true, Kit.Dim);
        head.Margin = new Thickness(6, 20, 0, 6);
        stack.Children.Add(head);
        stack.Children.Add(_list);
        Body.Children.Add(stack);

        _tick.Tick += OnTick;
        Closed += (_, _) =>
        {
            _tick.Stop();
            if (_recording) { try { _rec.Stop(); } catch { } }
            try { _player?.Close(); } catch { }
        };
    }

    protected override void OnShown()
    {
        Layout();
        _tick.Start();
        RenderList(null);
    }

    private void Layout()
    {
        double w = _wave.ActualWidth;
        if (w <= 0) return;
        double step = w / BarCount;
        for (int i = 0; i < BarCount; i++)
        {
            _bars[i].Width = Math.Max(2, step * 0.5);
            Canvas.SetLeft(_bars[i], i * step + step * 0.25);
        }
        Paint();
    }

    private void Paint()
    {
        double h = _wave.Height;
        for (int i = 0; i < BarCount; i++)
        {
            double v = _lv[i];
            double bh = _recording ? 4 + v * (h - 6) : 4 + Math.Max(0, Math.Sin((i + _phase * 0.35) * 0.45)) * 7 * (0.4 + 0.6 * Math.Sin(i * 0.2 + 1));
            bh = Math.Max(4, Math.Min(h, bh));
            _bars[i].Height = bh;
            _bars[i].RadiusX = _bars[i].RadiusY = Math.Min(3, bh / 2);
            Canvas.SetTop(_bars[i], (h - bh) / 2);
        }
    }

    private void OnTick(object? s, EventArgs e)
    {
        _tickCount++;
        if (_recording)
        {
            // shift left, push the newest level (smoothed)
            for (int i = 0; i < BarCount - 1; i++) _lv[i] = _lv[i + 1];
            _lv[BarCount - 1] = _lv[BarCount - 1] * 0.35f + _rec.Level * 0.65f;
            double sec = _rec.Seconds;
            _time.Text = Fmt(sec);
            Paint();
        }
        else if (_tickCount % 2 == 0)
        {
            _phase++;
            Paint(); // a calm moving line while idle
        }
    }

    private static string Fmt(double sec)
    {
        int s = (int)sec;
        return s >= 3600 ? (s / 3600) + ":" + (s % 3600 / 60).ToString("00") + ":" + (s % 60).ToString("00") : (s / 60) + ":" + (s % 60).ToString("00");
    }

    private void Toggle()
    {
        if (_recording) { StopRec(); return; }
        try { _player?.Stop(); _playing = ""; } catch { }
        try { Directory.CreateDirectory(Dir); } catch { }
        _file = System.IO.Path.Combine(Dir, "Memo " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".wav");
        int rc = _rec.Start(_file);
        if (rc != 0)
        {
            _status.Text = "Microphone not available. Check Windows Settings > Privacy > Microphone.";
            _status.TextWrapping = TextWrapping.Wrap;
            _status.TextAlignment = TextAlignment.Center;
            return;
        }
        _recording = true;
        Array.Clear(_lv, 0, _lv.Length);
        _time.Text = "0:00";
        _status.Text = "Recording…";
        _status.Foreground = Kit.Hex("#FF453A");
        Morph(true);
        RenderList(null);
    }

    private void StopRec()
    {
        _recording = false;
        _rec.Stop();
        Morph(false);
        _status.Foreground = Kit.Dim;
        _status.Text = "Saved";
        for (int i = 0; i < BarCount; i++) _lv[i] = 0;
        Paint();
        RenderList(_file);
        _file = "";
    }

    private void Morph(bool rec)
    {
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(320);
        _dot.BeginAnimation(WidthProperty, new DoubleAnimation(rec ? 28 : 58, d) { EasingFunction = ease });
        _dot.BeginAnimation(HeightProperty, new DoubleAnimation(rec ? 28 : 58, d) { EasingFunction = ease });
        _dot.BeginAnimation(Rectangle.RadiusXProperty, new DoubleAnimation(rec ? 8 : 29, d) { EasingFunction = ease });
        _dot.BeginAnimation(Rectangle.RadiusYProperty, new DoubleAnimation(rec ? 8 : 29, d) { EasingFunction = ease });
        foreach (Rectangle b in _bars) b.Fill = rec ? Kit.Hex("#FF453A") : Kit.Faint;
    }

    // ---------- recordings ----------

    private void RenderList(string? fresh)
    {
        _list.Children.Clear();
        List<FileInfo> files;
        try
        {
            files = Directory.Exists(Dir)
                ? new DirectoryInfo(Dir).GetFiles("*.wav").OrderByDescending(f => f.CreationTime).Take(4).ToList()
                : new List<FileInfo>();
        }
        catch { files = new List<FileInfo>(); }

        if (files.Count == 0)
        {
            var t = Kit.T("Your recordings will show up here", 12.5, false, Kit.Faint);
            t.Margin = new Thickness(6, 4, 0, 4);
            _list.Children.Add(t);
            return;
        }
        foreach (FileInfo f in files)
        {
            UIElement row = Row(f);
            _list.Children.Add(row);
            if (fresh != null && string.Equals(f.FullName, fresh, StringComparison.OrdinalIgnoreCase)) Kit.FadeIn(row, 260, 0.97);
        }
    }

    private UIElement Row(FileInfo f)
    {
        double secs = Math.Max(0, (f.Length - 44) / 88200.0);
        string path = f.FullName;

        var grid = new Grid { Height = 52 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        bool playing = string.Equals(_playing, path, StringComparison.OrdinalIgnoreCase);
        var play = Kit.Button(playing ? "pause" : "play", null, () => PlayToggle(path), false, 34);
        play.Margin = new Thickness(10, 0, 12, 0);
        play.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(play);

        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        string title = f.CreationTime.Date == DateTime.Today ? "Today, " + f.CreationTime.ToString("h:mm tt") : f.CreationTime.ToString("MMM d, h:mm tt");
        col.Children.Add(Kit.T(title, 13.5, true));
        col.Children.Add(Kit.T(Fmt(secs), 11.5, false, Kit.Dim));
        Grid.SetColumn(col, 1);
        grid.Children.Add(col);

        var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Opacity = 0 };
        var show = Kit.Button("folder", null, () =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); } catch { }
        }, false, 30, "Show in folder");
        show.Margin = new Thickness(0, 0, 6, 0);
        var del = Kit.Button("trash", null, () =>
        {
            try { if (string.Equals(_playing, path, StringComparison.OrdinalIgnoreCase)) { _player?.Close(); _playing = ""; } File.Delete(path); } catch { }
            RenderList(null);
        }, false, 30, "Delete");
        acts.Children.Add(show);
        acts.Children.Add(del);
        Grid.SetColumn(acts, 2);
        grid.Children.Add(acts);

        var card = new Border { CornerRadius = new CornerRadius(16), Background = Kit.Fill, Margin = new Thickness(0, 0, 0, 6), Child = grid };
        card.MouseEnter += (_, _) => acts.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        card.MouseLeave += (_, _) => acts.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
        return card;
    }

    private void PlayToggle(string path)
    {
        try
        {
            if (_player == null)
            {
                _player = new MediaPlayer();
                _player.MediaEnded += (_, _) => { _playing = ""; RenderList(null); };
            }
            if (string.Equals(_playing, path, StringComparison.OrdinalIgnoreCase))
            {
                _player.Pause();
                _playing = "";
            }
            else
            {
                _player.Open(new Uri(path));
                _player.Play();
                _playing = path;
            }
        }
        catch { _playing = ""; }
        RenderList(null);
    }
}
