using System.Text.RegularExpressions;

namespace Brobot.Sender;

public enum NotificationCategory
{
    Generic,
    Email,
    Meeting,
}

/// <summary>
/// Sorts a PcNotification into a broad category so MainWindow can phrase it
/// differently on Peemo's screen ("Você recebeu um email: &lt;título&gt;"
/// instead of the generic "&lt;App&gt;: &lt;texto&gt;") -- starting with
/// just Email, the one category actually asked for so far; more categories
/// (chat, calendar, ...) are a matter of adding another marker list and
/// enum value here, not touching NotificationMonitor/TeamsNotificationWatcher.
///
/// Matches on AppName only for now -- Outlook's is unambiguous ("Outlook"),
/// and an installed Gmail PWA would show up the same simple way ("Gmail").
/// A Gmail notification arriving from a plain browser tab is a harder case
/// deliberately left unhandled: the browser's own DisplayName ("Google
/// Chrome"/"Microsoft Edge") says nothing about which site sent it, only
/// PcNotification.AppUserModelId might (browsers register a distinct
/// per-site id, confirmed for a different site in this machine's own toast
/// registry, but Gmail's exact value hasn't been confirmed against a real
/// notification yet) -- add a rule here once it has been.
///
/// Meeting is Email's one exception, not a separate app: Outlook's own
/// calendar reminder toast (confirmed against a real capture -- "Testes SSO
/// Sanepar" / "Reunião do Microsoft Teams" / "Qui, 10:15", with a
/// "X minutos antes do início" snooze control) comes from the exact same
/// AppUserModelId a new-mail toast does, so AppName alone can't tell them
/// apart. What does: a calendar reminder's body always carries a day/time
/// line ("Qui, 10:15") a plain new-mail toast's sender-name/subject pair
/// never does, so TimePattern matching against the *whole* joined Text
/// (not just Title) is what actually distinguishes them. This is the same
/// class of fragility already accepted for TeamsNotificationWatcher's own
/// PT-BR string matching: an ordinary email whose body happens to quote a
/// time (a plain "3:00pm" mentioned in the preview snippet) would
/// misclassify as a meeting. Checked before the plain Email rule since it's
/// the more specific case.
/// </summary>
public static class NotificationClassifier
{
    private static readonly string[] EmailAppNameMarkers = { "outlook", "gmail" };
    private const string MeetingAppNameMarker = "outlook";

    // "10:15", "9:05" -- a bare HH:MM, which is exactly (and, empirically,
    // only) what a calendar reminder's own day/time line renders as.
    private static readonly Regex TimePattern = new(@"\b(\d{1,2}):(\d{2})\b", RegexOptions.Compiled);

    public static NotificationCategory Classify(PcNotification notification)
    {
        if (notification.AppName.Contains(MeetingAppNameMarker, StringComparison.OrdinalIgnoreCase)
            && TimePattern.IsMatch(notification.Text))
        {
            return NotificationCategory.Meeting;
        }

        foreach (string marker in EmailAppNameMarkers)
        {
            if (notification.AppName.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return NotificationCategory.Email;
            }
        }
        return NotificationCategory.Generic;
    }

    /// <summary>
    /// "10:15" -&gt; "10h15", the PT-BR time separator Peemo's own font/wire
    /// already writes everywhere else (see PROTOCOL.md's TIME command).
    /// Null only if Classify already returned something other than Meeting
    /// for this same notification -- TimePattern is what Classify itself
    /// keyed on, so a Meeting-classified notification is always guaranteed
    /// a match here.
    /// </summary>
    public static string? ExtractMeetingTime(PcNotification notification)
    {
        Match match = TimePattern.Match(notification.Text);
        return match.Success ? $"{match.Groups[1].Value}h{match.Groups[2].Value}" : null;
    }

    /// <summary>
    /// The same HH:MM TimePattern already keyed Classify/ExtractMeetingTime
    /// on, turned into an actual DateTime for AgendaTracker to count down
    /// against -- today's date at that clock time, or tomorrow's if that
    /// clock time is already more than a couple minutes in the past. A
    /// toast has no date field of its own to read (just the bare "Qui,
    /// 10:15" line), and Outlook only ever fires this reminder *before*
    /// the meeting starts, so "today" is right for the near-total majority
    /// of cases; the past-clock-time fallback exists only for the edge
    /// case of a reminder still showing in Action Center just after
    /// midnight for a meeting that was technically "yesterday" evening.
    /// </summary>
    public static DateTime? ExtractMeetingDateTime(PcNotification notification)
    {
        Match match = TimePattern.Match(notification.Text);
        if (!match.Success)
        {
            return null;
        }

        int hour = int.Parse(match.Groups[1].Value);
        int minute = int.Parse(match.Groups[2].Value);
        if (hour > 23 || minute > 59)
        {
            return null;
        }

        DateTime when = DateTime.Today.AddHours(hour).AddMinutes(minute);
        if (when < DateTime.Now.AddMinutes(-2))
        {
            when = when.AddDays(1);
        }
        return when;
    }
}
