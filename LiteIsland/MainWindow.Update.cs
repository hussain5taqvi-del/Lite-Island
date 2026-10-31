using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace LiteIsland;

public partial class MainWindow
{
    private DispatcherTimer? _updTimer;
    private bool _updBusy;

    /// <summary>One line for the Settings window ("Version 1.6.2 - up to date").</summary>
    public string UpdateStatus { get; private set; } = "";
    /// <summary>A newer version is installed on disk and starts at the next launch.</summary>
    public bool UpdateReady { get; private set; }
    public event Action? UpdateStatusChanged;

    private void InitUpdates()
    {
        Updater.CleanOld();
        UpdateStatus = "Version " + Updater.Current + " - up to date";
        _updTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) }; // not while Windows is still starting
        _updTimer.Tick += (_, _) =>
        {
            _updTimer.Interval = TimeSpan.FromHours(12);
            if (_s.AutoUpdate && !UpdateReady) _ = CheckUpdateAsync(false);
        };
        _updTimer.Start();
    }

    private void SetUpdateStatus(string s)
    {
        UpdateStatus = s;
        UpdateStatusChanged?.Invoke();
    }

    /// <param name="manual">true = the user pressed "Check now"</param>
    public async Task CheckUpdateAsync(bool manual)
    {
        if (_updBusy || UpdateReady) return;
        _updBusy = true;
        try
        {
            SetUpdateStatus("Checking...");
            Updater.Info? info = await Updater.CheckAsync(System.Threading.CancellationToken.None);
            if (info == null)
            {
                SetUpdateStatus("Version " + Updater.Current + " - up to date");
                return;
            }
            if (!Updater.CanWriteHere())
            {
                SetUpdateStatus("Version " + info.Version + " is available (download it from GitHub: this folder is read-only)");
                if (manual) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(info.PageUrl) { UseShellExecute = true });
                return;
            }
            SetUpdateStatus("Downloading " + info.Version + "...");
            string? err = await Updater.InstallAsync(info, p => Dispatcher.BeginInvoke(new Action(() => SetUpdateStatus("Downloading " + info.Version + "  " + (int)(p * 100) + "%"))), System.Threading.CancellationToken.None);
            if (err != null)
            {
                SetUpdateStatus("Update failed: " + err);
                return;
            }
            UpdateReady = true;
            SetUpdateStatus("Version " + info.Version + " is installed - restart to use it");
            ShowHud("\uE895", "Update " + info.Version + " ready", null, "restart", 2600);
        }
        catch (Exception ex)
        {
            SetUpdateStatus(manual ? "Could not check for updates (" + ex.Message + ")" : "Version " + Updater.Current + " - up to date");
        }
        finally
        {
            _updBusy = false;
        }
    }
}
