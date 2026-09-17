namespace Brobot.Sender;

/// <summary>
/// How the 18h "Relatório do dia" turns today's raw numbers into a verdict.
/// Pure and stateless on purpose — DailyReportTracker owns the counters,
/// this just maps a snapshot of them to a score/rating, so the weights below
/// can be retuned without touching anything that persists state.
///
/// Rules, straight from the product decision behind this feature: successful
/// AND failed builds both count positive (either one means work happened —
/// success just means more of it), a git commit counts positive the same
/// way (see hooks/mimo-git-hook.ps1's GitCommit event), meeting time counts
/// positive (it's work too), game time is free for the first half hour of
/// the day and then
/// penalizes *increasingly* per extra half hour (not a flat per-minute
/// rate — a two-hour session should hurt a lot more per-minute than a
/// forty-minute one), and media (music/video) time is deliberately left out
/// of the score entirely — it's logged in the report, never judged.
/// </summary>
public enum DailyPerformanceRating
{
    Pessimo,
    Ruim,
    Questionavel,
    Medio,
    Bom,
    Excelente,
}

public readonly record struct DailyReportResult(
    int BuildSuccessCount,
    int BuildFailCount,
    int CommitCount,
    double MeetingMinutes,
    double MediaMinutes,
    double GameMinutes,
    double Score,
    DailyPerformanceRating Rating);

public static class DailyReportScoring
{
    // A day with no signal at all lands exactly here — "nothing happened"
    // reads as Médio, not as a judgment in either direction.
    private const double Baseline = 50;

    private const double BuildSuccessPoints = 8;
    private const double BuildFailPoints = 3;
    // Between the two build weights: a commit is real, concrete progress —
    // more than a failed build's "at least you tried", but a commit alone
    // isn't the same signal as a build actually passing.
    private const double CommitPoints = 5;
    private const double MeetingPointsPer30Min = 4;

    // First half hour of gaming is free; every half hour after that costs
    // more than the last (block n costs n * GamePenaltyPerBlock, summed) —
    // a triangular ramp rather than a flat rate, so a two-hour session hurts
    // disproportionately more than a forty-minute one, not just linearly more.
    private const double GameFreeMinutes = 30;
    private const double GamePenaltyPerBlock = 5;

    private const double PessimoCeiling = 15;
    private const double RuimCeiling = 35;
    private const double QuestionavelCeiling = 50;
    private const double MedioCeiling = 70;
    private const double BomCeiling = 90;

    public static DailyReportResult Evaluate(
        int buildSuccessCount, int buildFailCount, int commitCount,
        double meetingSeconds, double mediaSeconds, double gameSeconds)
    {
        double meetingMinutes = meetingSeconds / 60.0;
        double mediaMinutes = mediaSeconds / 60.0;
        double gameMinutes = gameSeconds / 60.0;

        double score = Baseline
            + BuildSuccessPoints * buildSuccessCount
            + BuildFailPoints * buildFailCount
            + CommitPoints * commitCount
            + MeetingPointsPer30Min * (meetingMinutes / 30.0)
            - GamePenalty(gameMinutes);

        DailyPerformanceRating rating =
            score < PessimoCeiling ? DailyPerformanceRating.Pessimo :
            score < RuimCeiling ? DailyPerformanceRating.Ruim :
            score < QuestionavelCeiling ? DailyPerformanceRating.Questionavel :
            score < MedioCeiling ? DailyPerformanceRating.Medio :
            score < BomCeiling ? DailyPerformanceRating.Bom :
            DailyPerformanceRating.Excelente;

        return new DailyReportResult(
            buildSuccessCount, buildFailCount, commitCount,
            meetingMinutes, mediaMinutes, gameMinutes,
            score, rating);
    }

    /// <summary>
    /// 0 for up to GameFreeMinutes. Past that, minutes are bucketed into
    /// 30-min blocks (a partial block still counts as a full one, so minute
    /// 31 already incurs the first block's penalty rather than waiting for
    /// minute 60) and summed as a triangular number: block 1 costs
    /// GamePenaltyPerBlock, block 2 costs 2x that on top, block 3 costs 3x
    /// on top of that, and so on.
    /// </summary>
    private static double GamePenalty(double gameMinutes)
    {
        double extraMinutes = Math.Max(0, gameMinutes - GameFreeMinutes);
        if (extraMinutes <= 0)
        {
            return 0;
        }

        double extraBlocks = Math.Ceiling(extraMinutes / 30.0);
        return GamePenaltyPerBlock * extraBlocks * (extraBlocks + 1) / 2.0;
    }
}
