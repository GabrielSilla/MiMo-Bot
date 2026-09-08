namespace Brobot.Sender;

/// <summary>
/// One of MiMo's 10 achievements. Id doubles as the wire token this app
/// sends as ACHIEVEMENT's own `&lt;ID&gt;` field (see PROTOCOL.md and
/// BrobotCore/include/Face.h's AchievementIcon) — Core picks its trophy
/// accent off the very same string, so the two never need a separate lookup
/// table kept in sync by hand. Emoji is WPF-only (the Conquistas tab renders
/// it via the system's own emoji font) — Core's bitmap Font5x7 has no glyph
/// for any of these, so the text sent over ACHIEVEMENT when one unlocks (see
/// AchievementMonitor.Unlocked's handler in MainWindow) never includes it.
/// Description is the still-locked criterion; Quote is what replaces it once
/// unlocked (see MainWindow.RefreshAchievementCard) — the criterion tells
/// you what to aim for, the quote is the payoff.
/// </summary>
public sealed record Achievement(string Id, string Emoji, string Name, string Description, string Quote);

/// <summary>
/// The fixed list of achievements MiMo tracks. All 10 are built from signals
/// this app already observes elsewhere (Conexão, Jogos, Mídia, Atividade da
/// IA, Pausa, Tema) plus the OS's own boot time — no new sensor needed. Two
/// ideas from the original brainstorm (window-focus duration, PC uptime as
/// its own "marathon" achievement) were dropped for exactly that reason:
/// nothing here tracks which window has focus, and while boot time is cheap
/// to read once (see AchievementMonitor's EarlyBird check), a *running*
/// uptime accumulator would need its own always-on timer this app has no
/// other reason to keep.
/// </summary>
public static class AchievementCatalog
{
    public static readonly IReadOnlyList<Achievement> All =
    [
        new Achievement(
            "FIRST_CONTACT", "👋", "FIRST CONTACT",
            "MiMo se conectou pela primeira vez.",
            "Prazer, eu sou o MiMo."),

        new Achievement(
            "EARLY_BIRD", "🌅", "EARLY BIRD",
            "Ligou o PC antes das 7h.",
            "Quem madruga, pega a minhoca."),

        new Achievement(
            "NIGHT_OWL", "🦉", "NIGHT OWL",
            "Ainda trabalhando depois da meia-noite.",
            "Dormir é opcional."),

        new Achievement(
            "COFFEE_MACHINE", "☕", "COFFEE MACHINE",
            "4 horas de atividade no mesmo dia.",
            "Rodando à base de café e código."),

        new Achievement(
            "ONE_MORE_GAME", "🎮", "ONE MORE GAME",
            "Jogou por mais de 3 horas acumuladas.",
            "Só mais uma partidinha."),

        new Achievement(
            "VICTORY_ROYALE", "⚔️", "VICTORY ROYALE",
            "Venceu a Batalha RPG várias vezes.",
            "A tecnologia foi derrotada. Por enquanto."),

        new Achievement(
            "AI_OVERLOAD", "🧠", "AI OVERLOAD",
            "Usou a IA por muito tempo acumulado num dia.",
            "Já pensou em pensar por conta própria?"),

        new Achievement(
            "AUDIOPHILE", "🎧", "AUDIOPHILE",
            "Ouviu música por muitas horas acumuladas.",
            "No repeat desde sempre."),

        new Achievement(
            "BREAK_TAKER", "🧘", "BREAK TAKER",
            "Recebeu vários lembretes de Pausa.",
            "As pernas agradecem (ou não)."),

        new Achievement(
            "IDENTITY_CRISIS", "🎨", "IDENTITY CRISIS",
            "Já experimentou todos os temas do MiMo.",
            "Qual MiMo estamos hoje?"),
    ];
}
