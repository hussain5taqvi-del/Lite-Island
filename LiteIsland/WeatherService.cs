using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LiteIsland;

public sealed record WeatherInfo(string City, double TempC, double FeelsC, int Humidity,
                                 double WindKmh, int WindDir, int Code, bool IsDay, DateTime Time,
                                 double? HighC = null, double? LowC = null);

/// <summary>
/// Real weather from Open-Meteo (free, no account). The place comes from the city typed in Settings,
/// or, when that is empty, from the internet connection. Refreshes every 15 minutes (2 minutes after a
/// failure). If nothing could be fetched, <see cref="Current"/> is null and the UI shows nothing.
/// </summary>
public sealed class WeatherService : IDisposable
{
    private static readonly HttpClient Http = CreateClient();
    private readonly object _lock = new();
    private Timer? _timer;
    private string _cityKey = "";
    private double? _lat, _lon;
    private string _place = "";
    private int _busy;

    public WeatherInfo? Current { get; private set; }

    /// <summary>Raised on a background thread (Current can be null).</summary>
    public event Action<WeatherInfo?>? Changed;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LiteIsland/1.9");
        return c;
    }

    public void Start(string city)
    {
        _cityKey = (city ?? "").Trim();
        _timer = new Timer(_ => _ = RefreshAsync(), null, 1500, Timeout.Infinite);
    }

    public void SetCity(string city)
    {
        city = (city ?? "").Trim();
        if (string.Equals(city, _cityKey, StringComparison.Ordinal)) return;
        lock (_lock)
        {
            _cityKey = city;
            _lat = _lon = null;
            _place = "";
        }
        Current = null;
        Changed?.Invoke(null);
        _timer?.Change(200, Timeout.Infinite);
    }

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        string keyAtStart = _cityKey;
        bool ok = false;
        try
        {
            if (_lat == null) await ResolveLocationAsync(keyAtStart);
            if (_lat != null && _lon != null)
            {
                var inv = CultureInfo.InvariantCulture;
                string url = "https://api.open-meteo.com/v1/forecast?latitude=" + _lat.Value.ToString("0.####", inv) +
                             "&longitude=" + _lon.Value.ToString("0.####", inv) +
                             "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,wind_direction_10m,is_day&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto";
                using JsonDocument doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                JsonElement c = doc.RootElement.GetProperty("current");
                double? hi = null, lo = null;
                try
                {
                    JsonElement d = doc.RootElement.GetProperty("daily");
                    hi = d.GetProperty("temperature_2m_max")[0].GetDouble();
                    lo = d.GetProperty("temperature_2m_min")[0].GetDouble();
                }
                catch { }

                var info = new WeatherInfo(
                    _place,
                    c.GetProperty("temperature_2m").GetDouble(),
                    c.GetProperty("apparent_temperature").GetDouble(),
                    (int)Math.Round(c.GetProperty("relative_humidity_2m").GetDouble()),
                    c.GetProperty("wind_speed_10m").GetDouble(),
                    (int)Math.Round(c.GetProperty("wind_direction_10m").GetDouble()),
                    c.GetProperty("weather_code").GetInt32(),
                    c.GetProperty("is_day").GetInt32() == 1,
                    DateTime.Now, hi, lo);

                if (keyAtStart == _cityKey) // the city was not changed meanwhile
                {
                    Current = info;
                    ok = true;
                    Changed?.Invoke(info);
                }
            }
        }
        catch
        {
            // No internet or the service is down: handled below
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }

        if (!ok)
        {
            // Old data is only kept for a while, then it is dropped (never show stale weather as current)
            if (Current != null && (DateTime.Now - Current.Time).TotalMinutes > 90)
            {
                Current = null;
                Changed?.Invoke(null);
            }
            // City changed while fetching: go again at once, otherwise retry in 2 minutes
            if (keyAtStart != _cityKey) _timer?.Change(200, Timeout.Infinite);
            else _timer?.Change(TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
        }
        else
        {
            _timer?.Change(TimeSpan.FromMinutes(15), Timeout.InfiniteTimeSpan);
        }
    }

    private async Task ResolveLocationAsync(string city)
    {
        var inv = CultureInfo.InvariantCulture;
        if (city.Length > 0)
        {
            string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&name=" + Uri.EscapeDataString(city);
            using JsonDocument doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (doc.RootElement.TryGetProperty("results", out JsonElement results) && results.GetArrayLength() > 0)
            {
                JsonElement r = results[0];
                _lat = r.GetProperty("latitude").GetDouble();
                _lon = r.GetProperty("longitude").GetDouble();
                _place = r.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? city : city;
            }
        }
        else
        {
            using JsonDocument doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipwho.is/"));
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("success", out JsonElement s) && s.ValueKind == JsonValueKind.True)
            {
                _lat = root.GetProperty("latitude").GetDouble();
                _lon = root.GetProperty("longitude").GetDouble();
                _place = root.TryGetProperty("city", out JsonElement n) ? n.GetString() ?? "" : "";
            }
        }
        _ = inv;
    }

    public static string Describe(int code) => code switch
    {
        0 => "Clear sky",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Foggy",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 => "Rain showers",
        81 => "Rain showers",
        82 => "Heavy showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm, hail",
        _ => "Unknown"
    };

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
