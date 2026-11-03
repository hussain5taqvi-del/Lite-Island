using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace LiteIsland;

// Window snap: Ctrl + Alt + arrow keys place the window you are working in (halves, maximize, restore), and
// D / F / G = left / middle / right third, U / I / J / K = the four corners, Enter = centered.
// Registered as system hotkeys (nothing runs until you press them). Turn it off in Settings.
public partial class MainWindow
{
    [StructLayout(LayoutKind.Sequential)] private struct Rc { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonInfo { public int Size; public Rc Monitor, Work; public int Flags; }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool SnapGetRect(IntPtr h, out Rc r);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] private static extern bool SnapSetPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool SnapMonInfo(IntPtr mon, ref MonInfo mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetStyle(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Rc r, int size);

    private const int SnapBase = 0x4C30;
    private static readonly (uint vk, string act)[] SnapKeys =
    {
        (0x25, "left"), (0x27, "right"), (0x26, "max"), (0x28, "restore"), (0x0D, "center"),
        (0x44, "t1"), (0x46, "t2"), (0x47, "t3"),
        (0x55, "q1"), (0x49, "q2"), (0x4A, "q3"), (0x4B, "q4"),
    };

    private bool _snapRegistered, _snapHooked;
    private readonly Dictionary<IntPtr, Rc> _snapPrev = new();

    private void ApplySnap()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!_snapHooked)
        {
            HwndSource.FromHwnd(hwnd)?.AddHook(SnapHook);
            _snapHooked = true;
        }
        if (_s.WindowSnap && !_snapRegistered)
        {
            for (int i = 0; i < SnapKeys.Length; i++)
                RegisterHotKey(hwnd, SnapBase + i, 0x1 | 0x2 | 0x4000, SnapKeys[i].vk); // Alt + Ctrl, no repeat
            _snapRegistered = true;
        }
        else if (!_s.WindowSnap && _snapRegistered)
        {
            for (int i = 0; i < SnapKeys.Length; i++) UnregisterHotKey(hwnd, SnapBase + i);
            _snapRegistered = false;
        }
    }

    private void DisposeSnap()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (_snapRegistered) for (int i = 0; i < SnapKeys.Length; i++) UnregisterHotKey(hwnd, SnapBase + i);
            _snapRegistered = false;
        }
        catch { }
    }

    private IntPtr SnapHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == MiloHotId)
        {
            handled = true;
            MiloActivate();
            return IntPtr.Zero;
        }
        if (msg == 0x0312) // WM_HOTKEY
        {
            int i = wParam.ToInt32() - SnapBase;
            if (i >= 0 && i < SnapKeys.Length)
            {
                handled = true;
                try { SnapForeground(SnapKeys[i].act); } catch { }
            }
        }
        return IntPtr.Zero;
    }

    private void SnapForeground(string act)
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero || h == new WindowInteropHelper(this).Handle || !IsWindowVisible(h)) return;
        var cls = new StringBuilder(64);
        GetClassName(h, cls, 64);
        string c = cls.ToString();
        if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "XamlExplorerHostIslandWindow") return;
        if ((GetStyle(h, -16) & 0x00040000) == 0) return; // only windows you can resize (not dialogs, menus, games)

        IntPtr oldCtx = SetThreadDpiAwarenessContext(new IntPtr(-4)); // real pixels on every screen
        try
        {
            var mi = new MonInfo { Size = Marshal.SizeOf<MonInfo>() };
            if (!SnapMonInfo(MonitorFromWindow(h, 2), ref mi)) return;
            Rc wa = mi.Work;
            int x = wa.L, y = wa.T, w = wa.R - wa.L, hh = wa.B - wa.T;

            if (act == "max")
            {
                ShowWindow(h, IsZoomed(h) ? 9 : 3);
                return;
            }

            if (act == "restore")
            {
                if (IsZoomed(h)) { ShowWindow(h, 9); return; }
                if (_snapPrev.TryGetValue(h, out Rc prev))
                {
                    Place(h, prev.L, prev.T, prev.R - prev.L, prev.B - prev.T, false);
                    _snapPrev.Remove(h);
                }
                else Place(h, x + w / 5, y + hh / 8, w * 3 / 5, hh * 3 / 4, true);
                return;
            }

            if (SnapGetRect(h, out Rc cur) && !IsZoomed(h))
            {
                if (_snapPrev.Count > 32) _snapPrev.Clear();
                if (!_snapPrev.ContainsKey(h)) _snapPrev[h] = cur;
            }
            if (IsZoomed(h)) ShowWindow(h, 9);

            switch (act)
            {
                case "left": Place(h, x, y, w / 2, hh, true); break;
                case "right": Place(h, x + w / 2, y, w - w / 2, hh, true); break;
                case "center": Place(h, x + w / 5, y + hh / 8, w * 3 / 5, hh * 3 / 4, true); break;
                case "t1": Place(h, x, y, w / 3, hh, true); break;
                case "t2": Place(h, x + w / 3, y, w / 3, hh, true); break;
                case "t3": Place(h, x + 2 * (w / 3), y, w - 2 * (w / 3), hh, true); break;
                case "q1": Place(h, x, y, w / 2, hh / 2, true); break;
                case "q2": Place(h, x + w / 2, y, w - w / 2, hh / 2, true); break;
                case "q3": Place(h, x, y + hh / 2, w / 2, hh - hh / 2, true); break;
                case "q4": Place(h, x + w / 2, y + hh / 2, w - w / 2, hh - hh / 2, true); break;
            }
        }
        finally { SetThreadDpiAwarenessContext(oldCtx); }
    }

    // Windows 10 / 11 windows have an invisible border: compensate, so the visible edges meet the screen edges
    private static void Place(IntPtr h, int x, int y, int w, int hh, bool compensate)
    {
        int dl = 0, dt = 0, dr = 0, db = 0;
        if (compensate && SnapGetRect(h, out Rc win) && DwmGetWindowAttribute(h, 9, out Rc ext, Marshal.SizeOf<Rc>()) == 0)
        {
            dl = ext.L - win.L; dt = ext.T - win.T; dr = win.R - ext.R; db = win.B - ext.B;
        }
        SnapSetPos(h, IntPtr.Zero, x - dl, y - dt, w + dl + dr, hh + dt + db, 0x0004 | 0x0010); // no z-order change, no activate
    }
}
