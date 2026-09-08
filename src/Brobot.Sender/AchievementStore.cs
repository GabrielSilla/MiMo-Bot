using System.IO;
using System.Text.Json;

namespace Brobot.Sender;

/// <summary>
/// Everything AchievementMonitor needs to survive a Sender restart: which
/// achievements are already unlocked (and when), plus the running counters
/// each still-locked one's criterion depends on. One flat class rather than
/// a generic "counter bag" — there are only 10 achievements and each one's
/// criterion is different enough (a duration, a count, a set of distinct
/// values) that naming the field is clearer than a dictionary of untyped
/// counters would be.
/// </summary>
public sealed class AchievementProgress
{
    public Dictionary<string, DateTime> UnlockedAt { get; set; } = new();

    // Daily accumulators — AchievementMonitor.RollOverDayIfNeeded zeroes
    // these out the moment Date no longer matches today, rather than trying
    // to prorate a session that spans midnight.
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Now);
    public double TodayConnectedSeconds { get; set; }
    public double TodayGameSeconds { get; set; }
    public double TodayAiActiveSeconds { get; set; }

    // Lifetime counters — never reset.
    public double LifetimeMusicSeconds { get; set; }
    public int RpgVictories { get; set; }
    public int BreakRemindersSent { get; set; }
    public HashSet<string> ThemesUsed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Loads/saves AchievementProgress to %AppData%\Brobot\achievements.json — same on-disk neighborhood and best-effort error handling as SenderSettings/GameMonitor's own cache file.</summary>
public static class AchievementStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Brobot", "achievements.json");

    public static AchievementProgress Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                AchievementProgress? progress = JsonSerializer.Deserialize<AchievementProgress>(json);
                if (progress != null)
                {
                    return progress;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt/unreadable file — start fresh rather than crash the
            // app over lost achievement progress. Worst case, something
            // that was already unlocked has to be earned again.
        }

        return new AchievementProgress();
    }

    public static void Save(AchievementProgress progress)
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
