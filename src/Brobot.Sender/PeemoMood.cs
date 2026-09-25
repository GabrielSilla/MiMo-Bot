namespace Brobot.Sender;

/// <summary>
/// Peemo's mood through the day (see specs/mood.md), which sets how
/// sarcastic the phrase Sender picks is: leve while Animado, médio at Fim
/// de Dia, ácido when Cansado. Core derives the same mood from the TIME it
/// receives and shows it as the battery badge (Personality.cpp's
/// moodForTime) — these hours must match that file's MOOD_*_START_HOUR
/// constants. They can't drift apart in practice: TIME comes from this
/// same machine's clock.
/// </summary>
internal enum Mood { Animado, FimDeDia, Cansado }

internal static class PeemoMood
{
    private const int AnimadoStartHour = 7;
    private const int FimDeDiaStartHour = 16;
    private const int CansadoStartHour = 22;

    public static Mood At(DateTime time)
    {
        int hour = time.Hour;
        if (hour >= CansadoStartHour || hour < AnimadoStartHour) return Mood.Cansado;
        if (hour >= FimDeDiaStartHour) return Mood.FimDeDia;
        return Mood.Animado;
    }

    public static Mood Current => At(DateTime.Now);
}

/// <summary>
/// One phrase pool per sarcasm level — leve (Animado), médio (Fim de Dia),
/// ácido (Cansado) — for any list that should follow Peemo's mood (see
/// specs/voice-guide.md for what each level may and may not say).
/// </summary>
internal sealed class MoodPhrases
{
    private static readonly Random Rng = new();

    public required string[] Leve { get; init; }
    public required string[] Medio { get; init; }
    public required string[] Acido { get; init; }

    public string[] For(Mood mood) => mood switch
    {
        Mood.FimDeDia => Medio,
        Mood.Cansado => Acido,
        _ => Leve,
    };

    public string Pick(Mood mood)
    {
        string[] pool = For(mood);
        return pool[Rng.Next(pool.Length)];
    }

    public string PickNow() => Pick(PeemoMood.Current);
}
