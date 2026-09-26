using System.IO;
using System.Text.Json;

namespace Brobot.Sender.Thoughts;

/// <summary>
/// Everything Pensamentos needs to remember across Sender restarts, in
/// %AppData%\Brobot\thought-history.json — same on-disk neighborhood and
/// best-effort error handling as DailyReportStore/AchievementStore. This is
/// *Sender's* memory for not repeating itself, not Peemo's: the phrases
/// themselves still never imply Peemo remembers other days.
/// </summary>
internal sealed class ThoughtHistory
{
    /// <summary>FiredToday/CountToday reset when this stops matching today.</summary>
    public DateOnly Day { get; set; } = DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Trigger ids that already fired today — each trigger level speaks once a day.</summary>
    public HashSet<string> FiredToday { get; set; } = new();

    /// <summary>Thoughts per source today, for per-day limits (one space thought a day, ~1 "neste dia"...).</summary>
    public Dictionary<string, int> CountToday { get; set; } = new();

    /// <summary>Per-install shuffled queues of phrase ids (key = source or source:group); a pool never repeats until its queue runs out.</summary>
    public Dictionary<string, List<string>> Queues { get; set; } = new();

    /// <summary>When a phrase id was last used, for cooldowns ("the same trigger phrase won't repeat within a month").</summary>
    public Dictionary<string, DateTime> UsedAt { get; set; } = new();

    /// <summary>The weighted source that spoke last — the director never picks it twice in a row.</summary>
    public string? LastSource { get; set; }

    /// <summary>Last base-phrase category, so PeemoBaseMessages never repeats a category back to back.</summary>
    public string? LastCategory { get; set; }

    /// <summary>Last situation group WorkContextMessages spoke from — tried last next time, same idea as LastCategory.</summary>
    public string? LastContextGroup { get; set; }
}

internal sealed class ThoughtHistoryStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Brobot", "thought-history.json");

    public ThoughtHistory Data { get; }

    public ThoughtHistoryStore()
    {
        Data = Load();
        RollOverDayIfNeeded(DateTime.Now);
    }

    public void RollOverDayIfNeeded(DateTime now)
    {
        DateOnly today = DateOnly.FromDateTime(now);
        if (Data.Day == today)
        {
            return;
        }

        Data.Day = today;
        Data.FiredToday.Clear();
        Data.CountToday.Clear();
    }

    public bool WasFiredToday(string triggerId) => Data.FiredToday.Contains(triggerId);

    public void MarkFired(string triggerId) => Data.FiredToday.Add(triggerId);

    public int CountToday(string source) => Data.CountToday.TryGetValue(source, out int n) ? n : 0;

    public bool UsedWithin(string phraseId, TimeSpan window, DateTime now) =>
        Data.UsedAt.TryGetValue(phraseId, out DateTime at) && now - at < window;

    /// <summary>Records a thought that actually went out, then saves.</summary>
    public void RecordSpoken(Thought thought, string? phraseId, bool countsAsLastSource, DateTime now)
    {
        RollOverDayIfNeeded(now);
        Data.CountToday[thought.Source] = CountToday(thought.Source) + 1;
        if (phraseId != null)
        {
            Data.UsedAt[phraseId] = now;
        }
        if (countsAsLastSource)
        {
            Data.LastSource = thought.Source;
        }
        Save();
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(Data));
        }
        catch (Exception)
        {
            // Best-effort — worst case a phrase repeats a little sooner.
        }
    }

    private static ThoughtHistory Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                ThoughtHistory? history = JsonSerializer.Deserialize<ThoughtHistory>(File.ReadAllText(FilePath));
                if (history != null)
                {
                    return history;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt/unreadable — start fresh; the only cost is repeats.
        }

        return new ThoughtHistory();
    }
}
