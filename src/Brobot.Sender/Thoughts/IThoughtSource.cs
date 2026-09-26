namespace Brobot.Sender.Thoughts;

/// <summary>
/// Where a thought can come from. Each source owns its own rules and data
/// (which phrases, when they apply, what's already been used); the
/// ThoughtDirector only picks which source gets asked. A new source is one
/// class implementing this, registered in MainWindow's director setup.
/// </summary>
internal interface IThoughtSource
{
    /// <summary>Stable id, persisted in ThoughtHistoryStore (LastSource).</summary>
    string Name { get; }

    /// <summary>Relative share of the weighted draw. Ignored for priority sources.</summary>
    double Weight { get; }

    /// <summary>
    /// Asked before the weighted draw at every slot and only answers when
    /// something real happened (a satellite pass) — an event, not a pool.
    /// Doesn't count for the "never the same source twice in a row" rule.
    /// </summary>
    bool IsPriority => false;

    /// <summary>
    /// A thought for right now, or null for "nothing fresh right now" —
    /// the director then passes the turn to the next source. Must never
    /// block on the network: anything remote is prefetched into a stock.
    /// </summary>
    Thought? TryPick(ThoughtContext context);
}
