namespace Brobot.Sender.Thoughts;

/// <summary>
/// *Which* source speaks when a thought is due (see specs/sender-thoughts.md,
/// "Sources and weights"). Priority sources (satellite passes) are asked
/// first and only answer when something real is happening. Otherwise the
/// weighted sources are tried in a weight-drawn order that puts whichever
/// spoke last at the very end — so it's never the same source twice in a
/// row unless nothing else has anything — and a source returning null
/// just passes the turn to the next one.
/// </summary>
internal sealed class ThoughtDirector
{
    private readonly IReadOnlyList<IThoughtSource> _sources;
    private readonly ThoughtHistoryStore _history;
    private readonly Random _rng;

    public ThoughtDirector(IEnumerable<IThoughtSource> sources, ThoughtHistoryStore history, Random? rng = null)
    {
        _sources = sources.ToList();
        _history = history;
        _rng = rng ?? new Random();
    }

    public Thought? Pick(ThoughtContext context)
    {
        _history.RollOverDayIfNeeded(context.Now);

        foreach (IThoughtSource source in _sources.Where(s => s.IsPriority))
        {
            if (source.TryPick(context) is { } thought)
            {
                return thought;
            }
        }

        foreach (IThoughtSource source in WeightedOrder())
        {
            if (source.TryPick(context) is { } thought)
            {
                return thought;
            }
        }

        return null;
    }

    /// <summary>Whether a spoken thought's source should become LastSource (priority ones don't).</summary>
    public bool CountsAsLastSource(Thought thought) =>
        _sources.FirstOrDefault(s => s.Name == thought.Source) is { IsPriority: false };

    private IEnumerable<IThoughtSource> WeightedOrder()
    {
        var pool = _sources.Where(s => !s.IsPriority && s.Weight > 0).ToList();
        IThoughtSource? last = pool.FirstOrDefault(s => s.Name == _history.Data.LastSource);
        if (last != null)
        {
            pool.Remove(last);
        }

        // Weighted draw without replacement: each step picks one of the
        // remaining sources in proportion to its weight.
        while (pool.Count > 0)
        {
            double roll = _rng.NextDouble() * pool.Sum(s => s.Weight);
            IThoughtSource chosen = pool[^1];
            foreach (IThoughtSource candidate in pool)
            {
                roll -= candidate.Weight;
                if (roll <= 0)
                {
                    chosen = candidate;
                    break;
                }
            }
            pool.Remove(chosen);
            yield return chosen;
        }

        if (last != null)
        {
            yield return last;
        }
    }
}
