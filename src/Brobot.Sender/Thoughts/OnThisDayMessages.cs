using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Brobot.Sender.Thoughts;

/// <summary>
/// "Neste dia" historical events from Wikipedia's own feed, natively in
/// PT-BR (Wikimedia's onthisday API, free with no key). The day's
/// "selected" events are fetched once and cached (thought-onthisday.json);
/// if none of them is short and light enough, the much longer "events"
/// list is the fallback. At most one per day. Space events are marked by
/// SpaceTopic and go out with Core's space animation.
/// </summary>
internal sealed class OnThisDayMessages : IThoughtSource, IBackgroundThoughtSource
{
    private const string CacheFile = "thought-onthisday.json";
    private static readonly TimeSpan RefreshCheckInterval = TimeSpan.FromMinutes(30);

    private static readonly Regex Grim = new(
        @"(?<![\p{L}])(mort\w*|morre\w*|assassin\w*|guerra\w*|massacre\w*|ataque\w*|atentado\w*|bomba\w*|genoc\w*|execu\w*|terremoto\w*|tsunami\w*|naufr\w*|explos\w*|incêndio\w*|vítima\w*|tirote\w*|golpe\w*|ditadura\w*|nazis\w*|holocausto|escrav\w*|suic\w*|queda\w*|acidente\w*|epidemia\w*|pandemia\w*|invas\w*|bombarde\w*|terror\w*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed class Event
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public string? SpaceFace { get; set; }
    }

    public sealed class Cache
    {
        public DateOnly Day { get; set; }
        public List<Event> Events { get; set; } = new();
    }

    private readonly ThoughtHistoryStore _history;
    private readonly object _lock = new();
    private readonly Random _rng = new();
    private Cache _cache;

    public string Name => "onthisday";
    public double Weight => 15;

    public OnThisDayMessages(ThoughtHistoryStore history)
    {
        _history = history;
        _cache = ThoughtHttp.LoadJson<Cache>(CacheFile) ?? new Cache();
    }

    public Thought? TryPick(ThoughtContext context)
    {
        if (_history.CountToday(Name) >= 1)
        {
            return null;
        }
        lock (_lock)
        {
            if (_cache.Day != DateOnly.FromDateTime(context.Now))
            {
                return null;
            }
            var unused = _cache.Events.Where(e => !_history.Data.UsedAt.ContainsKey(e.Id)).ToList();
            if (unused.Count == 0)
            {
                return null;
            }
            Event pick = unused[_rng.Next(unused.Count)];
            return new Thought(pick.SpaceFace ?? "NEUTRAL", pick.Text, Name, "neste-dia", pick.Id);
        }
    }

    public void Start() => _ = RefreshLoopAsync();

    private async Task RefreshLoopAsync()
    {
        while (true)
        {
            try
            {
                DateOnly today = DateOnly.FromDateTime(DateTime.Now);
                bool stale;
                lock (_lock)
                {
                    stale = _cache.Day != today;
                }
                if (stale)
                {
                    await FetchAsync(today);
                }
            }
            catch (Exception)
            {
                // Offline — try again on the next round.
            }
            await Task.Delay(RefreshCheckInterval);
        }
    }

    private async Task FetchAsync(DateOnly day)
    {
        List<Event> events = await FetchKindAsync(day, "selected");
        if (events.Count == 0)
        {
            events = await FetchKindAsync(day, "events");
        }
        lock (_lock)
        {
            _cache = new Cache { Day = day, Events = events };
            ThoughtHttp.SaveJson(CacheFile, _cache);
        }
    }

    private static async Task<List<Event>> FetchKindAsync(DateOnly day, string kind)
    {
        string url = $"https://api.wikimedia.org/feed/v1/wikipedia/pt/onthisday/{kind}/{day.Month:00}/{day.Day:00}";
        using HttpResponseMessage response = await ThoughtHttp.Client.GetAsync(url);
        var result = new List<Event>();
        if (!response.IsSuccessStatusCode)
        {
            return result;
        }

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty(kind, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement item in list.EnumerateArray())
        {
            string? text = item.TryGetProperty("text", out JsonElement t) ? t.GetString()?.Trim() : null;
            int? year = item.TryGetProperty("year", out JsonElement y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null;
            if (string.IsNullOrEmpty(text) || year == null || Grim.IsMatch(text))
            {
                continue;
            }

            text = text.TrimEnd('.');
            // After the colon a lowercase start reads naturally — and for an
            // accented capital (É, Ó...) it's the only way it keeps its
            // accent on screen, since the font only has lowercase accents.
            if (text.Length > 1 && "ÃÁÀÂÉÊÍÓÔÕÚ".Contains(text[0]))
            {
                text = char.ToLowerInvariant(text[0]) + text[1..];
            }

            if (ThoughtText.Accept($"Neste dia, em {year}: {text}.") is { } line)
            {
                result.Add(new Event
                {
                    Id = $"otd:{day:MM-dd}:{year}:{StableHash(text):x8}",
                    Text = line,
                    SpaceFace = SpaceTopic.ClassifyPortuguese(text),
                });
            }
        }
        return result;
    }

    // FNV-1a: string.GetHashCode is randomized per process on .NET, and
    // these ids are persisted in ThoughtHistory.UsedAt across restarts.
    private static uint StableHash(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash = (hash ^ c) * 16777619;
        }
        return hash;
    }
}
