using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Brobot.Sender.Thoughts;

/// <summary>
/// Random facts from uselessfacts.jsph.pl (English), translated to PT-BR
/// via MyMemory — both free with no key. A small stock of already
/// translated facts lives on disk (thought-facts.json) and is refilled in
/// the background, so TryPick only ever reads the stock. Facts are
/// filtered *before* translating: short enough in English (Portuguese runs
/// 15–20% longer) and nothing grim — uselessfacts does return some.
/// Space-themed facts are marked SATELLITE/SPACE by SpaceTopic on the
/// English text, and then go out with Core's space animation.
/// </summary>
internal sealed class RandomFactsMessages : IThoughtSource, IBackgroundThoughtSource
{
    private const string StockFile = "thought-facts.json";
    private const int TargetStock = 8;
    private const int RefillBelow = 5;
    private const int MaxEnglishLength = 70;
    // MyMemory allows ~5,000 chars/day anonymously; a fact is ~70, so this
    // stays far below it even on a day of failed/rejected attempts.
    private const int MaxTranslationsPerDay = 20;
    private static readonly TimeSpan RefillCheckInterval = TimeSpan.FromMinutes(30);

    private static readonly Regex Grim = new(
        @"\b(kill|killed|kills|killing|death|deaths|dead|die|died|dies|dying|murder|suicide|war|wars|blood|bomb|gun|guns|shot|shoot|cancer|disease|rape|abuse|drug|drugs|prison|execut\w*|slave\w*|nazi|hitler|corpse|torture|sex|sexual|naked|penis|vagina|genocide|terror\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed class Fact
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public string? SpaceFace { get; set; }
    }

    public sealed class Stock
    {
        public List<Fact> Facts { get; set; } = new();
        public DateOnly TranslationsDay { get; set; }
        public int TranslationsToday { get; set; }
    }

    private readonly ThoughtHistoryStore _history;
    private readonly object _lock = new();
    private readonly Stock _stock;

    public string Name => "facts";
    public double Weight => 15;

    public RandomFactsMessages(ThoughtHistoryStore history)
    {
        _history = history;
        _stock = ThoughtHttp.LoadJson<Stock>(StockFile) ?? new Stock();
    }

    public Thought? TryPick(ThoughtContext context)
    {
        lock (_lock)
        {
            if (_stock.Facts.Count == 0)
            {
                return null;
            }
            Fact fact = _stock.Facts[0];
            _stock.Facts.RemoveAt(0);
            ThoughtHttp.SaveJson(StockFile, _stock);
            return new Thought(fact.SpaceFace ?? "NEUTRAL", fact.Text, Name, "fato", "fact:" + fact.Id);
        }
    }

    public void Start() => _ = RefillLoopAsync();

    private async Task RefillLoopAsync()
    {
        while (true)
        {
            try
            {
                await RefillAsync();
            }
            catch (Exception)
            {
                // Offline or the API changed — the stock just waits for the next round.
            }
            await Task.Delay(RefillCheckInterval);
        }
    }

    private async Task RefillAsync()
    {
        lock (_lock)
        {
            if (_stock.Facts.Count >= RefillBelow)
            {
                return;
            }
        }

        // Bounded so a run of rejected facts can't turn into a request storm.
        for (int attempt = 0; attempt < 15; attempt++)
        {
            lock (_lock)
            {
                DateOnly today = DateOnly.FromDateTime(DateTime.Now);
                if (_stock.TranslationsDay != today)
                {
                    _stock.TranslationsDay = today;
                    _stock.TranslationsToday = 0;
                }
                if (_stock.Facts.Count >= TargetStock || _stock.TranslationsToday >= MaxTranslationsPerDay)
                {
                    return;
                }
            }

            (string id, string english)? fetched = await FetchEnglishFactAsync();
            if (fetched is { } f && IsUsable(f.id, f.english))
            {
                string? portuguese = await TranslateAsync(f.english);
                lock (_lock)
                {
                    _stock.TranslationsToday++;
                    if (portuguese != null && ThoughtText.Accept(portuguese) is { } text)
                    {
                        _stock.Facts.Add(new Fact { Id = f.id, Text = text, SpaceFace = SpaceTopic.ClassifyEnglish(f.english) });
                    }
                    ThoughtHttp.SaveJson(StockFile, _stock);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(3)); // be polite to both free APIs
        }
    }

    private bool IsUsable(string id, string english)
    {
        if (english.Length > MaxEnglishLength || Grim.IsMatch(english))
        {
            return false;
        }
        lock (_lock)
        {
            return _stock.Facts.All(x => x.Id != id) && !_history.Data.UsedAt.ContainsKey("fact:" + id);
        }
    }

    private static async Task<(string id, string english)?> FetchEnglishFactAsync()
    {
        using HttpResponseMessage response = await ThoughtHttp.Client.GetAsync(
            "https://uselessfacts.jsph.pl/api/v2/facts/random?language=en");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string? id = doc.RootElement.GetProperty("id").GetString();
        string? text = doc.RootElement.GetProperty("text").GetString()?.Trim();
        return id != null && !string.IsNullOrEmpty(text) ? (id, text) : null;
    }

    private static async Task<string?> TranslateAsync(string english)
    {
        string url = "https://api.mymemory.translated.net/get?langpair=en|pt-br&q=" + Uri.EscapeDataString(english);
        using HttpResponseMessage response = await ThoughtHttp.Client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("responseStatus", out JsonElement status)
            && status.ValueKind == JsonValueKind.Number && status.GetInt32() != 200)
        {
            return null;
        }
        if (doc.RootElement.TryGetProperty("quotaFinished", out JsonElement quota) && quota.ValueKind == JsonValueKind.True)
        {
            return null;
        }
        string? translated = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString();
        // Quota/error notices come back *as* the "translation"; so does an
        // untranslated echo when the service gives up.
        if (string.IsNullOrWhiteSpace(translated)
            || translated.Contains("MYMEMORY", StringComparison.OrdinalIgnoreCase)
            || string.Equals(translated.Trim(), english.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return System.Net.WebUtility.HtmlDecode(translated);
    }
}
