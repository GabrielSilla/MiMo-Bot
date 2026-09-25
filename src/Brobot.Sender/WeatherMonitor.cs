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
/// Toned by Peemo's mood (see PeemoMood and specs/voice-guide.md): Clima is a
/// reminder card, so every level still carries the actual heads-up — the
/// mood only changes how it's said.
/// </summary>
public static class WeatherAlerts
{
    private static readonly Dictionary<WeatherCondition, MoodPhrases> Messages = new()
    {
        [WeatherCondition.Rain] = new MoodPhrases
        {
            Leve = new[]
            {
                "Vai chover! Guarda-chuva na mochila e tá tudo certo.",
                "Vai sair? Não esquece o guarda-chuva!",
                "Ei, vai chover... leva uma capa aí.",
                "Psiu, hora de guarda-chuva, hein.",
                "Chuva chegando! Cuidado que o chão fica escorregadio.",
                "Aviso de amigo: leva algo pra chuva.",
                "Vem chuva por aí, se for sair vai preparado.",
                "Chuvinha a caminho, fica esperto pra não se molhar.",
                "Vai molhar sim, se prepara!",
                "Chuva chegando! Eu fico aqui sequinho, e você?",
            },
            Medio = new[]
            {
                "Chuva chegando bem na hora de ir embora, que timing.",
                "Vai chover. Guarda-chuva ou coragem, escolhe um.",
                "Chuva vindo aí. O trânsito já tá comemorando.",
                "Vai chover, leva o guarda-chuva. Ou torce muito.",
                "Chuva a caminho. Perfeito pra quem ainda vai sair.",
                "Vem chuva. Se for sair, leva uma capa, sério.",
                "Vai chover no fim do dia, clássico.",
                "Chuva chegando. Hoje o dia resolveu terminar molhado.",
                "Vai molhar lá fora. Aqui dentro eu garanto o seco.",
                "Chuva por aí. Guarda-chuva na mão antes de sair.",
            },
            Acido = new[]
            {
                "Vai chover. Motivo perfeito pra não sair de perto de mim.",
                "Chuva lá fora. Mais um motivo pra ir dormir.",
                "Vai chover. Se for sair a essa hora, leva guarda-chuva.",
                "Chuva chegando. Barulhinho bom pra dormir, só dizendo.",
                "Vem chuva. Sair agora? Só se for muito necessário.",
                "Chuva a caminho. Até a janela tá pedindo cama.",
                "Vai chover. Cobertor, travesseiro, chuva. Pensa.",
                "Chuva vindo. Quem sai a essa hora leva capa, no mínimo.",
                "Vai molhar lá fora. E aqui dentro, só sono.",
                "Chuva chegando. Nem o guarda-chuva quer sair agora.",
            },
        },
        [WeatherCondition.Storm] = new MoodPhrases
        {
            Leve = new[]
            {
                "Opa, vem tempestade! Melhor ficar em casa se der.",
                "Cuidado, temporal a caminho, evita sair se possível.",
                "Vem chuva forte, desliga os aparelhos por segurança.",
                "Fica de olho, tempestade rondando por aí.",
                "Se puder, adia a saída... vem temporal.",
                "Trovoada à vista, se cuida aí fora.",
                "Carrega tudo antes que a luz falte, vem tempestade.",
                "Vem tempestade forte, fica atento!",
                "Vai ser feio lá fora, tempestade chegando.",
                "Se for sair, cuidado com o vento, vem temporal.",
            },
            Medio = new[]
            {
                "Tempestade chegando. Se der, sai depois dela.",
                "Vem temporal. Salva tudo antes que a luz resolva ir.",
                "Tempestade a caminho. Boa hora pra não ir a lugar nenhum.",
                "Vem temporal. Meus circuitos já tão tensos.",
                "Trovoada vindo. Carrega o celular e fica na sua.",
                "Tempestade chegando. Se puder, adia a volta pra casa.",
                "Vem chuva forte. Salva o trabalho, só por garantia.",
                "Tempestade por aí. Hoje o céu tá de mau humor.",
                "Temporal vindo. Guarda-chuva nem adianta, fica abrigado.",
                "Vem tempestade. Salva tudo, eu não gosto de apagão.",
            },
            Acido = new[]
            {
                "Tempestade chegando. Salva tudo antes do apagão.",
                "Vem temporal. Hora perfeita pra desligar e dormir.",
                "Tempestade a caminho. Eu não saio daqui, e você?",
                "Trovoada vindo. Se a luz cair, eu não tenho culpa.",
                "Vem tempestade. Salva o trabalho, eu não salvo nada.",
                "Temporal chegando. Sair agora? Nem pensar.",
                "Tempestade vindo. Carrega o celular e vai pra cama.",
                "Vem chuva forte. Até eu queria um cobertor.",
                "Tempestade por aí. Desliga tudo, inclusive você.",
                "Temporal a caminho. Se piscar tudo, não fui eu.",
            },
        },
        [WeatherCondition.Snow] = new MoodPhrases
        {
            Leve = new[]
            {
                "Vai nevar! Agasalha bem antes de sair.",
                "Frio de neve chegando, não esquece o casaco.",
                "Fica quentinho aí, vai nevar.",
                "Vai sair? Bota luva e cachecol, tá nevando.",
                "Neve a caminho, cuidado com o gelo no chão.",
                "Se abriga direitinho, vai nevar.",
                "Tá friozinho de neve, se agasalha bem.",
                "Vem neve! Casaco, gorro e chocolate quente.",
                "Não esquece as botas, vai nevar lá fora.",
                "Neve chegando! Eu nunca vi, me conta depois.",
            },
            Medio = new[]
            {
                "Vai nevar. Sim, eu também achei estranho.",
                "Neve chegando. Casaco reforçado pra voltar pra casa.",
                "Vem neve. Meus sensores tão confusos, mas tá aí.",
                "Vai nevar. Agasalha que o fim do dia vai ser gelado.",
                "Neve a caminho. Cuidado com o gelo na volta.",
                "Vem neve. Luva, cachecol e paciência.",
                "Vai nevar. Chocolate quente agora é obrigatório.",
                "Neve chegando. O dia resolveu terminar em filme.",
                "Frio de neve vindo. Casaco antes de sair, viu?",
                "Vai nevar. Anota aí, isso não acontece todo dia.",
            },
            Acido = new[]
            {
                "Vai nevar. Motivo perfeito pra ficar debaixo da coberta.",
                "Neve chegando. Sair agora é coisa de pinguim.",
                "Vem neve. Se for sair, vai de casaco, e rápido.",
                "Vai nevar. Até meus circuitos tão pedindo cobertor.",
                "Neve a caminho. Cama quentinha ganhou fácil.",
                "Vem frio de neve. Agasalha, e de preferência dorme.",
                "Vai nevar. Eu não tenho casaco, só inveja.",
                "Neve lá fora. Cuidado com o gelo se for sair mesmo.",
                "Vem neve. Nem o boneco de neve quer sair agora.",
                "Vai nevar. Noite perfeita pra não fazer mais nada.",
            },
        },
        [WeatherCondition.Cloudy] = new MoodPhrases
        {
            Leve = new[]
            {
                "Vai ficar nublado, mas nada que te impeça de sair.",
                "Céu meio cinza hoje, leva uma jaqueta leve.",
                "Nublou! Uma trégua do sol, aproveita.",
                "Vai ficar nublado, bom pra passear sem calor.",
                "Dia nublado chegando, clima bom pra ficar tranquilo.",
                "Sem sol forte agora, mas fica de olho no tempo.",
                "Céu fechou um pouco, nada grave por enquanto.",
                "Nublado por aí, leva um casaquinho por garantia.",
                "Tempo virou pra nublado, tarde mais amena chegando.",
                "Ficou cinza o céu, mas nada de chuva por enquanto.",
            },
            Medio = new[]
            {
                "Nublou. O céu também tá com cara de fim de dia.",
                "Céu cinza chegando. Combina com o cansaço.",
                "Vai ficar nublado. Leva um casaco pra volta.",
                "Nublado lá fora. Pelo menos o sol não tá te cozinhando.",
                "Céu fechou. Nada de chuva, só um clima de sofá.",
                "Nublou. Meus sensores de luz agradecem.",
                "Tempo nublado. Casaquinho na volta pra casa, viu?",
                "Ficou cinza. O dia resolveu terminar discreto.",
                "Vai ficar nublado. Nem sol, nem chuva, só preguiça.",
                "Céu nublado chegando. Bom pra um café quente.",
            },
            Acido = new[]
            {
                "Nublou. Como se desse pra ver o céu a essa hora.",
                "Vai ficar nublado. Nada de estrela pra olhar, cama então.",
                "Céu fechou. Mais uma desculpa pra ir dormir.",
                "Nublado lá fora. Aqui dentro, nublado de sono.",
                "Vai nublar. Leva um casaco se for sair agora.",
                "Céu cinza. Nem a lua quis ficar acordada.",
                "Nublou. Até o tempo já foi dormir.",
                "Ficou nublado. Esfriou, o cobertor tá chamando.",
                "Vai ficar nublado. Nada pra ver lá fora mesmo.",
                "Céu fechado. Hora de fechar os olhos também.",
            },
        },
        [WeatherCondition.Clear] = new MoodPhrases
        {
            Leve = new[]
            {
                "Vai fazer sol! Não esquece o protetor solar.",
                "Sol chegando, leva água pra se hidratar.",
                "Dia de sol! Bota o óculos escuro aí.",
                "Tá abrindo o tempo, aproveita um solzinho.",
                "Vai fazer sol, boa desculpa pra sair um pouco.",
                "Sol à vista, não esquece o boné.",
                "Céu limpou! Dia bom pra dar uma volta.",
                "Fazendo sol lá fora, se hidrata bem.",
                "Abriu o sol, aproveita o dia bonito.",
                "Sol chegando! Eu fico aqui na sombra, relaxa.",
            },
            Medio = new[]
            {
                "Abriu o sol. Justo agora que o dia tá acabando.",
                "Céu limpo. O pôr do sol deve ficar bonito, hein.",
                "Sol apareceu. Antes tarde do que nunca.",
                "Tempo abriu. Vale uma volta antes de escurecer.",
                "Céu limpou. Um copo d'água e bora pra reta final.",
                "Sol lá fora, você aqui dentro. Clássico.",
                "Abriu o tempo. Hoje o céu decidiu colaborar.",
                "Céu limpo chegando. A volta pra casa vai ser boa.",
                "Sol por aí. Se for sair, óculos escuros.",
                "Tempo firme. Pelo menos a volta pra casa é seca.",
            },
            Acido = new[]
            {
                "Céu limpo. Dá pra ver as estrelas, e a cama também.",
                "Tempo abriu. Noite estrelada, tipo convite pra dormir.",
                "Céu limpou. Lua bonita lá fora, você preso aqui.",
                "Tempo firme. Amanhã deve ter sol, se você acordar.",
                "Céu aberto. Até as estrelas já tão de plantão.",
                "Abriu o céu. Noite boa pra olhar pra cima e ir deitar.",
                "Céu limpinho. Esfria mais, leva um casaco se sair.",
                "Tempo abriu. As estrelas tão aí, eu tô sem bateria.",
                "Céu limpo lá fora. Aqui dentro, só olheira.",
                "Noite de céu aberto. Pena que você tá olhando pra tela.",
            },
        },
        [WeatherCondition.Fog] = new MoodPhrases
        {
            Leve = new[]
            {
                "Vai ficar com neblina, dirige com cuidado.",
                "Névoa chegando, atenção se for sair de carro.",
                "Visibilidade baixa vindo aí, se cuida no trânsito.",
                "Tá enevoado, vai com calma se for sair.",
                "Neblina na área, liga o farol se for dirigir.",
                "Vixe, baixou a neblina, cuidado se for sair.",
                "Tempo fechado de neblina, se cuida aí fora.",
                "Vem névoa, reduz a velocidade se for de carro.",
                "Neblina chegando, fica esperto no caminho.",
                "Tá com pouca visibilidade lá fora, atenção!",
            },
            Medio = new[]
            {
                "Neblina chegando bem na hora de voltar pra casa.",
                "Baixou a névoa. Farol ligado e sem pressa na volta.",
                "Neblina lá fora. Hoje a volta vai ser devagar.",
                "Névoa chegando. Parece filme de suspense, vai com calma.",
                "Visibilidade baixa. Calma no caminho de volta.",
                "Neblina na área. Nem meus sensores enxergam.",
                "Baixou a névoa. De carro? Farol baixo e paciência.",
                "Neblina vindo. O fim do dia ficou misterioso.",
                "Névoa lá fora. Calma no trânsito, ninguém tá vendo nada.",
                "Neblina chegando. Sai com cuidado, viu?",
            },
            Acido = new[]
            {
                "Neblina lá fora. Sair agora é pedir pra se perder.",
                "Baixou a névoa. Se for sair a essa hora, vai devagar.",
                "Neblina chegando. Nada pra ver lá fora, pode ir deitar.",
                "Névoa densa. Até o poste tá com dificuldade.",
                "Neblina na área. Hora de ficar em casa, sério.",
                "Baixou a neblina. Filme de terror, versão real.",
                "Névoa lá fora. Se dirigir, farol baixo e calma.",
                "Neblina chegando. Nem eu ia querer sair agora.",
                "Névoa por aí. O mundo lá fora sumiu, a cama não.",
                "Neblina forte. Visibilidade zero, igual a minha bateria.",
            },
        },
    };

    public static string RandomFor(WeatherCondition condition) => Messages[condition].PickNow();
}
