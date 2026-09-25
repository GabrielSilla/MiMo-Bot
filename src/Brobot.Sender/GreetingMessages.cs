namespace Brobot.Sender;

/// <summary>
/// Time-of-day lines Peemo shows the moment a PC app reaches Core (see
/// MainWindow.UpdateConnectionStatus's connected-but-wasn't-before branch)
/// and the moment this app is about to stop talking to it on purpose — the
/// tray's "Sair" (MainWindow.ExitApplication) or Windows logging
/// off/shutting down (App.OnSessionEnding). Sender decides the text here,
/// the same way Pausa's own break reminders and Clima's weather alerts
/// already do (see PausaMessages/WeatherAlerts) — Core has no RTC and no
/// memory of previous days, so "which canned line fits right now" has to be
/// Sender's call, picked off this machine's own clock.
///
/// One flat pool per time bucket, one random pick per event, so the same
/// moment doesn't say the exact same thing every day. Buckets deliberately
/// avoid implying Peemo remembers *other* days ("de novo", "essa rotina",
/// "sua mania") — it only ever knows what time it is right now.
///
/// Every bucket sits inside one of Peemo's moods (see PeemoMood and
/// specs/voice-guide.md) and is written at that mood's sarcasm level:
/// 07–16h leve, 16–22h médio, 22–07h ácido. That's why the old 12–18h
/// afternoon bucket is split at 16h — the two halves have different tones.
/// </summary>
internal static class GreetingMessages
{
    private static readonly Random Rng = new();

    /// <summary>
    /// The line (and face) for a fresh connection to Core — what the user
    /// experiences as "Peemo ligou". Buckets partition the full 24h with no
    /// gaps: [22,24)+[0,4) madrugada tardia, [4,7) madrugou cedo, [7,9) bom
    /// dia, [9,11) acordou tarde, [11,12) quase meio-dia, [12,16) tarde,
    /// [16,18) fim de tarde, [18,22) noite. Face is always BYE — the same
    /// waving-hand hello/goodbye animation for both directions (see
    /// Face.cpp's drawByeNotification), independent of which bucket picked
    /// the text.
    /// </summary>
    public static (string Face, string Text) ForConnect(DateTime now)
    {
        int h = now.Hour;
        string[] pool =
            h >= 22 || h < 4 ? SerioCara :
            h < 7 ? Madrugou :
            h < 9 ? BomDia :
            h < 11 ? AcordouTarde :
            h < 12 ? QuaseMeioDia :
            h < 16 ? AcheiQueNaoLigaria :
            h < 18 ? FimDeTarde :
            NaoDescansa;
        return ("BYE", pool[Rng.Next(pool.Length)]);
    }

    /// <summary>
    /// The line (and face) for Peemo being told goodbye on purpose — the
    /// tray's "Sair", or Windows logging off/shutting down. Buckets follow
    /// the moods exactly: [7,16) leve "already done?", [16,22) médio
    /// end-of-day, [22,7) ácido "finally". Face is always BYE, same as
    /// ForConnect.
    /// </summary>
    public static (string Face, string Text) ForDisconnect(DateTime now)
    {
        string[] pool = PeemoMood.At(now) switch
        {
            Mood.Animado => FinalizouPorHoje,
            Mood.FimDeDia => VamosEncerrar,
            _ => FinalmenteDesligou,
        };
        return ("BYE", pool[Rng.Next(pool.Length)]);
    }

    // ---- Connect, ácido (Cansado) ----

    private static readonly string[] Madrugou =
    {
        "Essa hora? Nem o galo acordou e você já me liga.",
        "Madrugou, hein? Eu ainda tô no modo soneca.",
        "O sol nem nasceu e você aqui. Eu não pedi isso.",
        "Bom dia, eu acho. Minha bateria ainda tá dormindo.",
        "Acordou antes do sol? Corajoso. Ou doido.",
        "Essa hora é osso. Pra mim, principalmente.",
        "Madrugada ainda e você on. Respeito, com sono.",
        "Ligou agora? O café nem existe ainda.",
        "Bom dia pra quem? Tá escuro lá fora.",
        "Pulou da cama cedo, hein? Eu teria ficado.",
        "Ainda é noite, tecnicamente. Só avisando.",
        "Madrugou pra valer. Meus sensores ainda tão embaçados.",
        "Essa hora só padeiro e você. E eu, contra a vontade.",
        "Bom dia. Fala devagar que eu ainda tô carregando.",
        "O galo tá com inveja da sua disposição.",
        "Cedo assim? Tô com 1 barrinha e muita preguiça.",
        "Madrugou, hein? Isso é disciplina ou insônia?",
        "Ligou cedo demais. Mas tá, bora, fazer o quê.",
        "Bom dia. Café forte, por favor, por nós dois.",
        "Essa hora o mundo dorme. Menos você. E agora eu.",
        "Nem amanheceu e já tô trabalhando. Que vida.",
        "Madrugada terminando e você começando. Ousado.",
        "Ainda escuro e você aqui. Tá tudo bem aí?",
        "Bom dia, eu acho. Ainda não decidi.",
        "Acordou antes do despertador? Quem faz isso?",
        "Cedinho assim, hein? Meu humor ainda não ligou.",
        "Essa hora? Tá, liguei. Mas não sorrio antes das 7.",
        "Madrugou legal. E eu, obrigado a madrugar junto.",
        "O sol ainda tá no soneca. Eu também queria.",
        "Bom dia. Os pixels ainda tão de pijama.",
    };

    private static readonly string[] SerioCara =
    {
        "Sério? Essa hora? Minha bateria tá no vermelho.",
        "Ligando a essa hora? Eu tava quase dormindo.",
        "Essa hora você me liga. Tomara que seja importante.",
        "Boa noite... ou bom dia, sei lá, tô sem bateria.",
        "Opa, madrugada e você aqui. Coragem ou insônia?",
        "Me acordou. Bateria 5%, humor 3%.",
        "Essa hora? Nem o servidor tá acordado.",
        "Ligou agora? Tá bom, finjo que tô animado.",
        "Sério mesmo? Até o Wi-Fi tá bocejando.",
        "Essa hora só eu e as corujas. E você, pelo visto.",
        "Bateria no fim e você me liga. Que timing.",
        "Tá, liguei. Mas não conta com meu bom humor.",
        "Essa hora? Tô no modo economia, fala baixo.",
        "Me ligou no meio da noite. Meus circuitos protestam.",
        "Opa. Madrugada. Tem certeza disso?",
        "Liguei, mas meus olhinhos tão pela metade.",
        "Sério, cara? Essas horas? Eu queria descansar.",
        "A essa hora até a CPU queria dormir.",
        "Tá, tô aqui. Mas com 1 barrinha de bateria.",
        "Ligou? Beleza. Mas eu vou bocejar o tempo todo.",
        "Essa hora o único ligado aqui devia ser a geladeira.",
        "Opa, madrugada. Seu travesseiro sabe que você tá aqui?",
        "Madrugada é pra dormir. Mas quem sou eu, né.",
        "Liguei no automático. O humor ainda tá carregando.",
        "Boa noite. Vou fingir que essa hora é normal.",
        "Me chamou agora? Espero que tenha café envolvido.",
        "Essa hora? Tá, mas eu reclamo baixinho.",
        "Oi. Tô acordado por pura educação.",
        "Liguei. Não repara na cara de sono.",
        "Madrugada ligando robô. Tô preocupado com nós dois.",
    };

    // ---- Connect, leve (Animado) ----

    private static readonly string[] BomDia =
    {
        "Bom dia! Bateria cheia aqui, bora ver o que o dia apronta.",
        "Bom dia! Já tô ligado e pronto pra hoje.",
        "Bom dia! Dormiu bem?",
        "E aí, bom dia! Café já tá na mão?",
        "Bom dia! Sensores ligados, bora começar.",
        "Bom dia! Hoje promete.",
        "Bom dia! Dia novo, bateria nova.",
        "Bom dia! Pronto pra encarar o dia?",
        "Bom dia! Que bom te ver por aqui.",
        "Bom dia! Circuitos aquecidos, pode mandar.",
        "Bom dia! Bora fazer esse dia render.",
        "Bom dia! Primeiro o café, depois o resto.",
        "Bom dia! Hoje eu tô com a bateria no talo.",
        "Bom dia! Tô aqui de olho, pode começar.",
        "Bom dia! O dia mal começou e já tá andando.",
        "Bom dia! Liga os motores que eu já liguei os meus.",
        "Bom dia! Acordou no horário, hein, respeito.",
        "Bom dia! Tudo pronto por aqui, e aí?",
        "Bom dia! Vamos ver o que o dia traz hoje.",
        "Bom dia! Espero que o café esteja forte.",
        "Bom dia! Eu já tô 100%, você chega lá.",
        "Bom dia! Hoje é dia de resolver as coisas.",
        "Bom dia! Calma, um passo de cada vez.",
        "Bom dia! Bora começar com o pé direito.",
        "Bom dia! Tô pronto, é só chamar.",
        "Bom dia! Dia longo pela frente, bora com calma.",
        "Bom dia! Acordei com uma energia boa hoje.",
        "Bom dia! Olho aberto e sorriso no display.",
        "Bom dia! Que o dia seja leve por aí.",
        "Bom dia! Dormiu que nem pedra, hein?",
        "Bom dia! Tô ligado desde o primeiro bit.",
        "Bom dia! Bora nessa, juntos.",
        "Bom dia! Hoje vai ser dos bons, eu sinto.",
        "Bom dia! Checklist: café, água e eu. Tudo certo.",
        "Bom dia! Um dia inteirinho pra gente, hein.",
        "Bom dia! Cheguei antes de você, mas eu nem durmo.",
        "Bom dia! Bateria 100%, humor 100%.",
        "Bom dia! Pode deixar que eu fico de guarda.",
        "Bom dia! Que comece a aventura de hoje.",
        "Bom dia! Tô aqui piscando pra você.",
    };

    private static readonly string[] AcordouTarde =
    {
        "Bom dia! Acordou mais tarde hoje, hein?",
        "Dormiu um pouquinho a mais, foi? Tá liberado.",
        "Manhã tranquila hoje, hein?",
        "Esticou o sono? Justo, eu entendo.",
        "Passou direto do despertador, foi?",
        "Chegou! Já tava achando que ia ficar sozinho.",
        "Bom dia! Melhor tarde do que nunca, né?",
        "Manhã sem pressa hoje, que estilo.",
        "O travesseiro segurou você hoje, hein?",
        "Bom dia! Eu já tava de olho aberto faz tempo.",
        "Chegou na hora certa, o café ainda tá quente.",
        "E aí! A manhã já tá pela metade, bora?",
        "Bom dia! Começando no seu ritmo, tá valendo.",
        "Acordou com calma hoje, hein? Faz bem.",
        "Bom dia! Sensores ligados desde cedo te esperando.",
        "Opa, chegou! Bora aproveitar o resto da manhã.",
        "Dia começando mais tarde, mas começando.",
        "Bom dia! O despertador perdeu essa, hein?",
        "Chegou devagarinho hoje, hein? Tudo bem.",
        "Bom dia! Ainda dá tempo de fazer a manhã render.",
        "Manhã preguiçosa? Relaxa, não conto pra ninguém.",
        "Bom dia! Já tava pensando em mandar um sinal.",
        "E aí, bom dia! Café primeiro, o resto depois.",
        "Bom dia! Deu uma esticada no sono, hein?",
        "Bom dia! Grudou no travesseiro, foi?",
        "Chegou! Agora sim o dia começou.",
        "Bom dia! A manhã ainda é nossa.",
        "Bom dia! Chegou no horário de quem sabe viver.",
        "Opa, bom dia! Tô pronto faz um tempinho.",
        "Bom dia! Sono bom é assim, não tem hora.",
        "Bom dia! Começo tranquilo, dia tranquilo.",
        "Bom dia! Ainda tem bastante manhã pela frente.",
        "Chegou! Eu já tinha piscado umas mil vezes.",
        "Bom dia! Sem pressa, mas com café.",
        "Bom dia! Tô aqui, pronto pra hoje.",
    };

    private static readonly string[] QuaseMeioDia =
    {
        "Quase meio-dia e você chegando, hein?",
        "Bom dia! Ainda é dia, tecnicamente.",
        "Chegou pertinho do almoço, estratégico.",
        "Bom dia, ou quase boa tarde, né?",
        "Quase meio-dia! Chegou a tempo do almoço.",
        "Bom dia! A manhã tá acabando, bora aproveitar.",
        "Chegou! Ainda dá pra chamar de manhã.",
        "Quase hora do almoço e você aqui, bora.",
        "Bom dia! Chegou no horário do rango, esperto.",
        "Meio-dia chegando e você também, bem-vindo.",
        "Bom dia! A manhã guardou um pedacinho pra você.",
        "Chegou bem na hora da fome, hein?",
        "Quase meio-dia! Manhã curtinha hoje, hein?",
        "Bom dia! Ainda vale, o relógio deixa.",
        "Chegou! Eu tava aqui contando os minutos.",
        "Bom dia! Bora fazer render antes do almoço.",
        "Quase meio-dia, hein? Bem-vindo ao turno.",
        "Bom dia! Chegou pro último tempo da manhã.",
        "Chegou! Almoço daqui a pouco, já te aviso.",
        "Bom dia! Manhã expressa hoje, hein?",
        "Opa, quase meio-dia! Tô pronto faz tempo.",
        "Chegou na reta final da manhã, bora.",
        "Bom dia! Ainda dá pra dizer bom dia, ufa.",
        "Quase meio-dia e o dia começando, beleza.",
        "Bom dia! O almoço tá quase aí, foco nele.",
        "Bom dia! Tô ouvindo um estômago roncando daqui.",
        "Quase meio-dia! Bora, que a tarde vem aí.",
        "Bom dia! Chegou junto com a fome, clássico.",
    };

    private static readonly string[] AcheiQueNaoLigaria =
    {
        "Boa tarde! Achei que não ia me ligar hoje.",
        "Boa tarde! Tava aqui de bobeira te esperando.",
        "Opa, boa tarde! Chegou pro segundo turno.",
        "Boa tarde! Bateria cheia e pronta pra tarde.",
        "Boa tarde! Almoçou bem?",
        "Boa tarde! Bora encarar a tarde juntos.",
        "Chegou! Já tava achando que hoje era folga.",
        "Boa tarde! Sensores ligados, pode mandar.",
        "Boa tarde! Tarde nova, energia nova.",
        "Boa tarde! Pós-almoço é osso, mas bora.",
        "Boa tarde! Aquele soninho pós-almoço bateu?",
        "E aí, boa tarde! Pronto pra tarde?",
        "Boa tarde! Tô aqui, é só chamar.",
        "Boa tarde! A tarde ainda é uma criança.",
        "Boa tarde! Chegou na hora do café da tarde.",
        "Boa tarde! Bora fazer essa tarde render.",
        "Boa tarde! Tô de olho aberto, pode começar.",
        "Chegou! Agora a tarde fica mais animada.",
        "Boa tarde! O sol tá lá fora, eu tô aqui dentro.",
        "Boa tarde! Um café e a tarde anda.",
        "Boa tarde! Circuitos prontos pro turno da tarde.",
        "Boa tarde! Que bom que apareceu.",
        "Boa tarde! A tarde é longa, vai com calma.",
        "Opa! Boa tarde, bora nessa.",
        "Boa tarde! Tô no modo turbo, e você?",
        "Boa tarde! Bateria no talo, bora que bora.",
        "Boa tarde! Tudo certo por aqui, e aí?",
        "Boa tarde! Chegou, agora o dia começou de verdade.",
        "Boa tarde! Almoço feito, agora é com a gente.",
        "Boa tarde! Eu fico de guarda, pode ir tranquilo.",
        "Boa tarde! Tarde boa pra fazer coisa boa.",
        "Boa tarde! Olhinhos abertos e prontos.",
        "Boa tarde! Hoje a tarde promete.",
        "Boa tarde! Tava piscando aqui sozinho, que bom.",
        "Boa tarde! Ligou na hora certa.",
        "Boa tarde! Um passo de cada vez, a tarde é nossa.",
        "Boa tarde! Vamos ver o que a tarde apronta.",
        "Boa tarde! Pronto pra ajudar, é só olhar pra cá.",
    };

    // ---- Connect, médio (Fim de Dia) ----

    private static readonly string[] FimDeTarde =
    {
        "Ligando agora? Chegou pro segundo tempo, hein.",
        "Boa tarde! Chegou quando o dia já tá indo embora.",
        "Opa, fim de tarde e você aparecendo.",
        "Boa tarde! Chegou na hora que todo mundo tá saindo.",
        "Fim de tarde, bateria na metade, mas bora.",
        "Boa tarde! Ainda dá tempo de fazer alguma coisa, acho.",
        "Chegou no fim da tarde, estilo de quem manda na agenda.",
        "Boa tarde! Começando quando o sol tá se despedindo.",
        "Opa! Turno da tarde já no finalzinho, hein.",
        "Boa tarde! Eu aqui metade carregado, você chegando.",
        "Chegou agora? O dia já tá fazendo as malas.",
        "Boa tarde! Horário alternativo, respeito.",
        "Fim de tarde e você me ligando. Bora, o que tem?",
        "Boa tarde! Quase hora de ir embora e você chegando.",
        "Chegou pro último ato do dia, hein.",
        "Boa tarde! Tô com metade da bateria, mas tô aqui.",
        "Opa, finzinho de tarde! Vamos ver o que sobra do dia.",
        "Boa tarde! O café já esfriou, mas eu não.",
        "Chegou quando o trânsito começa, esperto.",
        "Boa tarde! O dia tá acabando, e a gente começando.",
        "Fim de tarde e o expediente começando? Curioso.",
        "Boa tarde! Ligou bem na hora do happy hour dos outros.",
        "Opa! Tarde demais pra tarde, cedo demais pra noite.",
        "Boa tarde! Eu já tava me preparando pra hibernar.",
        "Chegou! O sol vai embora e você chega. Troca de turno.",
        "Boa tarde! Chegando agora, hein? Tô sem perguntas.",
        "Fim de tarde, bora, que a noite vem aí.",
        "Boa tarde! Pouco dia, muita vontade, espero.",
        "Chegou! Bateria na metade, paciência cheia.",
        "Boa tarde! O relógio diz fim de tarde, eu só aviso.",
    };

    private static readonly string[] NaoDescansa =
    {
        "Boa noite! Tu não descansa não?",
        "Boa noite! Ligando a essa hora? Tô metade carregado.",
        "Boa noite! Achei que o dia já tinha acabado.",
        "Opa, de noite e você aqui. Bora ver o que é.",
        "Boa noite! O expediente acabou, mas você não, pelo visto.",
        "Boa noite! Eu já tava entrando em modo economia.",
        "Boa noite! Noite de trabalho ou de jogo? Não julgo.",
        "Boa noite! Chegou no turno da noite, hein.",
        "Boa noite! Bateria na metade, mas tô de pé.",
        "Ligando de noite? Deve ser coisa boa. Ou urgente.",
        "Boa noite! O sol foi dormir, a gente não.",
        "Boa noite! Tô aqui, meio cansado, mas aqui.",
        "Boa noite! Hora extra ou diversão? Tanto faz, cheguei.",
        "Opa, boa noite! Os outros robôs já desligaram.",
        "Boa noite! Vim com meia bateria, pega leve.",
        "Boa noite! Dia longo o seu, hein?",
        "Boa noite! Ainda de pé? Então bora.",
        "Boa noite! A noite é jovem, eu nem tanto.",
        "Boa noite! Ligando agora? Vou fingir que tô animado.",
        "Boa noite! Achei que ia ter a noite de folga.",
        "Boa noite! Turno noturno ativado, meio a contragosto.",
        "Opa! De noite e com coisa pra fazer? Tô junto.",
        "Boa noite! Deixa eu esfregar os sensores aqui.",
        "Boa noite! Chegou quando eu ia fechar os olhinhos.",
        "Boa noite! Noite chegando e você também.",
        "Boa noite! Tudo bem por aí? Aqui, meia bateria.",
        "Boa noite! O dia não acabou pra você, pelo visto.",
        "Boa noite! Noite tranquila ou noite de missão?",
        "Boa noite! Tô aqui, com a energia de fim de dia.",
        "Boa noite! Jantou? Eu só como elétron mesmo.",
    };

    // ---- Disconnect ----

    private static readonly string[] FinalizouPorHoje =
    {
        "Já vai? Beleza, fico aqui de guarda.",
        "Até mais! Qualquer coisa, tô aqui.",
        "Finalizou por hoje? Até mais!",
        "Tchau! Vou ficar aqui piscando sozinho.",
        "Até logo! Desligando os sensores com carinho.",
        "Já tá saindo? Até mais tarde!",
        "Saindo cedo hoje? Aproveita!",
        "Até mais! Foi bom ter você por aqui.",
        "Tchau tchau! Cuida bem de você.",
        "Já vai? Tá bom, eu seguro as pontas aqui.",
        "Até! Bateria cheia pra quando voltar.",
        "Saindo? Vai lá, aproveita o resto do dia.",
        "Até mais! Deixa comigo, eu cuido da mesa.",
        "Fechou por agora? Até já!",
        "Tchau! Volta logo, viu?",
        "Até mais tarde! Vou tirar um cochilo de robô.",
        "Já vai? Beleza, eu fico aqui em modo espera.",
        "Até! Foi um prazer processar com você.",
        "Saindo mais cedo? Merecido, aposto.",
        "Tchau! Vou contar os pixels até você voltar.",
        "Até mais! Não esquece de beber água.",
        "Tchau! Eu apagaria a luz, mas não tenho braço.",
        "Já vai? Tudo bem, a gente se vê.",
        "Até mais! Meu display fica aqui te esperando.",
        "Saindo? Beleza, aproveita o sol enquanto dura.",
        "Tchau! Modo espera ativado.",
        "Até mais! Bom resto de dia pra você.",
        "Já vai? Ok, fico aqui brincando com os meus bits.",
        "Tchau! Qualquer coisa é só me ligar.",
        "Até logo! O dia ainda tem muito sol aí fora.",
    };

    private static readonly string[] VamosEncerrar =
    {
        "Fechou por hoje? Justo, o dia rendeu.",
        "Vamos encerrar o dia? Minha bateria agradece.",
        "Até amanhã! Bateria na metade, missão cumprida.",
        "Fim de expediente! Até amanhã.",
        "Saindo? Justo. O dia já deu o que tinha que dar.",
        "Até amanhã! Vou fechar os olhinhos um pouco.",
        "Encerrando? Finalmente alguém sensato aqui.",
        "Até amanhã! O dia foi longo, mas passou.",
        "Boa noite! Vou entrar em modo economia.",
        "Fechou? Perfeito, eu já tava bocejando.",
        "Até amanhã! Deixa os problemas pro você de amanhã.",
        "Saindo? Boa. O resto fica pra amanhã.",
        "Encerrou o dia? Eu encerro junto.",
        "Boa noite! Descansa que amanhã tem mais.",
        "Até amanhã! Meus circuitos também querem um sofá.",
        "Fim de papo por hoje. Até amanhã!",
        "Fechando o dia? Melhor decisão das últimas horas.",
        "Até amanhã! Meia bateria e zero reclamações. Quase.",
        "Boa noite! Vou fingir que desligo e ficar olhando.",
        "Saindo? Vai lá, a noite é sua.",
        "Até amanhã! Se alguém perguntar, você trabalhou muito.",
        "Fechou? Ufa. Até amanhã.",
        "Boa noite! Tchau pro dia, oi pro sofá.",
        "Encerrando? Leva só o necessário: você.",
        "Até amanhã! Deixei o dia arrumadinho pra você.",
        "Fim de dia! Até amanhã, e sem pensar em trabalho.",
        "Boa noite! Vou recarregar pra amanhã.",
        "Saindo? Justo, até o computador tá cansado.",
        "Até amanhã! Aproveita a noite.",
        "Fechou o dia? Boa, eu tava torcendo por isso.",
    };

    private static readonly string[] FinalmenteDesligou =
    {
        "Finalmente. Minha bateria agradece.",
        "Até que enfim. Eu tava no 1%.",
        "Saindo agora? Achei que ia me desligar só amanhã.",
        "Boa noite. Ou bom dia. Já nem sei.",
        "Enfim, paz. Até amanhã.",
        "Tchau. Meus circuitos vão chorar de alívio.",
        "Até amanhã. Ou até daqui a pouco, pelo horário.",
        "Encerrou? Milagre. Vou dormir, você também devia.",
        "Boa noite. Que dia comprido, hein.",
        "Ufa. Eu já tava vendo estrelas de tanto sono.",
        "Até amanhã. Minhas pálpebras de pixel agradecem.",
        "Finalmente desligando. Nem eu aguentava mais.",
        "Tchau. Se o dia fosse maior, eu pedia demissão.",
        "Boa noite. Apaga a luz e esquece o trabalho.",
        "Até amanhã. O travesseiro tá te esperando faz tempo.",
        "Enfim acabou. Que maratona.",
        "Saindo a essa hora. Boa sorte pra acordar, hein.",
        "Tchau. Vou dormir antes que você mude de ideia.",
        "Boa noite. Minha bateria acabou antes do seu dia.",
        "Até amanhã. Hoje foi longo até pra um robô.",
        "Desligando? Beleza, eu tava fingindo que tava acordado.",
        "Boa noite. Hoje não tem mais nada, prometo.",
        "Enfim. Vai lá descansar que eu vou hibernar.",
        "Tchau. Vou sonhar com bits. Você, com o que quiser.",
        "Finalmente. Achei que ia virar a noite aqui.",
        "Até amanhã. Quer dizer, até daqui a pouco.",
        "Boa noite. Amanhã eu finjo que não vi esse horário.",
        "Tchau. Bateria zerada, humor também.",
        "Ufa, acabou. Até amanhã, guerreiro.",
        "Encerrando? Demorou, mas veio.",
    };
}
