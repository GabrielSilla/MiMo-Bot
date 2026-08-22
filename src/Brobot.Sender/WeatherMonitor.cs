using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Windows.Devices.Geolocation;

namespace Brobot.Sender;

public enum WeatherCondition { Clear, Cloudy, Rain, Storm, Snow, Fog }

public sealed record WeatherReading(int TempC, WeatherCondition Condition)
{
    /// <summary>Matches Core's WEATHER command condition names exactly — see PROTOCOL.md.</summary>
    public string CoreConditionName => Condition switch
    {
        WeatherCondition.Clear => "CLEAR",
        WeatherCondition.Cloudy => "CLOUDY",
        WeatherCondition.Rain => "RAIN",
        WeatherCondition.Storm => "STORM",
        WeatherCondition.Snow => "SNOW",
        WeatherCondition.Fog => "FOG",
        _ => "CLEAR",
    };
}

/// <summary>
/// Auto-detects location via Windows' own geolocation (once per session —
/// weather doesn't need continuous GPS-grade tracking) and periodically
/// fetches current conditions from Open-Meteo. Open-Meteo specifically
/// because it needs no API key/signup: there's no public API for the
/// weather data Windows' own taskbar widget shows (that's Microsoft's
/// private MSN backend), so this is the closest thing to a zero-friction
/// external source.
/// </summary>
public sealed class WeatherMonitor : IDisposable
{
    private static readonly HttpClient HttpClient = new();
    private const int RefreshIntervalMinutes = 30;

    private CancellationTokenSource? _cts;

    /// <summary>Raised (off the UI thread) with a fresh reading, or null when a fetch attempt failed.</summary>
    public event Action<WeatherReading?>? WeatherUpdated;

    /// <summary>Human-readable progress/error text, for a status label.</summary>
    public event Action<string>? StatusChanged;

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken token)
    {
        StatusChanged?.Invoke("Detectando localização...");

        double lat, lon;
        try
        {
            GeolocationAccessStatus access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                StatusChanged?.Invoke("Acesso à localização negado (Config. do Windows > Privacidade > Localização)");
                return;
            }

            // City-level accuracy is plenty for weather and avoids the
            // higher-power/higher-friction GPS-grade location request.
            var locator = new Geolocator { DesiredAccuracyInMeters = 10000 };
            Geoposition position = await locator.GetGeopositionAsync();
            lat = position.Coordinate.Point.Position.Latitude;
            lon = position.Coordinate.Point.Position.Longitude;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Falha ao obter localização: {ex.Message}");
            return;
        }

        while (!token.IsCancellationRequested)
        {
            await RefreshAsync(lat, lon, token);

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(RefreshIntervalMinutes), token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private async Task RefreshAsync(double lat, double lon, CancellationToken token)
    {
        try
        {
            string url = "https://api.open-meteo.com/v1/forecast?latitude="
                + lat.ToString(CultureInfo.InvariantCulture)
                + "&longitude=" + lon.ToString(CultureInfo.InvariantCulture)
                + "&current_weather=true";

            using HttpResponseMessage response = await HttpClient.GetAsync(url, token);
            response.EnsureSuccessStatusCode();

            using Stream stream = await response.Content.ReadAsStreamAsync(token);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);

            JsonElement current = doc.RootElement.GetProperty("current_weather");
            int tempC = (int)Math.Round(current.GetProperty("temperature").GetDouble());
            int code = current.GetProperty("weathercode").GetInt32();

            var reading = new WeatherReading(tempC, MapWeatherCode(code));
            StatusChanged?.Invoke($"{reading.TempC}°C — atualizado agora");
            WeatherUpdated?.Invoke(reading);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusChanged?.Invoke($"Falha ao buscar clima: {ex.Message}");
            WeatherUpdated?.Invoke(null);
        }
    }

    // WMO weather codes, as returned by Open-Meteo's "weathercode" field —
    // grouped down to the handful of pictograms Core actually has.
    private static WeatherCondition MapWeatherCode(int code) => code switch
    {
        0 => WeatherCondition.Clear,
        1 or 2 or 3 => WeatherCondition.Cloudy,
        45 or 48 => WeatherCondition.Fog,
        51 or 53 or 55 or 56 or 57 => WeatherCondition.Rain,  // drizzle
        61 or 63 or 65 or 66 or 67 => WeatherCondition.Rain,
        71 or 73 or 75 or 77 => WeatherCondition.Snow,
        80 or 81 or 82 => WeatherCondition.Rain,              // showers
        85 or 86 => WeatherCondition.Snow,                    // snow showers
        95 or 96 or 99 => WeatherCondition.Storm,
        _ => WeatherCondition.Clear,
    };
}
