using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LiteIsland;

/// <summary>
/// Tells the island when the clipboard text changes (event driven, no polling) and which program copied it.
/// Anything a password manager marks as private is skipped.
/// </summary>
internal sealed class ClipboardService : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private HwndSource? _sink;
    private long _ignoreUntil;
    private bool _on;

    /// <summary>text, name of the program that copied it. Raised on the UI thread.</summary>
    public event Action<string, string>? TextCopied;

    public ClipboardService(Dispatcher ui)
    {
        _ui = ui;
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            ReadClipboard();
        };
    }

    public void SetEnabled(bool on)
    {
        if (on == _on) return;
        _on = on;
        if (on)
        {
            var p = new HwndSourceParameters("LiteIslandClip") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 };
            _sink = new HwndSource(p);
            _sink.AddHook(WndProc);
            AddClipboardFormatListener(_sink.Handle);
        }
        else if (_sink != null)
        {
            try { RemoveClipboardFormatListener(_sink.Handle); } catch { }
            _sink.RemoveHook(WndProc);
            _sink.Dispose();
            _sink = null;
        }
    }

    /// <summary>Changes made by LiteIsland itself (copy text, copy a clicked history item) are not listed again.</summary>
    public void IgnoreFor(int ms) => _ignoreUntil = Environment.TickCount64 + ms;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            // Programs often change the clipboard several times in a row: wait a moment, read once
            _debounce.Stop();
            _debounce.Start();
        }
        return IntPtr.Zero;
    }

    private void ReadClipboard()
    {
        if (Environment.TickCount64 < _ignoreUntil) return;

        // The program that owns the clipboard right now (read first, it can change)
        string source = OwnerName();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                IDataObject? data = Clipboard.GetDataObject();
                if (data == null) return;

                // Password managers ask Windows not to record their copies
                if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return;
                if (data.GetDataPresent("CanIncludeInClipboardHistory"))
                {
                    try
                    {
                        if (data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4 &&
                            BitConverter.ToInt32(ms.ToArray(), 0) == 0) return;
                    }
                    catch { }
                }

                if (!data.GetDataPresent(DataFormats.UnicodeText)) return;
                string? text = data.GetData(DataFormats.UnicodeText) as string;
                if (string.IsNullOrWhiteSpace(text)) return;
                TextCopied?.Invoke(text!, source);
                return;
            }
            catch (COMException)
            {
                // another program has the clipboard open: try again in a moment
                System.Threading.Thread.Sleep(40);
            }
            catch
            {
                return;
            }
        }
    }

    private static string OwnerName()
    {
        try
        {
            IntPtr owner = GetClipboardOwner();
            if (owner == IntPtr.Zero) return "Clipboard";
            GetWindowThreadProcessId(owner, out uint pid);
            if (pid == 0) return "Clipboard";

            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return "Clipboard";
            try
            {
                var sb = new StringBuilder(520);
                int size = sb.Capacity;
                if (!QueryFullProcessImageName(h, 0, sb, ref size)) return "Clipboard";
                string name = Path.GetFileNameWithoutExtension(sb.ToString());
                if (name.Length == 0) return "Clipboard";
                return char.ToUpperInvariant(name[0]) + name.Substring(1);
            }
            finally
            {
                CloseHandle(h);
            }
        }
        catch
        {
            return "Clipboard";
        }
    }

    public void Dispose() => SetEnabled(false);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
}
