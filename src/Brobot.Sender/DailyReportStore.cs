using System.IO;
using System.Text.Json;

namespace Brobot.Sender;

/// <summary>
/// Everything DailyReportTracker needs to survive a Sender restart: today's
/// running totals for the 18h "Relatório do dia" (see DailyReportScoring).
/// Its own file rather than folded into AchievementProgress — same unrelated-
/// concerns split SenderSettings and AchievementProgress already keep, even
/// though both are "a flat JSON blob in %AppData%\Brobot".
/// </summary>
public sealed class DailyReportProgress
{
    // DailyReportTracker.RollOverDayIfNeeded zeroes these out the moment
    // Date no longer matches today, same reasoning as
    // AchievementProgress.Date — a session spanning midnight isn't prorated,
    // it just starts today's counters at zero.
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Now);
    public int BuildSuccessCount { get; set; }
    public int BuildFailCount { get; set; }
    public int CommitCount { get; set; }
    public double MeetingSeconds { get; set; }
    public double MediaSeconds { get; set; }
    // Subset of MediaSeconds: specifically time spent with a YouTube tab
    // both playing and focused (see YouTubeTabDetector.cs) — MediaSeconds
    // itself doesn't care about source or focus, this one cares about both.
    public double VideoFocusedSeconds { get; set; }
    // Independent of both fields above: time with a TikTok/Instagram/
    // Facebook tab focused (see SocialMediaTabDetector.cs) — a deliberately
    // separate bucket from VideoFocusedSeconds, not a merge of the two, per
    // product decision (YouTube stays its own thing).
    public double SocialFocusedSeconds { get; set; }
    public double GameSeconds { get; set; }
}

/// <summary>Loads/saves DailyReportProgress to %AppData%\Brobot\daily-report.json — same on-disk neighborhood and best-effort error handling as AchievementStore/SenderSettings.</summary>
public static class DailyReportStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Brobot", "daily-report.json");

    public static DailyReportProgress Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                DailyReportProgress? progress = JsonSerializer.Deserialize<DailyReportProgress>(json);
                if (progress != null)
                {
                    return progress;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt/unreadable file — start fresh rather than crash the
            // app over a lost day of report stats.
        }

        return new DailyReportProgress();
    }

    public static void Save(DailyReportProgress progress)
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(progress));
        }
        catch (Exception)
        {
            // Best-effort — a failed write just means the next successful
            // one catches up, same as every other cache/settings file here.
        }
    }
}
