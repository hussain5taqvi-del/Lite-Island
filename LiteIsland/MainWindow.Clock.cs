using System;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LiteIsland;

// Clock (collapsed island + Clock page).
public partial class MainWindow
{

    private readonly DispatcherTimer _clockTimer = new();
    private string _timeStr = "";
    private string _ampm = "";
    private string _timeShown = "\u0000"; // what TimeText currently shows (avoids rebuilding it)
    private string _curTitle = "";

    // ---------- Setup ----------

    private void InitClock()
    {
        UpdateClock();

        // The clock timer ticks once per minute, right when the minute changes
        _clockTimer.Tick += (_, _) =>
        {
            UpdateClock();
            ScheduleClock();
        };
        ScheduleClock();
    }

    // ---------- Clock ----------

    private void ScheduleClock()
    {
        DateTime n = DateTime.Now;
        double ms = 60000 - (n.Second * 1000 + n.Millisecond) + 100;
        _clockTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _clockTimer.Start();
    }

    /// <summary>Called by the settings window when 12h / 24h changes.</summary>
    public void RefreshClock() => UpdateClock();

    private void UpdateClock()
    {
        DateTime now = DateTime.Now;
        CultureInfo inv = CultureInfo.InvariantCulture;

        _timeStr = now.ToString(_s.Use24Hour ? "HH:mm" : "h:mm", inv);
        _ampm = _s.Use24Hour ? "" : now.ToString("tt", inv).ToUpperInvariant();
        UpdateCenter();
        ClockBig.Text = now.ToString(_s.Use24Hour ? "HH:mm" : "h:mm tt", inv);
        ClockDate.Text = now.ToString("dddd, d MMMM yyyy", inv);
    }

    // Middle of the collapsed island: the song name while music plays, otherwise the time
    private void UpdateCenter()
    {
        bool song = _playing && !string.IsNullOrWhiteSpace(_curTitle);
        SongText.Text = _curTitle;
        ShowTime();
        SongText.Visibility = song ? Visibility.Visible : Visibility.Collapsed;
        IdlePanel.Visibility = song ? Visibility.Collapsed : Visibility.Visible;
    }

    // Big time with a small AM / PM next to it
    private void ShowTime()
    {
        string ap = _hideAmPm ? "" : _ampm;
        string key = _timeStr + "|" + ap;
        if (key == _timeShown) return;
        _timeShown = key;

        TimeText.Inlines.Clear();
        TimeText.Inlines.Add(new Run(_timeStr));
        if (ap.Length > 0)
        {
            var r = new Run(" " + ap) { FontSize = 11, FontWeight = FontWeights.SemiBold };
            r.SetResourceReference(TextElement.ForegroundProperty, "TextDimBrush");
            TimeText.Inlines.Add(r);
        }
    }
}
