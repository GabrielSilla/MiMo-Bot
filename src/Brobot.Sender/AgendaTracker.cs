using System.Windows.Threading;

namespace Brobot.Sender;

/// <summary>
/// Tracks meetings detected via NotificationClassifier's own Meeting
/// category (see MainWindow.OnPcNotificationReceived) and, independent of
/// however the original "Meet: &lt;título&gt; &lt;hora&gt;" NOTIFY got
/// sent, fires its own countdown ladder as the meeting actually approaches:
/// 15/10/5 minutes before, then "Deu a hora!" at the moment itself. Backed
/// by a plain DispatcherTimer polling every 20s (see PollInterval) rather
/// than one-shot timers scheduled per threshold -- four DispatcherTimers
/// per tracked item, each needing its own cancel-on-remove bookkeeping,
/// would be real state to get wrong for a precision this coarse-grained
/// (a 20s window is invisible against a "15 minutes" countdown).
///
/// Entirely in-memory, never persisted to disk -- the feature was asked
/// for exactly that way, and Outlook's own calendar stays the actual
/// source of truth; losing this list on a restart just means waiting for
/// the next reminder toast to repopulate it.
/// </summary>
public sealed class AgendaTracker : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    // Below this many minutes past the meeting's own start, an item is
    // dropped from the list on its own -- keeps the tab from accumulating
    // stale entries indefinitely once "Deu a hora!" has already fired.
    // Cancelling ahead of time is what the delete button is for instead.
    private const double AutoRemoveMinutesPast = -2;

    private readonly List<AgendaItem> _items = new();
    private DispatcherTimer? _timer;

    /// <summary>The current list, sorted soonest-first. Read this after Changed fires to redraw the Agenda tab.</summary>
    public IReadOnlyList<AgendaItem> Items => _items;

    /// <summary>The list itself changed (add, remove, or auto-expiry) — redraw the Agenda tab's rows.</summary>
    public event Action? Changed;

    /// <summary>One countdown threshold was just crossed for one item — send it as a NOTIFY.</summary>
    public event Action<AgendaItem, string>? ReminderDue;

    public void Start()
    {
        if (_timer != null)
        {
            return;
        }
        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => CheckReminders();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Adds a newly detected meeting, unless it's a near-duplicate of one
    /// already tracked -- Outlook's own reminder toast for the same meeting
    /// can fire more than once (confirmed while building
    /// TeamsNotificationWatcher's own detection, a different notification
    /// entirely but the same underlying "the OS re-shows a toast" behaviour),
    /// and without this each repeat would add a second row for the same
    /// meeting. Matched on title plus a time within a minute of an existing
    /// entry's own, since there's no real calendar event id available from
    /// a toast to key on instead.
    /// </summary>
    public void Add(string title, DateTime when)
    {
        bool alreadyTracked = _items.Any(i =>
            i.Title == title && Math.Abs((i.When - when).TotalMinutes) < 1);
        if (alreadyTracked)
        {
            return;
        }

        var item = new AgendaItem(title, when);
        _items.Add(item);
        _items.Sort((a, b) => a.When.CompareTo(b.When));
        Changed?.Invoke();

        // Outlook's own default reminder lead time is 15 minutes, so the
        // item being added is routinely *already* inside that window the
        // instant it's first seen -- checking right away, not waiting for
        // the next 20s tick, is what lets the 15-minute line fire
        // essentially immediately instead of a poll interval late.
        CheckReminders();
    }

    /// <summary>Manual removal — the delete button in the Agenda tab, for a meeting cancelled ahead of its own time.</summary>
    public void Remove(string id)
    {
        int removed = _items.RemoveAll(i => i.Id == id);
        if (removed > 0)
        {
            Changed?.Invoke();
        }
    }

    private void CheckReminders()
    {
        DateTime now = DateTime.Now;
        bool listChanged = false;

        for (int i = _items.Count - 1; i >= 0; i--)
        {
            AgendaItem item = _items[i];
            double minutesUntil = (item.When - now).TotalMinutes;

            // Checked most-urgent-first with a plain if/else-if chain, not
            // four independent ifs: minutesUntil <= 0 is already inside
            // every wider window above it, so without this an item that
            // was never open while the app was running (or one added right
            // at its own start time) would fire all four lines back to
            // back in the same tick instead of just the one that actually
            // applies now.
            if (!item.ReminderNowSent && minutesUntil <= 0)
            {
                item.ReminderNowSent = true;
                ReminderDue?.Invoke(item, $"Deu a hora! Entre na agenda: {item.Title}");
            }
            else if (!item.Reminder5Sent && minutesUntil <= 5)
            {
                item.Reminder5Sent = true;
                ReminderDue?.Invoke(item, $"Faltam 5 minutos para a agenda: {item.Title}");
            }
            else if (!item.Reminder10Sent && minutesUntil <= 10)
            {
                item.Reminder10Sent = true;
                ReminderDue?.Invoke(item, $"Faltam 10 minutos para a agenda: {item.Title}");
            }
            else if (!item.Reminder15Sent && minutesUntil <= 15)
            {
                item.Reminder15Sent = true;
                ReminderDue?.Invoke(item, $"Faltam 15 minutos para a agenda: {item.Title}");
            }

            if (minutesUntil <= AutoRemoveMinutesPast)
            {
                _items.RemoveAt(i);
                listChanged = true;
            }
        }

        if (listChanged)
        {
            Changed?.Invoke();
        }
    }
}
