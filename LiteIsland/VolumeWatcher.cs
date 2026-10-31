using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace LiteIsland;

/// <summary>
/// Tells us when the system volume (or mute) changes, using the Windows Core Audio notification.
/// It is event based: no polling, so it uses no CPU while the volume does not change.
/// </summary>
public sealed class VolumeWatcher : IDisposable
{
    /// <summary>Volume 0..1 and the mute state. Raised on a background thread.</summary>
    public event Action<float, bool>? Changed;

    private IAudioEndpointVolume? _vol;
    private AudioCallback? _callback;
    private string _deviceId = "";

    // The default speakers can change (headphones plugged in): check once in a while
    private readonly DispatcherTimer _deviceCheck = new() { Interval = TimeSpan.FromSeconds(20) };
    private bool _enabled;

    public VolumeWatcher()
    {
        _deviceCheck.Tick += (_, _) => Connect(onlyIfChanged: true);
    }

    public void SetEnabled(bool on)
    {
        if (on == _enabled) return;
        _enabled = on;

        if (on)
        {
            Connect(onlyIfChanged: false);
            _deviceCheck.Start();
        }
        else
        {
            _deviceCheck.Stop();
            Disconnect();
        }
    }

    private void Connect(bool onlyIfChanged)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out IMMDevice device) != 0) return;
                try
                {
                    device.GetId(out string id);
                    if (onlyIfChanged && _vol != null && id == _deviceId) return;

                    Disconnect();

                    Guid iid = typeof(IAudioEndpointVolume).GUID;
                    if (device.Activate(ref iid, 23, IntPtr.Zero, out object o) != 0) return;

                    var vol = (IAudioEndpointVolume)o;
                    var cb = new AudioCallback(this);
                    if (vol.RegisterControlChangeNotify(cb) != 0)
                    {
                        Marshal.ReleaseComObject(vol);
                        return;
                    }

                    _vol = vol;
                    _callback = cb;
                    _deviceId = id;
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch
        {
            // No audio device: try again at the next check
        }
    }

    private void Disconnect()
    {
        try
        {
            if (_vol != null && _callback != null) _vol.UnregisterControlChangeNotify(_callback);
        }
        catch { }

        try
        {
            if (_vol != null) Marshal.ReleaseComObject(_vol);
        }
        catch { }

        _vol = null;
        _callback = null;
        _deviceId = "";
    }

    public void Dispose()
    {
        _deviceCheck.Stop();
        _enabled = false;
        Disconnect();
    }

    private void Raise(float volume, bool muted) => Changed?.Invoke(volume, muted);

    // ---------- Windows Core Audio COM pieces ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyData
    {
        public Guid EventContext;
        public int Muted;
        public float MasterVolume;
        public uint Channels;
    }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class AudioCallback : IAudioEndpointVolumeCallback
    {
        private readonly VolumeWatcher _owner;
        public AudioCallback(VolumeWatcher owner) => _owner = owner;

        public int OnNotify(IntPtr notifyData)
        {
            try
            {
                var n = Marshal.PtrToStructure<NotifyData>(notifyData);
                _owner.Raise(n.MasterVolume, n.Muted != 0);
            }
            catch { }
            return 0;
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object iface);

        [PreserveSig]
        int OpenPropertyStore(int access, out IntPtr properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
    [PreserveSig]
    int OnNotify(IntPtr notifyData);
}

// Only the first two methods are needed (the rest of the interface is never called)
[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

    [PreserveSig]
    int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
}
