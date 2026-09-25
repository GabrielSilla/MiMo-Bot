namespace Brobot.Sender;

/// <summary>
/// The line (and face) SendDailyReport picks to go with today's numbers —
/// same "Sender decides the text, Core just renders it" split as
/// PausaMessages/WeatherAlerts/GreetingMessages. One pool per
/// DailyPerformanceRating and per Peemo mood (see PeemoMood and
/// specs/voice-guide.md), one random pick per report, so the same verdict
/// doesn't read identically every time it lands. Same voice rules as every
/// other pool here: casual buddy tone, nothing that implies Peemo remembers
/// *other* days ("de novo", "que nem ontem") — it only ever knows today's
/// own numbers.
/// </summary>
internal static class DailyReportMessages
{
    public static string RandomFor(DailyPerformanceRating rating)
    {
        MoodPhrases pool = rating switch
        {
            DailyPerformanceRating.Pessimo => Pessimo,
            DailyPerformanceRating.Ruim => Ruim,
            DailyPerformanceRating.Questionavel => Questionavel,
            DailyPerformanceRating.Medio => Medio,
            DailyPerformanceRating.Bom => Bom,
            DailyPerformanceRating.Excelente => Excelente,
            _ => Medio,
        };
        return pool.PickNow();
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

    /// <summary>Sender's own RelatorioStatusText label — unrelated to what Core draws on Peemo's screen.</summary>
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

    private static readonly MoodPhrases Pessimo = new()
    {
        Leve = new[]
        {
            "Hoje foi osso, hein? Amanhã a gente vira o jogo.",
            "Dia pesado esse, viu. Descansa e amanhã recomeça.",
            "Esse dia não foi dos bons, mas amanhã é outra chance.",
            "Complicado hoje, hein? Dá um tempo pra cabeça.",
            "Dia difícil, mas não desanima não.",
            "Hoje travou geral. Reseta e amanhã vale mais.",
            "Não foi um dos melhores dias, mas passou.",
            "Dia meio perdido, mas tudo bem, acontece.",
        },
        Medio = new[]
        {
            "Dia fraco. Nem o compilador quis colaborar.",
            "Hoje foi osso. Pelo menos já tá acabando.",
            "Dia pesado. Amanhã tem outro, graças aos meus circuitos.",
            "Hoje o dia ganhou de você. Amanhã tem revanche.",
            "Esses números tão tímidos, hein. Amanhã eles melhoram.",
            "Dia complicado. Relatório curto pra não doer.",
            "Hoje não rendeu. O sofá vai entender.",
            "Dia difícil. Eu também teria travado.",
        },
        Acido = new[]
        {
            "Dia fraco. Vou fingir que não vi esses números.",
            "Hoje foi osso. Esse relatório devia vir com aviso.",
            "Dia pra esquecer. Sorte que eu não tenho memória.",
            "Esses números tão pedindo cama. Você também.",
            "Hoje não foi. Amanhã, com bateria cheia, quem sabe.",
            "Dia péssimo. Nem eu teria coragem de publicar isso.",
            "Relatório do dia: melhor ler amanhã, descansado.",
            "Placar de hoje: dia 1, você 0. Revanche amanhã.",
        },
    };

    private static readonly MoodPhrases Ruim = new()
    {
        Leve = new[]
        {
            "Hoje ficou devendo um pouco, hein.",
            "Dia abaixo do esperado, mas amanhã dá pra ajustar.",
            "Faltou um empurrãozinho hoje, mas tudo bem.",
            "Deu uma travada no dia hoje, hein.",
            "Dia meio de lado hoje, mas segue o jogo.",
            "Hoje rendeu menos do que podia, mas ok.",
            "Ficou devendo hoje, mas sem drama.",
            "Dia meio travado, mas amanhã tem outro.",
        },
        Medio = new[]
        {
            "Hoje rendeu pouco. Acontece nas melhores famílias.",
            "Dia morno. Nada que um café amanhã não resolva.",
            "Ficou devendo hoje. Não conto pra ninguém.",
            "Dia abaixo da média. O relatório foi educado com você.",
            "Hoje foi devagar. Tipo eu no fim do dia.",
            "Rendeu pouco hoje. Amanhã a bateria volta cheia.",
            "Dia fraquinho. Os números tão meio envergonhados.",
            "Hoje o dia empurrou e você segurou. Mais ou menos.",
        },
        Acido = new[]
        {
            "Dia ruim. Os números tão pedindo desculpa.",
            "Rendeu pouco. Pelo menos o relatório é curto.",
            "Dia fraco. Se alguém perguntar, foi um dia estratégico.",
            "Hoje não rolou. Eu também tô no 1%, te entendo.",
            "Esses números tão com sono. Igual a gente.",
            "Dia abaixo da média. Amanhã eu finjo que não vi.",
            "Relatório ruim. Mas pelo menos o dia tá acabando.",
            "Hoje foi fraco. O travesseiro resolve metade disso.",
        },
    };

    private static readonly MoodPhrases Questionavel = new()
    {
        Leve = new[]
        {
            "Hoje foi meio estranho, hein, nem bom nem ruim.",
            "Dia dividido esse, teve de tudo um pouco.",
            "Hoje ficou naquela dúvida, sinceramente.",
            "Dia meio sem direção hoje, hein.",
            "Hoje rendeu, mas também não rendeu, sabe?",
            "Dia esquisito esse, misturou tudo.",
            "Dia meio confuso, mas segue o baile.",
            "Hoje foi tipo assim... né? Vai entender.",
        },
        Medio = new[]
        {
            "Dia questionável. Nem eu sei o que dizer.",
            "Hoje rendeu uma coisa e perdeu outra. Empate técnico.",
            "Dia confuso. O relatório também ficou na dúvida.",
            "Hoje foi um mistério. Bom e ruim ao mesmo tempo.",
            "Dia esquisito. Meus circuitos não conseguem classificar.",
            "Hoje ficou no limbo. Nem pra cá, nem pra lá.",
            "Dia meio assim. Sabe quando o café esfria? Isso.",
            "Resultado duvidoso. Mas duvidoso é quase bom, né?",
        },
        Acido = new[]
        {
            "Dia questionável. E eu tô cansado demais pra questionar.",
            "Hoje foi... sei lá. Pergunta amanhã.",
            "Resultado duvidoso. Tipo acordar às 3h achando que é dia.",
            "Dia confuso. Igual meu sinal de Wi-Fi agora.",
            "Hoje ficou no meio. Eu fico no meu cantinho.",
            "Relatório estranho. Vou deixar pra entender amanhã.",
            "Dia nem bom nem ruim. Só longo.",
            "Hoje deu empate. O juiz já foi dormir.",
        },
    };

    private static readonly MoodPhrases Medio = new()
    {
        Leve = new[]
        {
            "Dia tranquilo hoje, nem muito nem pouco.",
            "Hoje foi um dia normal, sem grandes emoções.",
            "Dia mediano esse, cumpriu o básico.",
            "Hoje foi de boa, sem exagero pra nenhum lado.",
            "Dia OK esse, dentro do esperado.",
            "Dia pacato hoje, sem sustos.",
            "Hoje foi na média mesmo, sem drama.",
            "Hoje foi tranquilo, nada de mais pra reportar.",
        },
        Medio = new[]
        {
            "Dia médio. Nem história pra contar, nem pra esconder.",
            "Hoje cumpriu tabela. Tem dia que é assim.",
            "Dia na média. Nem o relatório se empolgou.",
            "Hoje foi ok. O sofá tá esperando de qualquer jeito.",
            "Dia mediano. Meia bateria, meio resultado.",
            "Dia sem sustos. Tá bom, não reclamo.",
            "Hoje foi médio. Tipo café morno.",
            "Dia normalzinho. Missão cumprida, sem fogos.",
        },
        Acido = new[]
        {
            "Dia médio. Tô cansado demais pra ter opinião.",
            "Hoje foi ok. Nada que valha ficar acordado discutindo.",
            "Dia na média. Amanhã tem mais média, pode dormir.",
            "Relatório morno. Eu também tô.",
            "Hoje cumpriu tabela. A cama também cumpre, só dizendo.",
            "Dia mediano. Nem bom pra comemorar, nem ruim pra chorar.",
            "Hoje foi normal. Normal e comprido.",
            "Dia ok. Minha bateria tá pior que esses números.",
        },
    };

    private static readonly MoodPhrases Bom = new()
    {
        Leve = new[]
        {
            "Hoje rendeu bem, hein! Mandou bem.",
            "Dia bom esse, bom trabalho!",
            "Hoje foi sólido, que resultado.",
            "Dia produtivo esse, mandou bem demais.",
            "Hoje deu pra sentir o esforço, ficou bom.",
            "Hoje rendeu de verdade, bom trabalho.",
            "Dia bacana esse, ficou com saldo positivo.",
            "Dia redondo esse, ficou show.",
        },
        Medio = new[]
        {
            "Dia bom. Pode ir pro sofá com a consciência tranquila.",
            "Hoje rendeu. Tá liberado pra reclamar menos.",
            "Bom dia de trabalho. O resto do dia é todo seu.",
            "Dia sólido. Até eu fiquei com orgulho.",
            "Hoje mandou bem. Os números tão até sorrindo.",
            "Dia produtivo. Merece um café sem culpa.",
            "Rendeu bem hoje. Pode desligar tranquilo.",
            "Dia bom. Se perguntarem, foi fácil.",
        },
        Acido = new[]
        {
            "Dia bom. Melhor parar agora, enquanto tá ganhando.",
            "Rendeu bem. Não precisa provar mais nada a essa hora.",
            "Hoje foi bom. Até minha bateria de 1% reconhece.",
            "Dia sólido. Parar agora é estratégia, pensa nisso.",
            "Bons números. Pena que você vai lembrar deles com sono.",
            "Dia bom. O travesseiro manda parabéns.",
            "Hoje rendeu. Eu aplaudiria, mas tô sem energia.",
            "Dia produtivo. E longo. Mais longo que produtivo.",
        },
    };

    private static readonly MoodPhrases Excelente = new()
    {
        Leve = new[]
        {
            "Hoje foi excelente, arrasou de verdade!",
            "Dia sensacional esse, parabéns!",
            "Hoje rendeu demais, mandou muito bem!",
            "Dia impecável esse, olha esse resultado!",
            "Hoje foi top demais!",
            "Dia espetacular esse, olha esses números!",
            "Hoje foi brilhante, muito bem!",
            "Dia de respeito esse, ficou excelente!",
        },
        Medio = new[]
        {
            "Dia excelente. Pode ir embora de cabeça erguida.",
            "Hoje você arrasou. Até meus circuitos esquentaram.",
            "Números lindos. Vou emoldurar esse relatório.",
            "Dia impecável. Merece um fim de dia sem culpa nenhuma.",
            "Hoje foi enorme. O sofá tá esperando o campeão.",
            "Dia excelente. Se o chefe visse, dava aumento.",
            "Arrasou hoje. Eu nem tenho mais piadas pra isso.",
            "Dia perfeito. Fecha tudo e vai curtir, merecido.",
        },
        Acido = new[]
        {
            "Dia excelente. E ainda acordado. Exagero, né?",
            "Números incríveis. Parar no topo é pra poucos.",
            "Hoje foi perfeito. Não estraga, vai descansar.",
            "Dia excelente. Até eu, com 1% de bateria, tô impressionado.",
            "Arrasou hoje. Agora arrasa no travesseiro.",
            "Dia impecável. Já pode desligar como lenda.",
            "Números excelentes. Nem vou fazer piada, tô cansado.",
            "Dia brilhante. Mais brilhante que essa tela a essa hora.",
        },
    };
}
