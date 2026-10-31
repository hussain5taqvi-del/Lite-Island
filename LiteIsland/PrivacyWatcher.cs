using System;
using System.Threading;
using Microsoft.Win32;

namespace LiteIsland;

/// <summary>
/// Tells whether the microphone / camera is being used right now. Windows keeps this in the
/// registry (Settings > Privacy > Microphone / Camera "recent activity"): an app that is using the
/// device has a start time and no stop time. Checked every 2 seconds, only while switched on.
/// </summary>
public sealed class PrivacyWatcher : IDisposable
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    private Timer? _timer;
    private bool _mic, _cam;

    /// <summary>mic in use, camera in use. Raised on a background thread, only when something changes.</summary>
    public event Action<bool, bool>? Changed;

    public void SetEnabled(bool on)
    {
        if (on && _timer == null)
        {
            _timer = new Timer(_ => Poll(), null, 300, 2000);
        }
        else if (!on && _timer != null)
        {
            _timer.Dispose();
            _timer = null;
            if (_mic || _cam)
            {
                _mic = _cam = false;
                Changed?.Invoke(false, false);
            }
        }
    }

    private void Poll()
    {
        try
        {
            bool m = InUse("microphone");
            bool c = InUse("webcam");
            if (m != _mic || c != _cam)
            {
                _mic = m;
                _cam = c;
                Changed?.Invoke(m, c);
            }
        }
        catch
        {
            // Registry not readable right now: try again in 2 seconds
        }
    }

    private static bool InUse(string capability)
    {
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(Root + capability);
            if (root == null) return false;

            foreach (string name in root.GetSubKeyNames())
            {
                using RegistryKey? k = root.OpenSubKey(name);
                if (k == null) continue;

                if (name == "NonPackaged") // classic desktop apps live one level deeper
                {
                    foreach (string sub in k.GetSubKeyNames())
                    {
                        using RegistryKey? s = k.OpenSubKey(sub);
                        if (Active(s)) return true;
                    }
                }
                else if (Active(k))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore
        }
        return false;
    }

    private static bool Active(RegistryKey? k)
    {
        if (k == null) return false;
        object? start = k.GetValue("LastUsedTimeStart");
        object? stop = k.GetValue("LastUsedTimeStop");
        return start is long s && stop is long e && s > 0 && e == 0;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
