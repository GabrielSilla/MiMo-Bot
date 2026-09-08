namespace Brobot.Sender;

/// <summary>
/// Tracks the 10 achievements in AchievementCatalog against signals this app
/// already observes elsewhere for their own reasons — GameMonitor,
/// WindowsMediaMonitor, AiThoughtsListener, BrobotConnection, Pausa, Tema.
/// Every public method here is meant to be called from the exact spot in
/// MainWindow that already reacts to that signal (sending a FACE/MSG,
/// updating a status label, ...); this class adds bookkeeping alongside
/// that, it doesn't own or replace any of it. Progress is persisted (see
/// AchievementStore) so it survives a Sender restart.
///
/// Time-based criteria (Coffee Machine, One More Game, Audiophile, AI
/// Overload) are all approximations of "how long were you doing X", not a
/// real activity/idle detector — nothing in this app watches mouse/keyboard
/// input, so "Coffee Machine" really measures "MiMo was connected", and
/// "One More Game"/"Audiophile" measure "GameMonitor/WindowsMediaMonitor
/// reported this as active", for as long as this app happened to be running
/// to see it. Honest enough for a Tamagotchi-style nudge, not a precise
/// timesheet.
/// </summary>
public sealed class AchievementMonitor
{
    // "Ligou o PC antes das 7h" is checked against the OS's actual boot time
    // (Environment.TickCount64), not when Sender happened to launch — so it
    // reflects the machine coming up, not this app.
    private static readonly TimeSpan EarlyBirdCutoff = TimeSpan.FromHours(7);

    // "Ainda trabalhando depois da meia-noite" is bounded to a pre-dawn
    // window rather than "any time after 00:00" — otherwise it would read as
    // "still connected literally any time tomorrow", not "still going".
    private static readonly TimeSpan NightOwlWindowEnd = TimeSpan.FromHours(5);

    private const double CoffeeMachineThresholdSeconds = 4 * 3600;
    private const double OneMoreGameThresholdSeconds = 3 * 3600;
    private const double AudiophileThresholdSeconds = 24 * 3600;
    private const double AiOverloadThresholdSeconds = 2 * 3600;
    private const int VictoryRoyaleThreshold = 3;
    private const int BreakTakerThreshold = 10;

    // A gap longer than this between two AI hook events counts as "stopped
    // using it", not "still going" — otherwise leaving a Claude Code
    // terminal open overnight would silently credit the whole idle gap as
    // active AI time.
    private static readonly TimeSpan AiActivityGapLimit = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan PeriodicSaveInterval = TimeSpan.FromMinutes(1);

    private static readonly string[] AllCoreThemes = ["DEFAULT", "MATRIX", "MI2MO2", "MI84"];

    private readonly AchievementProgress _progress;

    private bool _gameActive;
    private bool _musicActive;
    private DateTime? _lastAiEventAt;
    private DateTime _lastTick;
    private DateTime _lastPeriodicSave = DateTime.MinValue;

    /// <summary>Raised the moment an achievement's criterion is first met — from whatever thread called the triggering method (see each call site in MainWindow).</summary>
    public event Action<Achievement>? Unlocked;

    public AchievementMonitor()
    {
        _progress = AchievementStore.Load();
        _lastTick = DateTime.Now;
        RollOverDayIfNeeded();
    }

    public bool IsUnlocked(string id) => _progress.UnlockedAt.ContainsKey(id);

    public DateTime? GetUnlockedAt(string id) =>
        _progress.UnlockedAt.TryGetValue(id, out DateTime at) ? at : null;

    /// <summary>Call once whenever BrobotConnection transitions from disconnected to connected.</summary>
    public void OnConnected()
    {
        TryUnlock("FIRST_CONTACT");
        CheckEarlyBird();
    }

    /// <summary>Call from GameMonitor's own GameChanged handler (and when the Jogos checkbox turns off) with whether a game is currently detected.</summary>
    public void SetGameActive(bool active) => _gameActive = active;

    /// <summary>Call from WindowsMediaMonitor's NowPlayingChanged handler (and when the Mídia checkbox turns off) with whether the current session is audio-only (Spotify etc.), not video.</summary>
    public void SetMusicActive(bool active) => _musicActive = active;

    /// <summary>
    /// Call once per AiThoughtsListener event, regardless of which one — this
    /// is a heartbeat, not per-event-type logic. Consecutive events within
    /// AiActivityGapLimit of each other credit the gap between them as active
    /// AI time; a longer gap (or the very first event) credits nothing and
    /// just starts the clock.
    /// </summary>
    public void OnAiActivity()
    {
        RollOverDayIfNeeded();

        DateTime now = DateTime.Now;
        if (_lastAiEventAt is { } last && (now - last) <= AiActivityGapLimit)
        {
            _progress.TodayAiActiveSeconds += (now - last).TotalSeconds;
            TryUnlock("AI_OVERLOAD", _progress.TodayAiActiveSeconds >= AiOverloadThresholdSeconds);
        }

        _lastAiEventAt = now;
        MaybeSave(now);
    }

    /// <summary>Call when OnFrameReceived sees "RPG OVER VICTORY".</summary>
    public void OnRpgVictory()
    {
        _progress.RpgVictories++;
        TryUnlock("VICTORY_ROYALE", _progress.RpgVictories >= VictoryRoyaleThreshold);
        AchievementStore.Save(_progress);
    }

    /// <summary>Call from SendBreakReminder, once per NOTIFY COFFEE actually sent.</summary>
    public void OnBreakReminderSent()
    {
        _progress.BreakRemindersSent++;
        TryUnlock("BREAK_TAKER", _progress.BreakRemindersSent >= BreakTakerThreshold);
        AchievementStore.Save(_progress);
    }

    /// <summary>Call from TemaComboBox_SelectionChanged with the CoreTheme value just sent (DEFAULT/MATRIX/MI2MO2/MI84).</summary>
    public void OnThemeSelected(string coreTheme)
    {
        _progress.ThemesUsed.Add(coreTheme);
        TryUnlock("IDENTITY_CRISIS", AllCoreThemes.All(_progress.ThemesUsed.Contains));
        AchievementStore.Save(_progress);
    }

    /// <summary>
    /// Drives every time-based accumulator — called from
    /// UpdateConnectionStatus's own 200ms poll, which already knows
    /// `connected` at every tick, so nothing new has to run a timer of its
    /// own just for this. Elapsed wall-clock time since the last call is
    /// credited to whichever of connected/game-active/music-active is
    /// currently true.
    /// </summary>
    public void Tick(bool connected)
    {
        RollOverDayIfNeeded();

        DateTime now = DateTime.Now;
        double elapsed = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        CheckEarlyBird();

        if (elapsed <= 0 || elapsed > 300)
        {
            // First call ever, or a gap too long to trust (system sleep, a
            // clock change) — crediting that whole gap to whatever happened
            // to be active before it would overstate real elapsed time.
            return;
        }

        if (connected)
        {
            _progress.TodayConnectedSeconds += elapsed;
            TryUnlock("COFFEE_MACHINE", _progress.TodayConnectedSeconds >= CoffeeMachineThresholdSeconds);
            CheckNightOwl(now);
        }

        if (_gameActive)
        {
            _progress.TodayGameSeconds += elapsed;
            TryUnlock("ONE_MORE_GAME", _progress.TodayGameSeconds >= OneMoreGameThresholdSeconds);
        }

        if (_musicActive)
        {
            _progress.LifetimeMusicSeconds += elapsed;
            TryUnlock("AUDIOPHILE", _progress.LifetimeMusicSeconds >= AudiophileThresholdSeconds);
        }

        MaybeSave(now);
    }

    private void CheckNightOwl(DateTime now)
    {
        if (now.TimeOfDay < NightOwlWindowEnd)
        {
            TryUnlock("NIGHT_OWL");
        }
    }

    private void CheckEarlyBird()
    {
        // DateTime arithmetic (not a raw TimeSpan-of-milliseconds subtraction)
        // is what handles a machine that's been up for days without wrapping
        // negative — .TimeOfDay on the result is the wall-clock moment the
        // OS actually booted, however long ago that was.
        DateTime bootAt = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (bootAt.TimeOfDay < EarlyBirdCutoff)
        {
            TryUnlock("EARLY_BIRD");
        }
    }

    private void RollOverDayIfNeeded()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        if (_progress.Date == today)
        {
            return;
        }

        _progress.Date = today;
        _progress.TodayConnectedSeconds = 0;
        _progress.TodayGameSeconds = 0;
        _progress.TodayAiActiveSeconds = 0;
    }

    private void TryUnlock(string id, bool condition = true)
    {
        if (!condition || _progress.UnlockedAt.ContainsKey(id))
        {
            return;
        }

        // Saved immediately rather than waiting for the next periodic save —
        // an unlock is a discrete fact worth persisting right away, same
        // reasoning MimoDiscovery's PersistDiscoveredAddress already follows.
        _progress.UnlockedAt[id] = DateTime.Now;
        AchievementStore.Save(_progress);

        Achievement achievement = AchievementCatalog.All.First(a => a.Id == id);
        Unlocked?.Invoke(achievement);
    }

    /// <summary>
    /// Tick()/OnAiActivity() run far too often (every 200ms, or on every AI
    /// hook event) to write the progress file on every call — this throttles
    /// those two to once a minute. Anything that unlocks still saves
    /// immediately via TryUnlock regardless of this throttle.
    /// </summary>
    private void MaybeSave(DateTime now)
    {
        if (now - _lastPeriodicSave < PeriodicSaveInterval)
        {
            return;
        }

        _lastPeriodicSave = now;
        AchievementStore.Save(_progress);
    }
}
