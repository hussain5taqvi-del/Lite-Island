using System;
using System.Management;
using System.Threading.Tasks;

namespace LiteIsland;

/// <summary>
/// Tells us when the screen brightness changes (laptop screens). Uses the Windows WMI event,
/// so nothing runs while the brightness stays the same. On PCs without a built-in screen it simply never fires.
/// </summary>
public sealed class BrightnessWatcher : IDisposable
{
    /// <summary>Brightness 0..100. Raised on a background thread.</summary>
    public event Action<int>? Changed;

    private readonly object _lock = new();
    private ManagementEventWatcher? _watcher;
    private bool _enabled;

    public void SetEnabled(bool on)
    {
        lock (_lock)
        {
            if (on == _enabled) return;
            _enabled = on;
        }

        if (on) Task.Run(Start); // connecting to WMI can take a moment: keep it off the UI thread
        else Stop();
    }

    private void Start()
    {
        try
        {
            var scope = new ManagementScope(@"root\wmi");
            var query = new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent");
            var w = new ManagementEventWatcher(scope, query);
            w.EventArrived += (_, e) =>
            {
                try
                {
                    Changed?.Invoke(Convert.ToInt32(e.NewEvent["Brightness"]));
                }
                catch { }
            };
            w.Start();

            lock (_lock)
            {
                if (_enabled)
                {
                    _watcher = w;
                    return;
                }
            }

            // It was switched off while connecting
            w.Stop();
            w.Dispose();
        }
        catch
        {
            // WMI not available or no brightness support: ignore
        }
    }

    private void Stop()
    {
        ManagementEventWatcher? w;
        lock (_lock)
        {
            w = _watcher;
            _watcher = null;
        }

        try
        {
            w?.Stop();
            w?.Dispose();
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_lock) _enabled = false;
        Stop();
    }
}
