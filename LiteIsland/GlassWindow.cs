using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LiteIsland;

/// <summary>
/// Used by the Settings and Wallpapers windows only (never the island):
///  - pure black window with a black title bar
///  - makes sure the window really gets keyboard focus (the island never takes focus,
///    so Windows used to refuse to give it to the new window and typing did nothing)
/// </summary>
internal static class GlassWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>Call from OnSourceInitialized: black title bar to match the pure black window.</summary>
    public static void Apply(Window w)
    {
        IntPtr hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // dark title bar
            int black = 0x000000;
            DwmSetWindowAttribute(hwnd, 35, ref black, sizeof(int)); // title bar color (Windows 11)
            int edge = 0x2A2A2A;
            DwmSetWindowAttribute(hwnd, 34, ref edge, sizeof(int));  // thin window border (Windows 11)
        }
        catch { }
    }

    /// <summary>Brings the window to the front with keyboard focus, even when another program is active.</summary>
    public static void ForceForeground(Window w)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;

            IntPtr fg = GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
            uint cur = GetCurrentThreadId();

            if (fgThread != 0 && fgThread != cur)
            {
                AttachThreadInput(cur, fgThread, true);
                SetForegroundWindow(hwnd);
                AttachThreadInput(cur, fgThread, false);
            }
            else
            {
                SetForegroundWindow(hwnd);
            }
            w.Activate();
        }
        catch { }
    }
}
