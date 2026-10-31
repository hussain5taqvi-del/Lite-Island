using System;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using System.IO;
using System.Linq;

namespace LiteIsland;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Helper mode: "LiteIsland.exe --worker ..." does one heavy job (picture, PDF, OCR...) and exits.
        // No window, no single-instance check, no RAM limit (it lives for a few seconds).
        if (e.Args.Length > 0 && e.Args[0] == "--worker")
        {
            int code = Worker.Run(e.Args);
            Shutdown(code);
            return;
        }

        // Hard RAM limit for the island itself (default 30 MB, see Settings.json "RamLimitMb")
        MemoryGuard.Apply(AppSettings.Load().RamLimitMb);

        SmoothRender.Init();

        // Allow only one copy (so startup + manual start never create two islands)
        _mutex = new Mutex(true, @"Local\LiteIsland_SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        // Never close silently: write problems to %AppData%\LiteIsland\error.log
        DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);

        // Keep the Windows startup entry pointing at this exe
        StartupManager.Repair();

        // Started by Windows at sign-in: wait until the desktop (taskbar) is ready, so the island
        // is placed correctly and the tray icon appears
        if (e.Args.Contains(StartupManager.AutoArg)) WaitForDesktop();

        // Animations run at the refresh rate of the screen (120 / 144 Hz screens look much smoother).
        // Nothing runs while the island is idle, so this costs nothing at rest.
        try
        {
            int hz = ScreenRefreshRate();
            if (hz > 60)
                Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline),
                    new FrameworkPropertyMetadata(Math.Min(hz, 165)));
        }
        catch { }

        base.OnStartup(e);
    }

    private static void WaitForDesktop()
    {
        try
        {
            for (int i = 0; i < 60; i++) // up to 30 seconds
            {
                if (FindWindow("Shell_TrayWnd", null) != IntPtr.Zero) break;
                Thread.Sleep(500);
            }
            Thread.Sleep(2000); // let the desktop finish loading
        }
        catch { }
    }

    private static void Log(Exception? ex)
    {
        try
        {
            if (ex == null) return;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteIsland");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "error.log");
            if (File.Exists(file) && new FileInfo(file).Length > 200_000) File.Delete(file);
            File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + Environment.NewLine + Environment.NewLine);
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);

    private static int ScreenRefreshRate()
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return 60;
        try { return GetDeviceCaps(dc, 116); } // VREFRESH
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
            _mutex = null;
        }
        base.OnExit(e);
    }
}
