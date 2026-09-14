namespace Brobot.Sender;

/// <summary>
/// One tracked meeting, kept in memory only by AgendaTracker -- never
/// written to SenderSettings/disk (the feature was asked for as "guarda em
/// memória", and the source of truth stays Outlook's own calendar; losing
/// this on a restart just means waiting for Outlook's next reminder toast
/// to repopulate it, not losing anything real).
///
/// The four ReminderXSent flags are what keep AgendaTracker's periodic
/// check from repeating the same countdown line every tick once a
/// threshold has been crossed -- each one is meant to fire exactly once
/// per item, in descending order of urgency (see AgendaTracker.CheckReminders).
/// </summary>
public sealed class AgendaItem
{
    public string Id { get; }
    public string Title { get; }
    public DateTime When { get; }

    public bool Reminder15Sent { get; set; }
    public bool Reminder10Sent { get; set; }
    public bool Reminder5Sent { get; set; }
    public bool ReminderNowSent { get; set; }

    public AgendaItem(string title, DateTime when)
    {
        Title = title;
        When = when;
        // Title+When is a stable-enough identity for this session: two
        // genuinely different meetings sharing both a title and a start
        // time is not a case worth guarding against, and there's no real
        // event id available from a toast notification to key on instead.
        Id = $"{title}|{when:O}";
    }
}
