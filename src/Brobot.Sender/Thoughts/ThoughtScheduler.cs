namespace Brobot.Sender.Thoughts;

/// <summary>
/// *When* Peemo thinks (see specs/sender-thoughts.md, "Timing"). The gap to
/// the next thought is 30 min + an exponential draw with a 50 min mean, so
/// it's unpredictable without ever being a quick follow-up. The 30-min floor
/// is added, not clamped: clamping would pile every short draw onto exactly
/// 30 min and make it predictable again. Restarting Sender draws a fresh
/// schedule — nothing here is persisted.
///
/// A slot that comes due while Peemo can't speak (see MainWindow's hold
/// conditions) isn't skipped: IsDue stays true until the caller actually
/// speaks and calls ScheduleNext, so the thought just waits for the clear.
/// </summary>
internal sealed class ThoughtScheduler
{
    private static readonly TimeSpan Floor = TimeSpan.FromMinutes(30);
    private const double ExponentialMeanMinutes = 50;
    private static readonly TimeSpan MaxGap = TimeSpan.FromHours(4);

    private readonly Random _rng;

    public DateTime NextAt { get; private set; }

    public ThoughtScheduler(DateTime now, Random? rng = null)
    {
        _rng = rng ?? new Random();
        ScheduleNext(now);
    }

    public bool IsDue(DateTime now) => now >= NextAt;

    public void ScheduleNext(DateTime now) => NextAt = now + NextGap();

    /// <summary>Modo teste: make the next check fire right away (holds still apply).</summary>
    public void ForceNow(DateTime now) => NextAt = now;

    private TimeSpan NextGap()
    {
        // Inverse-CDF sample of an exponential; 1 - NextDouble() is in
        // (0, 1], so the log never sees zero.
        double extraMinutes = -ExponentialMeanMinutes * Math.Log(1.0 - _rng.NextDouble());
        TimeSpan gap = Floor + TimeSpan.FromMinutes(extraMinutes);
        return gap > MaxGap ? MaxGap : gap;
    }
}
