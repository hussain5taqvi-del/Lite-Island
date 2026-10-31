using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace LiteIsland;

/// <summary>
/// Hold the ring key (chosen in the settings) while Explorer or the Desktop is in front: raises Requested with the files that are selected, KeyReleased when it is let go.
/// The keyboard hook only looks at which key goes down / up. Nothing is stored, logged or sent anywhere.
/// </summary>
internal sealed class SelectionService : IDisposable
{
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private readonly Dispatcher _ui;
    private readonly HookProc _proc;
    private IntPtr _hook;

    /// <summary>Raised on the UI thread. The list is never empty.</summary>
    public event Action<string[]>? Requested;

    /// <summary>Raised when the key was held in Explorer / Desktop but no file could be read, or the hook is missing.</summary>
    public event Action<string>? Problem;

    /// <summary>Set by the main window while it changes the clipboard itself (so the clipboard history ignores it).</summary>
    public Action<int>? IgnoreClipboard;

    public SelectionService(Dispatcher ui)
    {
        _ui = ui;
        _proc = HookCallback;
    }

    private Thread? _hookThread;
    private uint _hookThreadId;

    // The hook lives on its own thread with its own message loop. If it lived on the UI thread, a busy moment
    // (animation, memory trim) would make Windows drop the key event or even remove the hook: the ring would
    // open "sometimes".
    public void SetEnabled(bool on)
    {
        if (on && _hookThread == null)
        {
            var started = new ManualResetEventSlim(false);
            _hookThread = new Thread(() =>
            {
                _hookThreadId = GetCurrentThreadId();
                IntPtr h = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _proc, GetModuleHandle(null), 0);
                _hook = h;
                bool ok = h != IntPtr.Zero;
                started.Set();
                if (!ok) { _ui.BeginInvoke(new Action(() => Problem?.Invoke("The ring key could not be hooked"))); return; }
                while (GetMessage(out MSG m, IntPtr.Zero, 0, 0) > 0) { }
                UnhookWindowsHookEx(h);
                if (_hook == h) _hook = IntPtr.Zero;
            }) { IsBackground = true, Name = "LiteIsland keys" };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();
            started.Wait(1500);
        }
        else if (!on && _hookThread != null)
        {
            PostThreadMessage(_hookThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            _hookThread = null;
        }
    }

    /// <summary>The key that opens the ring while it is held (set from the settings).</summary>
    public volatile int TriggerVk = 0x58;
    private const int HoldMs = 150;

    /// <summary>True from the moment the key goes down in Explorer / Desktop until it is let go.</summary>
    public bool Held => _held;

    /// <summary>Raised on the UI thread when the held key is let go.</summary>
    public event Action? KeyReleased;

    /// <summary>The key that is held to open the apps ring (works in every program). Raised on the UI thread.</summary>
    public volatile int AppsVk = 0x14; // Caps Lock
    public volatile bool AppsEnabled = true;
    public event Action? AppsDown, AppsUp, AppsCancel;
    public bool AppsHeld => _appsHeld;
    private volatile bool _appsHeld, _appsCancel, _appsFired;
    private long _appsTick, _appsId;

    private volatile bool _held, _cancel, _fired;
    private long _holdId, _lastTick;

    private static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;
    private static int Group(int vk) => vk is 0x11 or 0xA2 or 0xA3 ? 1 : vk is 0x12 or 0xA4 or 0xA5 ? 2 : vk is 0x5B or 0x5C ? 3 : 0;

    // Ctrl / Alt / Win held together with the ring key means a normal shortcut (Ctrl+X ...): leave it alone
    private static bool OtherModifierDown(int trigger)
    {
        foreach (int v in new[] { 0x11, 0x12, 0x5B, 0x5C })
            if (Group(v) != Group(trigger) && (GetAsyncKeyState(v) & 0x8000) != 0) return true;
        return false;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                int msg = wParam.ToInt32();
                bool down = msg == 0x0100 || msg == 0x0104; // WM_KEYDOWN / WM_SYSKEYDOWN
                bool up = msg == 0x0101 || msg == 0x0105;   // WM_KEYUP / WM_SYSKEYUP
                int vk = Marshal.ReadInt32(lParam);          // KBDLLHOOKSTRUCT.vkCode
                int flags = Marshal.ReadInt32(lParam, 8);    // KBDLLHOOKSTRUCT.flags (bit 4 = injected)

                if ((flags & 0x10) == 0)
                {
                    int trig = TriggerVk;
                    bool mod = IsModifier(trig);
                    long now = Environment.TickCount64;

                    // Apps key: hold it and the ring of favorite apps opens, let go and it closes (or opens the app under
                    // the cursor). A letter-like key (Caps Lock ...) is swallowed so it does nothing else; a modifier key
                    // (Ctrl, Alt ...) is never swallowed and a shortcut such as Ctrl+C never opens the ring.
                    int av = AppsVk;
                    if (AppsEnabled && av != trig)
                    {
                        bool amod = IsModifier(av);
                        if (vk == av)
                        {
                            if (down)
                            {
                                if (_appsHeld && now - _appsTick < 700)
                                {
                                    _appsTick = now;
                                    return amod ? CallNextHookEx(_hook, code, wParam, lParam) : (IntPtr)1;
                                }
                                if (!OtherModifierDown(av))
                                {
                                    _appsHeld = true;
                                    _appsCancel = false;
                                    _appsTick = now;
                                    long aid = Interlocked.Increment(ref _appsId);
                                    _appsFired = false;
                                    Task.Delay(HoldMs).ContinueWith(_ =>
                                    {
                                        if (_appsHeld && !_appsCancel && Interlocked.Read(ref _appsId) == aid) { _appsFired = true; _ui.BeginInvoke(new Action(() => AppsDown?.Invoke())); }
                                    });
                                    if (!amod) return (IntPtr)1;
                                }
                            }
                            else if (up && _appsHeld)
                            {
                                _appsHeld = false;
                                _ui.BeginInvoke(new Action(() => AppsUp?.Invoke()));
                                if (!amod)
                                {
                                    if (!_appsFired) Tap(av); // a quick tap is still an ordinary key press (Caps Lock toggles ...)
                                    return (IntPtr)1;
                                }
                            }
                        }
                        else if (down && _appsHeld && amod)
                        {
                            _appsCancel = true; // Ctrl+C and the like: a shortcut, not the apps key
                            _ui.BeginInvoke(new Action(() => AppsCancel?.Invoke()));
                        }
                    }

                    if (vk == trig)
                    {
                        if (down)
                        {
                            if (_held && now - _lastTick < 700)
                            {
                                _lastTick = now;
                                return mod ? CallNextHookEx(_hook, code, wParam, lParam) : (IntPtr)1; // key repeat
                            }
                            if (InShellWindow() && !OtherModifierDown(trig))
                            {
                                // Held in Explorer / on the Desktop: after a short moment the ring opens, until the key is let go.
                                // Letter keys are swallowed there (Explorer must not jump to files that start with it);
                                // modifier keys are never swallowed.
                                _held = true;
                                _cancel = false;
                                _lastTick = now;
                                _fired = false;
                                long id = Interlocked.Increment(ref _holdId);
                                Task.Delay(HoldMs).ContinueWith(_ =>
                                {
                                    if (_held && !_cancel && Interlocked.Read(ref _holdId) == id) { _fired = true; _ui.BeginInvoke(new Action(OnHold)); }
                                });
                                if (!mod) return (IntPtr)1;
                            }
                        }
                        else if (up && _held)
                        {
                            _held = false;
                            _ui.BeginInvoke(new Action(() => KeyReleased?.Invoke()));
                            if (!mod)
                            {
                                if (!_fired && !_cancel) Tap(trig); // quick tap: the key still does its normal job
                                return (IntPtr)1;
                            }
                        }
                    }
                    else if (down && _held)
                    {
                        _cancel = true; // another key came with it: a shortcut, not a hold
                    }
                }
            }
        }
        catch
        {
            // a hook must never throw
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    // The ring keys are swallowed while held; when it was only a tap the key press is sent again (the hook ignores injected keys)
    private static void Tap(int vk)
    {
        Task.Run(() =>
        {
            try
            {
                keybd_event((byte)vk, 0, 0, UIntPtr.Zero);
                keybd_event((byte)vk, 0, 2, UIntPtr.Zero);
            }
            catch { }
        });
    }

    private static bool InShellWindow()
    {
        string cls = ClassOf(GetForegroundWindow());
        return cls == "CabinetWClass" || cls == "ExploreWClass" || cls == "Progman" || cls == "WorkerW";
    }

    // ---------- Reading the selection ----------

    private async void OnHold()
    {
        if (!_held) return;
        long myId = Interlocked.Read(ref _holdId); // a newer hold replaces an older, slower read (nothing can get stuck)
        try
        {
            IntPtr fg = GetForegroundWindow();
            string cls = ClassOf(fg);
            string[] files;

            if (cls == "CabinetWClass" || cls == "ExploreWClass")
            {
                files = await Task.Run(() => FromExplorer(fg));
                if (files.Length == 0) files = await FromDesktopAsync(); // shell gave nothing: copy-based fallback
            }
            else if (cls == "Progman" || cls == "WorkerW")
                files = await FromDesktopAsync();
            else
                return; // some other program: nothing happens

            if (!_held || myId != Interlocked.Read(ref _holdId)) return; // let go (or held again) while the selection was being read
            files = files.Where(f => File.Exists(f) || Directory.Exists(f)).ToArray();
            if (files.Length > 0) Requested?.Invoke(files);
            else Problem?.Invoke("Select a file first");
        }
        catch
        {
            // not fatal
        }
        finally
        {
        }
    }

    // Explorer windows: ask the shell which items are selected (no clipboard involved)
    private static string[] FromExplorer(IntPtr fg)
    {
        string[] result = Array.Empty<string>();
        var t = new Thread(() =>
        {
            try
            {
                Type? st = Type.GetTypeFromProgID("Shell.Application");
                if (st == null) return;
                dynamic shell = Activator.CreateInstance(st)!;
                dynamic windows = shell.Windows();
                int count = windows.Count;
                var found = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        dynamic w = windows.Item(i);
                        if (w == null) continue;
                        if ((long)w.HWND != fg.ToInt64()) continue;
                        dynamic sel = w.Document.SelectedItems();
                        int n = sel.Count;
                        for (int j = 0; j < n; j++)
                        {
                            string p = sel.Item(j).Path;
                            if (!string.IsNullOrEmpty(p)) found.Add(p);
                        }
                        if (found.Count > 0) break; // Windows 11 tabs: the tab that has a selection
                    }
                    catch
                    {
                        // a window that is not a file window
                    }
                }
                result = found.ToArray();
            }
            catch
            {
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        if (!t.Join(2000)) return Array.Empty<string>();
        return result;
    }

    // Desktop: the shell does not list it, so Ctrl+C is sent and the copied file names are read.
    // (The clipboard is cleared first, so an old copy can never be mistaken for the selection. Text is put back.)
    private async Task<string[]> FromDesktopAsync()
    {
        string? previousText = null;
        try { if (Clipboard.ContainsText()) previousText = Clipboard.GetText(); } catch { }
        IgnoreClipboard?.Invoke(2500);
        try { Clipboard.Clear(); } catch { }

        SendCtrlC();
        await Task.Delay(180);

        string[] files = Array.Empty<string>();
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var l = Clipboard.GetFileDropList();
                files = l.Cast<string>().ToArray();
            }
        }
        catch { }

        try { if (previousText != null) Clipboard.SetText(previousText); } catch { }
        return files;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        return hwnd != IntPtr.Zero && GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static void SendCtrlC()
    {
        var inputs = new INPUT[4];
        inputs[0] = Key(0x11, false); // Ctrl down
        inputs[1] = Key(0x43, false); // C down
        inputs[2] = Key(0x43, true);  // C up
        inputs[3] = Key(0x11, true);  // Ctrl up
        SendInput(4, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = 1, // INPUT_KEYBOARD
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u } }
    };

    public void Dispose() => SetEnabled(false);

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
}
