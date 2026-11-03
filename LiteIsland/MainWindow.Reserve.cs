using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LiteIsland;

// Optional "menu bar" mode: the strip under the island is reserved like a taskbar, so maximized windows
// (Chrome and so on) stop below the island instead of behind it. The reservation is removed by Windows itself
// if the app is closed or crashes.
public partial class MainWindow
{
    [StructLayout(LayoutKind.Sequential)] private struct RsRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RsAppBar
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RsRect rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll", EntryPoint = "SHAppBarMessage")] private static extern UIntPtr RsBarMessage(uint msg, ref RsAppBar data);
    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")] private static extern int RsMetric(int index);

    private bool _rsAdded;

    private void ApplyReserve()
    {
        IntPtr h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;

        var d = new RsAppBar { cbSize = Marshal.SizeOf<RsAppBar>(), hWnd = h, uCallbackMessage = 0x8000 + 9, uEdge = 1 /* top */ };
        if (!_s.ReserveTop)
        {
            RemoveReserve();
            return;
        }

        try
        {
            if (!_rsAdded)
            {
                if (RsBarMessage(0, ref d) == UIntPtr.Zero) return; // ABM_NEW
                _rsAdded = true;
            }
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
            int px = (int)Math.Ceiling((_s.CollapsedHeight + 6) * dpi);
            d.rc = new RsRect { Left = 0, Top = 0, Right = RsMetric(0), Bottom = px };
            RsBarMessage(2, ref d);                // ABM_QUERYPOS
            d.rc.Bottom = d.rc.Top + px;
            RsBarMessage(3, ref d);                // ABM_SETPOS
        }
        catch { }
    }

    private void RemoveReserve()
    {
        if (!_rsAdded) return;
        _rsAdded = false;
        try
        {
            var d = new RsAppBar { cbSize = Marshal.SizeOf<RsAppBar>(), hWnd = new WindowInteropHelper(this).Handle };
            RsBarMessage(1, ref d); // ABM_REMOVE
        }
        catch { }
    }
}
