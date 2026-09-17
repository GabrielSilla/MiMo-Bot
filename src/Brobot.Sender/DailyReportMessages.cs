namespace Brobot.Sender;

/// <summary>
/// The line (and face) SendDailyReport picks to go with today's numbers —
/// same "Sender decides the text, Core just renders it" split as
/// PausaMessages/WeatherAlerts/GreetingMessages. One flat pool per
/// DailyPerformanceRating, one random pick per report, so the same verdict
/// doesn't read identically every time it lands. Same voice rules as every
/// other pool here: casual buddy tone, nothing that implies MiMo remembers
/// *other* days ("de novo", "que nem ontem") — it only ever knows today's
/// own numbers.
/// </summary>
internal static class DailyReportMessages
{
    private static readonly Random Rng = new();

    public static string RandomFor(DailyPerformanceRating rating)
    {
        string[] pool = rating switch
        {
            DailyPerformanceRating.Pessimo => Pessimo,
            DailyPerformanceRating.Ruim => Ruim,
            DailyPerformanceRating.Questionavel => Questionavel,
            DailyPerformanceRating.Medio => Medio,
            DailyPerformanceRating.Bom => Bom,
            DailyPerformanceRating.Excelente => Excelente,
            _ => Medio,
        };
        return pool[Rng.Next(pool.Length)];
    }

    /// <summary>The &lt;RATING&gt; token in the REPORT wire command (see PROTOCOL.md) — Core, not Sender, decides how each rating actually reads/looks on screen (drawReportNotification in Face.cpp).</summary>
    public static string WireToken(DailyPerformanceRating rating) => rating switch
    {
        DailyPerformanceRating.Pessimo => "PESSIMO",
        DailyPerformanceRating.Ruim => "RUIM",
        DailyPerformanceRating.Questionavel => "QUESTIONAVEL",
        DailyPerformanceRating.Medio => "MEDIO",
        DailyPerformanceRating.Bom => "BOM",
        DailyPerformanceRating.Excelente => "EXCELENTE",
        _ => "MEDIO",
    };

    /// <summary>Sender's own RelatorioStatusText label — unrelated to what Core draws on MiMo's screen.</summary>
    public static string RatingLabel(DailyPerformanceRating rating) => rating switch
    {
        DailyPerformanceRating.Pessimo => "Péssimo",
        DailyPerformanceRating.Ruim => "Ruim",
        DailyPerformanceRating.Questionavel => "Questionável",
        DailyPerformanceRating.Medio => "Médio",
        DailyPerformanceRating.Bom => "Bom",
        DailyPerformanceRating.Excelente => "Excelente",
        _ => "Médio",
    };

    private static readonly string[] Pessimo =
    {
        "Hoje foi osso, hein? Amanha a gente vira o jogo.",
        "Dia pesado esse, viu. Bora descansar e recomecar amanha.",
        "Esse dia nao foi dos bons, mas amanha e outra chance.",
        "Complicado hoje, hein? Da um tempo pra cabeca.",
        "Dia dificil esse, mas nao desanima nao.",
        "Hoje nao rendeu muito, mas ninguem e assim todo dia.",
        "Esse foi osso mesmo, cara. Amanha bora com tudo.",
        "Dia meio perdido esse, mas tudo bem, acontece.",
        "Hoje travou geral, hein. Reseta e amanha vale mais.",
        "Nao foi um dos melhores dias, mas passou.",
        "Esse dia pesou pro lado errado, hein. Ate amanha, vamos nessa.",
        "Dia dos fracos, brincadeira, mas hoje nao foi facil mesmo.",
    };

    private static readonly string[] Ruim =
    {
        "Hoje ficou devendo um pouco, hein.",
        "Dia abaixo do esperado, mas amanha da pra ajustar.",
        "Faltou empurrao hoje, mas tudo bem.",
        "Hoje nao foi tao produtivo assim, ne.",
        "Deu uma travada no dia hoje, hein.",
        "Dia meio de lado hoje, mas segue o jogo.",
        "Hoje rendeu menos do que podia, mas ok.",
        "Ficou devendo hoje, mas sem drama.",
        "Dia fraquinho esse, cara.",
        "Hoje foi mais devagar que o normal.",
        "Nao foi ruim ruim, mas podia ter rendido mais.",
        "Dia meio capenga hoje, mas passou.",
    };

    private static readonly string[] Questionavel =
    {
        "Hoje foi meio estranho, hein, nem bom nem ruim.",
        "Dia dividido esse, teve de tudo um pouco.",
        "Hoje ficou naquela duvida, sinceramente.",
        "Dia meio sem direcao hoje, hein.",
        "Nao sei nem o que dizer desse dia, foi... diferente.",
        "Hoje foi de resultado duvidoso, digamos assim.",
        "Dia esquisito esse, misturou tudo.",
        "Hoje rendeu, mas tambem nao rendeu, sabe?",
        "Dia meio confuso, mas segue o baile.",
        "Hoje foi tipo assim... ne? Vai entender.",
        "Dia sem definicao clara hoje, hein.",
        "Meio no limbo hoje, cara, nem pra ca nem pra la.",
    };

    private static readonly string[] Medio =
    {
        "Dia tranquilo hoje, nem muito nem pouco.",
        "Hoje foi um dia normal, sem grandes emocoes.",
        "Dia mediano esse, cumpriu o basico.",
        "Hoje foi de boa, sem exagero pra nenhum lado.",
        "Dia OK esse, dentro do esperado.",
        "Hoje rolou o de sempre, tranquilo.",
        "Dia pacato hoje, sem sustos.",
        "Hoje foi na media mesmo, sem drama.",
        "Dia comum esse, cumpriu tabela.",
        "Hoje foi tranquilo, nada de mais pra reportar.",
        "Dia sem grandes picos hoje, tudo certinho.",
        "Hoje passou reto, dia de rotina mesmo.",
    };

    private static readonly string[] Bom =
    {
        "Hoje rendeu bem, hein! Mandou bem.",
        "Dia bom esse, parabens pelo esforco.",
        "Hoje foi solido, gostei do resultado.",
        "Dia produtivo esse, mandou bem demais.",
        "Hoje deu pra sentir o esforco, ficou bom.",
        "Dia positivo esse, seguindo assim vai longe.",
        "Hoje rendeu de verdade, bom trabalho.",
        "Dia bacana esse, ficou com saldo positivo.",
        "Hoje foi tranquilo pro lado bom, mandou bem.",
        "Dia que valeu a pena esse, parabens.",
        "Hoje o resultado apareceu, bom demais.",
        "Dia redondo esse, ficou show.",
    };

    private static readonly string[] Excelente =
    {
        "Hoje foi excelente, arrasou de verdade!",
        "Dia sensacional esse, meus parabens!",
        "Hoje rendeu demais, mandou muito bem!",
        "Dia impecavel esse, olha esse resultado!",
        "Hoje foi top demais, continua assim!",
        "Dia daqueles memoraveis, excelente trabalho!",
        "Hoje voce arrasou geral, parabens mesmo!",
        "Dia espetacular esse, olha esses numeros!",
        "Hoje foi brilhante, muito bem!",
        "Dia de respeito esse, ficou excelente!",
        "Hoje foi enorme, parabens pelo resultado!",
        "Dia perfeito quase esse, mandou muito bem!",
    };
}
