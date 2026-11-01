using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LiteIsland;

/// <summary>
/// System tray icon, pure Win32 (no WinForms / System.Drawing, which saves a lot of RAM).
/// Left click = Settings, right click = menu. The icon comes from the exe itself (app.ico).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const int IdSettings = 1, IdWallpapers = 2, IdStartup = 3, IdQuit = 4, IdMirror = 5;

    private readonly Action _openSettings, _openWallpapers, _openMirror, _exit;
    private readonly HwndSource _sink;
    private readonly uint _taskbarCreated;
    private IntPtr _hicon;
    private bool _added;

    public TrayIcon(Action openSettings, Action openWallpapers, Action openMirror, Action exit)
    {
        _openMirror = openMirror;
        _openSettings = openSettings;
        _openWallpapers = openWallpapers;
        _exit = exit;

        // Message-only window that receives the tray messages
        var p = new HwndSourceParameters("LiteIslandTray") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 };
        _sink = new HwndSource(p);
        _sink.AddHook(WndProc);

        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _hicon = LoadAppIcon();
        Add();
    }

    private void Add()
    {
        var d = NewData();
        d.uFlags = 0x1 | 0x2 | 0x4; // MESSAGE | ICON | TIP
        d.uCallbackMessage = WM_APP_TRAY;
        d.hIcon = _hicon;
        d.szTip = "LiteIsland";
        _added = Shell_NotifyIcon(0, ref d); // NIM_ADD
    }

    private NOTIFYICONDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _sink.Handle,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = ""
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAY)
        {
            int ev = (int)(lParam.ToInt64() & 0xFFFF);
            if (ev == WM_LBUTTONUP) { handled = true; _openSettings(); }
            else if (ev == WM_RBUTTONUP || ev == WM_CONTEXTMENU) { handled = true; ShowMenu(); }
        }
        else if (_taskbarCreated != 0 && msg == _taskbarCreated)
        {
            Add(); // Explorer restarted: put the icon back
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, 0x0, IdSettings, "Settings");
            AppendMenu(menu, 0x0, IdWallpapers, "Wallpapers");
            AppendMenu(menu, 0x0, IdMirror, "Camera mirror");
            AppendMenu(menu, StartupManager.IsEnabled ? 0x8u : 0x0u, IdStartup, "Start with Windows"); // 0x8 = checked
            AppendMenu(menu, 0x800, 0, null); // separator
            AppendMenu(menu, 0x0, IdQuit, "Quit");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_sink.Handle); // needed so the menu closes when clicking elsewhere
            int cmd = TrackPopupMenu(menu, 0x0100 | 0x0002, pt.X, pt.Y, 0, _sink.Handle, IntPtr.Zero); // RETURNCMD | RIGHTBUTTON
            PostMessage(_sink.Handle, 0, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case IdSettings: _openSettings(); break;
                case IdWallpapers: _openWallpapers(); break;
                case IdMirror: _openMirror(); break;
                case IdStartup: StartupManager.Set(!StartupManager.IsEnabled); break;
                case IdQuit: _exit(); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    // The logo that is inside the exe (ApplicationIcon), in the size the tray wants
    private static IntPtr LoadAppIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                IntPtr[] small = new IntPtr[1];
                if (ExtractIconEx(exe, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero) return small[0];
            }
        }
        catch { }
        return LoadIcon(IntPtr.Zero, new IntPtr(32512)); // plain Windows icon as the last resort
    }

    public void Dispose()
    {
        try
        {
            if (_added)
            {
                var d = NewData();
                Shell_NotifyIcon(2, ref d); // NIM_DELETE
                _added = false;
            }
        }
        catch { }
        try { _sink.RemoveHook(WndProc); _sink.Dispose(); } catch { }
        if (_hicon != IntPtr.Zero) { DestroyIcon(_hicon); _hicon = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr hInst, IntPtr name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
}
