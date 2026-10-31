using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace LiteIsland;

/// <summary>
/// Tells us when a Bluetooth device (headphones, AirPods, ...) connects. Event based, no polling.
/// </summary>
public sealed class BluetoothWatcher : IDisposable
{
    /// <summary>Device name and battery (null when the device does not report one). Raised on a background thread.</summary>
    public event Action<string, int?>? Connected;

    private DeviceWatcher? _watcher;
    private volatile bool _ready; // false while Windows lists the devices that were already connected
    private string _lastName = "";
    private long _lastAt;

    public void SetEnabled(bool on)
    {
        if (on) Start();
        else Stop();
    }

    private void Start()
    {
        if (_watcher != null) return;

        try
        {
            string selector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var w = DeviceInformation.CreateWatcher(selector);

            _ready = false;
            w.EnumerationCompleted += (sender, args) => _ready = true;
            w.Added += async (sender, info) => await OnAddedAsync(info);
            w.Start();
            _watcher = w;
        }
        catch
        {
            // Bluetooth not available
        }
    }

    private void Stop()
    {
        var w = _watcher;
        _watcher = null;
        _ready = false;
        if (w == null) return;

        try
        {
            if (w.Status == DeviceWatcherStatus.Started || w.Status == DeviceWatcherStatus.EnumerationCompleted)
                w.Stop();
        }
        catch { }
    }

    private async Task OnAddedAsync(DeviceInformation info)
    {
        try
        {
            if (!_ready) return; // device was already connected when the app started
            string name = info.Name;
            if (string.IsNullOrWhiteSpace(name)) return;

            // The same device can be reported twice in a row
            long now = Environment.TickCount64;
            if (name == _lastName && now - _lastAt < 8000) return;
            _lastName = name;
            _lastAt = now;

            // Give the device a moment to report its battery, then read it
            await Task.Delay(1500);
            int? battery = null;
            var list = await BluetoothService.GetConnectedAsync();
            var match = list.FirstOrDefault(d => d.Name == name);
            if (match != null) battery = match.Battery;

            Connected?.Invoke(name, battery);
        }
        catch
        {
            // Ignore: the alert is optional
        }
    }

    public void Dispose() => Stop();
}
