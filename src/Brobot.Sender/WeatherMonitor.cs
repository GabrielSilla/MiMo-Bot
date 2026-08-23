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

/// <summary>
/// Caring, casual PT-BR heads-up per weather condition — "going out? don't
/// forget the umbrella" rather than a bare "the weather changed" — shown
/// once by MainWindow.OnWeatherUpdated when a fresh reading's condition
/// actually differs from the previous one, not on every 30-min poll.
/// Deliberately accent-free, same convention as every other Core-bound
/// message in this codebase (see mimo-claude-hook.ps1, Personality.cpp's
/// BEDTIME_MESSAGES) — Font5x7 renders lowercase accents fine, but nothing
/// else here uses them, so this doesn't either.
/// </summary>
public static class WeatherAlerts
{
    private static readonly Random Rng = new();

    private static readonly Dictionary<WeatherCondition, string[]> Messages = new()
    {
        [WeatherCondition.Rain] = new[]
        {
            "Vai sair? Nao esquece o guarda-chuva!",
            "Ei, vai chover... leva uma capa ai",
            "Psiu, hora de guarda-chuva, hein",
            "Cuidado la fora, o chao deve ficar escorregadio",
            "Vai molhar sim, se prepara!",
            "Bora com o guarda-chuva na mochila, vai chover",
            "Aviso de amigo: leva algo pra chuva",
            "Ta vindo chuva, nao esquece de se agasalhar tambem",
            "Se for sair, nao esquece o guarda-chuva, viu?",
            "Chuva chegando, fica esperto pra nao se molhar",
        },
        [WeatherCondition.Storm] = new[]
        {
            "Opa, vem tempestade! Melhor ficar em casa se der",
            "Cuidado, temporal a caminho, evita sair se possivel",
            "Vem chuva forte, desliga os aparelhos por seguranca",
            "Fica de olho, tempestade rondando por ai",
            "Se puder, adia a saida... vem temporal",
            "Trovoada a vista, se cuida ai fora",
            "Melhor carregar tudo antes que a luz falte, vem tempestade",
            "Segura essa: vem tempestade forte, fica atento",
            "Vai ser feio la fora, tempestade chegando",
            "Se for sair, cuidado com o vento, vem temporal",
        },
        [WeatherCondition.Snow] = new[]
        {
            "Vai nevar! Agasalha bem antes de sair",
            "Frio de neve chegando, nao esquece o casaco",
            "Fica quentinho ai, vai nevar",
            "Vai sair? Bota luva e cachecol, ta nevando",
            "Neve a caminho, cuidado com o gelo no chao",
            "Se abriga direitinho, vai nevar",
            "Ta friozinho de neve, se agasalha bem",
            "Vem neve, esquenta esse coracao (e o corpo tambem)",
            "Nao esquece as botas, vai nevar la fora",
            "Fica em casa se puder, ta nevando bonito",
        },
        [WeatherCondition.Cloudy] = new[]
        {
            "Vai ficar nublado, mas nada que te impeca de sair",
            "Ceu meio cinza hoje, leva uma jaqueta leve",
            "Nublou! Talvez de uma tregua no sol, aproveita",
            "Vai ficar nublado, bom dia pra passear sem calor",
            "Dia nublado chegando, clima bom pra ficar tranquilo",
            "Sem sol forte hoje, mas fica de olho no tempo",
            "Ceu fechou um pouco, nada grave por enquanto",
            "Nublado por ai, leva um casaquinho por garantia",
            "Tempo mudou pra nublado, dia mais ameno chegando",
            "Ficou cinza o ceu, mas nada de chuva por enquanto",
        },
        [WeatherCondition.Clear] = new[]
        {
            "Vai fazer sol! Nao esquece o protetor solar",
            "Sol chegando, leva agua pra se hidratar",
            "Dia de sol! Bota o oculos escuro ai",
            "Ta abrindo o tempo, aproveita pra tomar um solzinho",
            "Vai fazer sol, boa desculpa pra sair um pouco",
            "Sol a vista, nao esquece o bone",
            "Ceu limpou! Otimo dia pra dar uma volta",
            "Fazendo sol la fora, se hidrata bem",
            "Abriu o sol, aproveita o dia bonito",
            "Vai fazer sol, mas nao esquece de se cuidar do calor",
        },
        [WeatherCondition.Fog] = new[]
        {
            "Vai ficar com neblina, dirige com cuidado",
            "Nevoa chegando, atencao redobrada se for sair de carro",
            "Visibilidade baixa vindo ai, se cuida no transito",
            "Ta enevoado, vai com calma se for sair",
            "Neblina na area, liga o farol se for dirigir",
            "Vixe, baixou a neblina, cuidado pra sair de casa",
            "Tempo fechado de neblina, se cuida ai fora",
            "Vem nevoa, reduz a velocidade se for de carro",
            "Neblina chegando, fica esperto no caminho",
            "Ta com pouca visibilidade la fora, atencao redobrada",
        },
    };

    public static string RandomFor(WeatherCondition condition)
    {
        string[] pool = Messages[condition];
        return pool[Rng.Next(pool.Length)];
    }
}
