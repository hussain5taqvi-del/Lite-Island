using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiteIsland;

// Raw winmm.dll audio: no extra packages, tiny memory. Microphone = 16 kHz mono 16 bit, speaker = 24 kHz mono 16 bit.
internal static class MiloWave
{
    public const uint WHDR_DONE = 1;
    public static readonly IntPtr Mapper = new(-1);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WaveHdr
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    public static readonly int HdrSize = Marshal.SizeOf<WaveHdr>();
    public static readonly int FlagsOff = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.Flags));
    public static readonly int RecOff = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.BytesRecorded));

    public static WaveFormat Mono16(int rate) => new()
    {
        FormatTag = 1, Channels = 1, SamplesPerSec = (uint)rate, BitsPerSample = 16, BlockAlign = 2,
        AvgBytesPerSec = (uint)(rate * 2), Size = 0
    };

    public static IntPtr NewHeader(int bytes, out IntPtr data)
    {
        data = Marshal.AllocHGlobal(bytes);
        IntPtr h = Marshal.AllocHGlobal(HdrSize);
        var s = new WaveHdr { Data = data, BufferLength = (uint)bytes };
        Marshal.StructureToPtr(s, h, false);
        return h;
    }

    [DllImport("winmm.dll")] public static extern int waveInOpen(out IntPtr h, IntPtr dev, ref WaveFormat f, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] public static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] public static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] public static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] public static extern int waveInStart(IntPtr h);
    [DllImport("winmm.dll")] public static extern int waveInStop(IntPtr h);
    [DllImport("winmm.dll")] public static extern int waveInReset(IntPtr h);
    [DllImport("winmm.dll")] public static extern int waveInClose(IntPtr h);

    [DllImport("winmm.dll")] public static extern int waveOutOpen(out IntPtr h, IntPtr dev, ref WaveFormat f, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] public static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] public static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] public static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] public static extern int waveOutReset(IntPtr h);
    [DllImport("winmm.dll")] public static extern int waveOutClose(IntPtr h);
}

/// <summary>Microphone: 16 kHz, 16 bit, mono, 40 ms chunks.</summary>
internal sealed class MiloMic
{
    private const int Bufs = 6, Bytes = 1280;
    private IntPtr _h;
    private readonly IntPtr[] _hdr = new IntPtr[Bufs];
    private readonly IntPtr[] _dat = new IntPtr[Bufs];
    private Thread? _th;
    private volatile bool _run;
    private float _hpIn, _hpOut;

    public Action<byte[]>? OnChunk;

    public int Start()
    {
        var f = MiloWave.Mono16(16000);
        int rc = MiloWave.waveInOpen(out _h, MiloWave.Mapper, ref f, IntPtr.Zero, IntPtr.Zero, 0);
        if (rc != 0) return rc;
        for (int i = 0; i < Bufs; i++)
        {
            _hdr[i] = MiloWave.NewHeader(Bytes, out _dat[i]);
            MiloWave.waveInPrepareHeader(_h, _hdr[i], MiloWave.HdrSize);
            MiloWave.waveInAddBuffer(_h, _hdr[i], MiloWave.HdrSize);
        }
        MiloWave.waveInStart(_h);
        _run = true;
        _th = new Thread(Loop) { IsBackground = true, Name = "MiloMic" };
        _th.Start();
        return 0;
    }

    private void Loop()
    {
        while (_run)
        {
            for (int i = 0; i < Bufs && _run; i++)
            {
                if ((((uint)Marshal.ReadInt32(_hdr[i], MiloWave.FlagsOff)) & MiloWave.WHDR_DONE) == 0) continue;
                int rec = Marshal.ReadInt32(_hdr[i], MiloWave.RecOff);
                if (rec <= 1) { MiloWave.waveInAddBuffer(_h, _hdr[i], MiloWave.HdrSize); continue; }
                var b = new byte[rec];
                Marshal.Copy(_dat[i], b, 0, rec);
                MiloWave.waveInAddBuffer(_h, _hdr[i], MiloWave.HdrSize); // give the buffer back first

                // one-pole high-pass (about 80 Hz): removes the hum and DC offset that cheap laptop mics add
                for (int k = 0; k + 1 < rec; k += 2)
                {
                    float x = (short)(b[k] | (b[k + 1] << 8));
                    float y = x - _hpIn + 0.97f * _hpOut;
                    _hpIn = x; _hpOut = y;
                    int v = Math.Clamp((int)y, short.MinValue, short.MaxValue);
                    b[k] = (byte)(v & 0xFF);
                    b[k + 1] = (byte)((v >> 8) & 0xFF);
                }
                OnChunk?.Invoke(b);
            }
            Thread.Sleep(8);
        }
    }

    public void Stop()
    {
        _run = false;
        try { _th?.Join(2000); } catch { }
        if (_h == IntPtr.Zero) return;
        MiloWave.waveInStop(_h);
        MiloWave.waveInReset(_h);
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
}

/// <summary>Speaker: plays 24 kHz mono PCM chunks as they arrive. Memory is freed as soon as a chunk has played.</summary>
internal sealed class MiloSpeaker
{
    private IntPtr _h;
    private readonly List<IntPtr[]> _items = new();
    private readonly object _lk = new();
    private bool _open;
    private long _lastBusy;

    public bool Open()
    {
        var f = MiloWave.Mono16(24000);
        if (MiloWave.waveOutOpen(out _h, MiloWave.Mapper, ref f, IntPtr.Zero, IntPtr.Zero, 0) != 0) return false;
        _open = true;
        return true;
    }

    public void Play(byte[] pcm)
    {
        if (pcm.Length < 2) return;
        lock (_lk)
        {
            if (!_open) return;
            Reap();
            IntPtr hd = MiloWave.NewHeader(pcm.Length, out IntPtr d);
            Marshal.Copy(pcm, 0, d, pcm.Length);
            MiloWave.waveOutPrepareHeader(_h, hd, MiloWave.HdrSize);
            if (MiloWave.waveOutWrite(_h, hd, MiloWave.HdrSize) != 0)
            {
                MiloWave.waveOutUnprepareHeader(_h, hd, MiloWave.HdrSize);
                Marshal.FreeHGlobal(d);
                Marshal.FreeHGlobal(hd);
                return;
            }
            _items.Add(new[] { hd, d });
        }
    }

    private void Free(IntPtr[] it)
    {
        MiloWave.waveOutUnprepareHeader(_h, it[0], MiloWave.HdrSize);
        Marshal.FreeHGlobal(it[1]);
        Marshal.FreeHGlobal(it[0]);
    }

    private void Reap()
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if ((((uint)Marshal.ReadInt32(_items[i][0], MiloWave.FlagsOff)) & MiloWave.WHDR_DONE) != 0)
            {
                Free(_items[i]);
                _items.RemoveAt(i);
            }
        }
    }

    public int Pending { get { lock (_lk) { if (!_open) return 0; Reap(); return _items.Count; } } }

    /// <summary>True while playing and for a short moment after (so the microphone does not hear the tail).</summary>
    public bool Busy
    {
        get
        {
            if (Pending > 0) { Interlocked.Exchange(ref _lastBusy, Environment.TickCount64); return true; }
            return Environment.TickCount64 - Interlocked.Read(ref _lastBusy) < 350;
        }
    }

    public void Flush()
    {
        lock (_lk) { if (!_open) return; MiloWave.waveOutReset(_h); Reap(); }
    }

    public void Close()
    {
        lock (_lk)
        {
            if (!_open) return;
            _open = false;
            MiloWave.waveOutReset(_h);
            foreach (IntPtr[] it in _items) Free(it);
            _items.Clear();
            MiloWave.waveOutClose(_h);
        }
    }
}
