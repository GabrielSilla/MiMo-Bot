namespace Brobot.Sender;

/// <summary>
/// Accumulates today's raw numbers for the 18h "Relatório do dia" — build
/// success/fail counts and meeting/media/game elapsed time — the same
/// "flag(s) set by whichever monitor already reacts to that signal, summed
/// by a periodic Tick" shape AchievementMonitor already uses for its own
/// time-based criteria. A separate class from AchievementMonitor rather than
/// new fields bolted onto it: achievements and the daily report are
/// unrelated questions that only happen to share an accumulation pattern,
/// same reasoning DailyReportStore's own header comment gives for not
/// folding into AchievementProgress.
///
/// Every flag here is best-effort telemetry, not a precise timesheet — see
/// AchievementMonitor's own header comment for why (nothing watches
/// mouse/keyboard input; this only knows what GameMonitor/WindowsMediaMonitor/
/// LiveCallMonitor happened to report while this app was running to see it).
/// </summary>
public sealed class DailyReportTracker
{
    private static readonly TimeSpan PeriodicSaveInterval = TimeSpan.FromMinutes(1);

    private readonly DailyReportProgress _progress;

    private bool _gameActive;
    private bool _mediaActive;
    private bool _meetingActive;
    private DateTime _lastTick;
    private DateTime _lastPeriodicSave = DateTime.MinValue;

    public DailyReportTracker()
    {
        _progress = DailyReportStore.Load();
        _lastTick = DateTime.Now;
        RollOverDayIfNeeded();
    }

    /// <summary>Call from OnBuildStateChanged's BuildState.Successful case only — not Started, to avoid double-counting one build.</summary>
    public void RecordBuildSuccess()
    {
        RollOverDayIfNeeded();
        _progress.BuildSuccessCount++;
        DailyReportStore.Save(_progress);
    }

    /// <summary>Call from OnBuildStateChanged's BuildState.Failed case only.</summary>
    public void RecordBuildFailure()
    {
        RollOverDayIfNeeded();
        _progress.BuildFailCount++;
        DailyReportStore.Save(_progress);
    }

    /// <summary>Call from GameMonitor's own GameChanged handler (and when the Jogos checkbox turns off) with whether a game is currently detected.</summary>
    public void SetGameActive(bool active) => _gameActive = active;

    /// <summary>
    /// Call from WindowsMediaMonitor's NowPlayingChanged handler (and when
    /// the Mídia checkbox turns off) with whether *anything* is playing —
    /// music or video both count here, unlike AchievementMonitor's own
    /// SetMusicActive, which is audio-only for a different, unrelated
    /// achievement criterion.
    /// </summary>
    public void SetMediaActive(bool active) => _mediaActive = active;

    /// <summary>Call from OnLiveCallChanged with whether _activeCalls is non-empty — a live call in any watched app counts as "in a meeting".</summary>
    public void SetMeetingActive(bool active) => _meetingActive = active;

    /// <summary>
    /// Drives every time-based accumulator here — called from the same
    /// 200ms UpdateConnectionStatus tick that already drives
    /// AchievementMonitor.Tick, but unlike that one this isn't gated on Core
    /// being connected: meeting/media/game detection all happen at the OS
    /// level, independent of whether MiMo is currently reachable.
    /// </summary>
    public void Tick()
    {
        RollOverDayIfNeeded();

        DateTime now = DateTime.Now;
        double elapsed = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        if (elapsed <= 0 || elapsed > 300)
        {
            // First call ever, or a gap too long to trust (system sleep, a
            // clock change) — crediting that whole gap to whatever happened
            // to be active before it would overstate real elapsed time.
            return;
        }

        if (_meetingActive)
        {
            _progress.MeetingSeconds += elapsed;
        }

        if (_mediaActive)
        {
            _progress.MediaSeconds += elapsed;
        }

        if (_gameActive)
        {
            _progress.GameSeconds += elapsed;
        }

        MaybeSave(now);
    }

    /// <summary>Pure read for SendDailyReport — today's counts run through DailyReportScoring, with no side effects of its own.</summary>
    public DailyReportResult BuildReport()
    {
        RollOverDayIfNeeded();
        return DailyReportScoring.Evaluate(
            _progress.BuildSuccessCount, _progress.BuildFailCount,
            _progress.MeetingSeconds, _progress.MediaSeconds, _progress.GameSeconds);
    }

    private void RollOverDayIfNeeded()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        if (_progress.Date == today)
        {
            return;
        }

        _progress.Date = today;
        _progress.BuildSuccessCount = 0;
        _progress.BuildFailCount = 0;
        _progress.MeetingSeconds = 0;
        _progress.MediaSeconds = 0;
        _progress.GameSeconds = 0;
    }

    /// <summary>Tick() runs every 200ms — this throttles the write to once a minute, same as AchievementMonitor.MaybeSave. RecordBuildSuccess/Failure save immediately regardless, same as AchievementMonitor's discrete-event methods.</summary>
    private void MaybeSave(DateTime now)
    {
        if (now - _lastPeriodicSave < PeriodicSaveInterval)
        {
            return;
        }

        _lastPeriodicSave = now;
        DailyReportStore.Save(_progress);
    }
}
