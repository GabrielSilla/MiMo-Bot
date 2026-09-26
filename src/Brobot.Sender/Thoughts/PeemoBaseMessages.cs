using System.IO;
using System.Reflection;

namespace Brobot.Sender.Thoughts;

/// <summary>
/// Peemo's own standalone phrases (Data/peemo-base.tsv, embedded) — the
/// weighted source that's also the final fallback, since it always has
/// something. Shuffled once per install into a persisted queue
/// (ThoughtHistory.Queues) and walked in order, so nothing repeats until
/// the whole pool has been used; never the same category twice in a row.
/// Joking phrases carry a sarcasm level and are only eligible in their
/// mood (see specs/mood.md); neutral ones ("-") are eligible in any.
/// </summary>
internal sealed class PeemoBaseMessages : IThoughtSource
{
    private const string QueueKey = "base";
    private const string ResourceName = "Brobot.Sender.Thoughts.Data.peemo-base.tsv";

    private sealed record Entry(string Id, string Category, string Face, string Level, string Text);

    private readonly ThoughtHistoryStore _history;
    private readonly Dictionary<string, Entry> _entries;
    private readonly Random _rng = new();

    public string Name => "base";
    public double Weight => 30;

    public PeemoBaseMessages(ThoughtHistoryStore history)
    {
        _history = history;
        _entries = LoadEntries().ToDictionary(e => e.Id);
    }

    public Thought? TryPick(ThoughtContext context)
    {
        List<string> queue = CurrentQueue();
        string mood = LevelFor(context.Mood);

        // First pass keeps "never the same category twice in a row"; the
        // second relaxes it rather than staying silent over it.
        Entry? pick = FirstEligible(queue, mood, avoidCategory: _history.Data.LastCategory)
            ?? FirstEligible(queue, mood, avoidCategory: null);
        if (pick == null)
        {
            // Nothing left fits this mood: start a fresh shuffle and try once more.
            queue = Reshuffle();
            pick = FirstEligible(queue, mood, avoidCategory: null);
            if (pick == null)
            {
                return null;
            }
        }

        queue.Remove(pick.Id);
        _history.Data.LastCategory = pick.Category;
        return new Thought(pick.Face, pick.Text, Name, pick.Category, pick.Id);
    }

    private Entry? FirstEligible(List<string> queue, string moodLevel, string? avoidCategory)
    {
        foreach (string id in queue)
        {
            if (!_entries.TryGetValue(id, out Entry? entry))
            {
                continue; // id from an older data file — skipped, cleaned up on the next reshuffle
            }
            if (entry.Level != "-" && entry.Level != moodLevel)
            {
                continue;
            }
            if (avoidCategory != null && entry.Category == avoidCategory)
            {
                continue;
            }
            return entry;
        }
        return null;
    }

    private List<string> CurrentQueue()
    {
        if (_history.Data.Queues.TryGetValue(QueueKey, out List<string>? queue))
        {
            // Phrases added to the data file after the shuffle join at a
            // random spot instead of waiting for the next full cycle.
            foreach (string id in _entries.Keys.Where(id => !queue.Contains(id) && !_history.Data.UsedAt.ContainsKey(id)))
            {
                queue.Insert(_rng.Next(queue.Count + 1), id);
            }
            if (queue.Count > 0)
            {
                return queue;
            }
        }
        return Reshuffle();
    }

    private List<string> Reshuffle()
    {
        List<string> queue = _entries.Keys.OrderBy(_ => _rng.Next()).ToList();
        _history.Data.Queues[QueueKey] = queue;
        return queue;
    }

    private static string LevelFor(Mood mood) => mood switch
    {
        Mood.FimDeDia => "medio",
        Mood.Cansado => "acido",
        _ => "leve",
    };

    private static IEnumerable<Entry> LoadEntries()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream == null)
        {
            yield break;
        }

        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            string[] cols = line.Split('\t');
            if (cols.Length == 5)
            {
                yield return new Entry(cols[0], cols[1], cols[2], cols[3], cols[4]);
            }
        }
    }
}
