using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LiteIsland;

// Charging / low battery notifications (event based, plus one tiny check every few minutes for the 20% and 10% warnings).
public partial class MainWindow
{
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus s);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte AcLine, Flag, Percent, Saver; public int LifeTime, FullLifeTime; }

    private byte _lastAc = 255;
    private int _lastLowStep = 100;
    private DispatcherTimer? _powerTimer;

    private void InitPower()
    {
        ReadPower(out _, out _);
        SystemEvents.PowerModeChanged += OnPowerMode;
        _powerTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _powerTimer.Tick += (_, _) => CheckPower(false);
        _powerTimer.Start();
    }

    private void DisposePower()
    {
        SystemEvents.PowerModeChanged -= OnPowerMode;
        _powerTimer?.Stop();
    }

    private bool ReadPower(out int percent, out bool hasBattery)
    {
        percent = 0; hasBattery = false;
        try
        {
            if (!GetSystemPowerStatus(out PowerStatus s)) return false;
            hasBattery = s.Flag != 128 && s.Percent != 255;
            percent = s.Percent == 255 ? 0 : s.Percent;
            _lastAc = s.AcLine;
            return true;
        }
        catch { return false; }
    }

    private void OnPowerMode(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;
        Dispatcher.BeginInvoke(new Action(() => CheckPower(true)));
    }

    private void CheckPower(bool fromEvent)
    {
        byte before = _lastAc;
        if (!ReadPower(out int pct, out bool has) || !has) return;

        if (fromEvent && before != 255 && before != _lastAc)
        {
            if (_lastAc == 1) { ShowHud("", "Charging", pct / 100.0, pct + "%", 2800); _lastLowStep = 100; }
            else ShowHud("", "On battery", pct / 100.0, pct + "%", 2400);
            return;
        }
        if (_lastAc == 1) { _lastLowStep = 100; return; }
        int step = pct <= 10 ? 10 : pct <= 20 ? 20 : 100;
        if (step < _lastLowStep)
        {
            _lastLowStep = step;
            ShowHud("", "Battery low", pct / 100.0, pct + "%", 4200);
        }
    }
}
