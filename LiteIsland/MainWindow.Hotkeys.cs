using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LiteIsland;

// System hotkeys: Milo (shortcut from Settings) and Ctrl + Alt + P = next focus mode.
// Registered with Windows, so nothing runs until a key is pressed.
public partial class MainWindow
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);

    private const int ProfileHotId = 0x4C30 + 201;
    private bool _hkHooked, _profHot;

    private void ApplySnap() // name kept: called from the settings/refresh paths
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!_hkHooked)
        {
            HwndSource.FromHwnd(hwnd)?.AddHook(HotkeyHook);
            _hkHooked = true;
        }
        if (!_profHot) _profHot = RegisterHotKey(hwnd, ProfileHotId, 0x1 | 0x2 | 0x4000, 0x50); // Ctrl + Alt + P
    }

    private void DisposeSnap()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (_profHot) UnregisterHotKey(hwnd, ProfileHotId);
            _profHot = false;
        }
        catch { }
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x0312) return IntPtr.Zero; // WM_HOTKEY
        int id = wParam.ToInt32();
        if (id == MiloHotId) { handled = true; MiloActivate("shortcut"); }
        else if (id == ProfileHotId) { handled = true; ApplyProfile(Profiles[(ProfileIndex() + 1) % Profiles.Length].Name); }
        return IntPtr.Zero;
    }
}
