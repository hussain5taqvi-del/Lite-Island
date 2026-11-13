using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LiteIsland;

// System hotkeys: Milo (shortcut from Settings) and Ctrl + Alt + M = next focus mode, and the screen tools (Ctrl + Alt + T / P / D / L / F).
// Registered with Windows, so nothing runs until a key is pressed.
public partial class MainWindow
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);

    private const int ProfileHotId = 0x4C30 + 201;
    private const int ToolHotBase = 0x4C30 + 210; // 210..214
    private static readonly (uint Vk, string Tool)[] ToolKeys = { (0x54, "ocr"), (0x50, "color"), (0x44, "draw"), (0x4C, "lens"), (0x46, "font") };
    private bool _hkHooked, _profHot, _toolHot;

    private void ApplySnap() // name kept: called from the settings/refresh paths
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!_hkHooked)
        {
            HwndSource.FromHwnd(hwnd)?.AddHook(HotkeyHook);
            _hkHooked = true;
        }
        if (!_profHot) _profHot = RegisterHotKey(hwnd, ProfileHotId, 0x1 | 0x2 | 0x4000, 0x4D); // Ctrl + Alt + M

        bool want = _s.ToolHotkeys;
        if (want && !_toolHot)
        {
            for (int i = 0; i < ToolKeys.Length; i++) RegisterHotKey(hwnd, ToolHotBase + i, 0x1 | 0x2 | 0x4000, ToolKeys[i].Vk);
            _toolHot = true;
        }
        else if (!want && _toolHot)
        {
            for (int i = 0; i < ToolKeys.Length; i++) UnregisterHotKey(hwnd, ToolHotBase + i);
            _toolHot = false;
        }
    }

    /// <summary>Starts a tool in its own small helper process (see ToolHost).</summary>
    private void LaunchTool(string tool)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--tool " + tool) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch { }
    }

    private void DisposeSnap()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (_profHot) UnregisterHotKey(hwnd, ProfileHotId);
            _profHot = false;
            if (_toolHot) for (int i = 0; i < ToolKeys.Length; i++) UnregisterHotKey(hwnd, ToolHotBase + i);
            _toolHot = false;
        }
        catch { }
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x0312) return IntPtr.Zero; // WM_HOTKEY
        int id = wParam.ToInt32();
        if (id == MiloHotId) { handled = true; MiloActivate("shortcut"); }
        else if (id >= ToolHotBase && id < ToolHotBase + ToolKeys.Length) { handled = true; LaunchTool(ToolKeys[id - ToolHotBase].Tool); }
        else if (id == ProfileHotId) { handled = true; ApplyProfile(Profiles[(ProfileIndex() + 1) % Profiles.Length].Name); }
        return IntPtr.Zero;
    }
}
