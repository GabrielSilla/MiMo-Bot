namespace Brobot.Sender;

/// <summary>
/// The screen-time nudges (every 15 min of focused YouTube / social media,
/// see DailyReportTracker's milestones) and the CPU/RAM alerts
/// (ResourceAlertMonitor), toned by Peemo's mood — see PeemoMood and
/// specs/voice-guide.md. All four are alerts first: every phrase keeps the
/// number ({min} / {percent}) so the point of the alert survives whatever
/// the joke is. The nudges never tell anyone to stop (Peemo is an
/// accomplice, not a supervisor); CPU/RAM is a reminder card, so it may
/// suggest closing something.
/// </summary>
internal static class AlertMessages
{
    private static readonly MoodPhrases VideoWatch = new()
    {
        Leve = new[]
        {
            "{min} min de YouTube! Deve estar bom esse vídeo.",
            "Já são {min} min de YouTube, só avisando.",
            "{min} min de vídeo! Tô assistindo junto daqui.",
            "Opa, {min} min de YouTube. Tá rendendo, hein?",
            "{min} min assistindo! Pisca um pouco, seus olhos agradecem.",
            "YouTube já tá em {min} min. Só pra você saber.",
            "{min} min de vídeo, hein! Sem julgamento.",
            "Já deu {min} min de YouTube. O tempo voa, né?",
            "{min} min no YouTube! A pipoca já acabou?",
            "Relógio do YouTube: {min} min. Aviso de amigo.",
        },
        Medio = new[]
        {
            "{min} min de YouTube. Mais um e vira maratona.",
            "{min} min de vídeo. O algoritmo tá te conhecendo bem.",
            "Já são {min} min de YouTube. O 'próximo vídeo' é esperto.",
            "{min} min assistindo. Isso conta como pesquisa?",
            "YouTube em {min} min. Se perguntarem, era tutorial.",
            "{min} min de vídeo. O autoplay agradece a audiência.",
            "{min} min no YouTube. Quase dá pra pedir certificado.",
            "{min} min de vídeo. O dia tá indo embora, só dizendo.",
            "{min} min de YouTube. O botão de pular intro te conhece.",
            "{min} min assistindo. Nem eu processo tanto vídeo.",
        },
        Acido = new[]
        {
            "{min} min de YouTube. O 'só mais um' tá ganhando de lavada.",
            "{min} min de vídeo a essa hora. O algoritmo venceu.",
            "{min} min de YouTube. Com esse tempo, já dá pra monetizar.",
            "{min} min assistindo. O travesseiro tá com ciúme.",
            "YouTube em {min} min. Amanhã eu nem vou lembrar disso.",
            "{min} min de vídeo. Nem o criador do canal assiste tanto.",
            "{min} min de YouTube. Já é praticamente um curso.",
            "{min} min assistindo. Vou fingir que era documentário.",
            "{min} min de YouTube a essa hora. Coragem.",
            "{min} min de vídeo. O autoplay tá rindo de nós dois.",
        },
    };

    private static readonly MoodPhrases SocialWatch = new()
    {
        Leve = new[]
        {
            "{min} min de rede social! Só avisando, sem julgamento.",
            "Já são {min} min nas redes, só pra você saber.",
            "{min} min rolando o feed! Pisca um pouco.",
            "Opa, {min} min de rede social. Tá bom o feed hoje?",
            "{min} min nas redes! Aviso de amigo.",
            "Redes sociais: {min} min. Eu só conto, não julgo.",
            "{min} min de rolagem! Seu polegar tá bem?",
            "Já deu {min} min de rede social. O tempo voa.",
            "{min} min nas redes, hein! Algum meme bom?",
            "Relógio das redes: {min} min. Tô só avisando.",
        },
        Medio = new[]
        {
            "{min} min de rede social. O feed não acaba, já te adianto.",
            "{min} min nas redes. Se perguntarem, era pesquisa de mercado.",
            "Já são {min} min de feed. O algoritmo tá feliz.",
            "{min} min de rede social. Seu polegar já fez academia hoje.",
            "{min} min rolando. O fim do feed é uma lenda.",
            "Redes sociais em {min} min. Não vou contar pra ninguém.",
            "{min} min nas redes. O dia tá acabando, o feed não.",
            "{min} min de rede social. Que resistência, hein.",
            "{min} min de feed. Conta como networking?",
            "{min} min nas redes. Fofoca boa hoje, pelo visto.",
        },
        Acido = new[]
        {
            "{min} min de rede social a essa hora. O algoritmo agradece.",
            "{min} min de feed. Nem o dono da rede passa tanto tempo lá.",
            "{min} min nas redes. O feed é infinito, sua bateria não.",
            "{min} min rolando a tela. Seu polegar pediu férias.",
            "{min} min de rede social. O travesseiro tá esperando, só isso.",
            "{min} min nas redes. Vou fingir que era trabalho.",
            "{min} min de feed. Nada mudou no último minuto, confia.",
            "{min} min de rede social. O algoritmo já te conhece melhor que eu.",
            "{min} min nas redes a essa hora. Coragem.",
            "{min} min de feed. Eu desligaria, mas quem sou eu.",
        },
    };

    private static readonly MoodPhrases CpuHigh = new()
    {
        Leve = new[]
        {
            "CPU em {percent}%! Tô suando aqui, dá uma aliviada?",
            "CPU em {percent}%! O PC tá fazendo força, hein.",
            "Opa, CPU em {percent}%! Esquentou por aqui.",
            "CPU em {percent}%! Fecha algo pesado, se der.",
            "CPU em {percent}%! O ventilador tá fazendo cardio.",
            "CPU em {percent}%! Tá rodando coisa pesada, hein?",
            "CPU em {percent}%! Dá um respiro pro processador.",
            "CPU em {percent}%! Tô sentindo o calor daqui.",
        },
        Medio = new[]
        {
            "CPU em {percent}%. Tô trabalhando mais que muita gente hoje.",
            "CPU em {percent}%. O processador pediu hora extra.",
            "CPU em {percent}%. Algo aí tá comendo tudo, dá uma olhada.",
            "CPU em {percent}%. O PC também tá com cara de fim de dia.",
            "CPU em {percent}%. O ventilador já pensa em pedir as contas.",
            "CPU em {percent}%. Fecha o que não precisa, ele agradece.",
            "CPU em {percent}%. Esse suor aqui não é à toa.",
            "CPU em {percent}%. Tá rodando um foguete aí?",
        },
        Acido = new[]
        {
            "CPU em {percent}% a essa hora. Nem o processador aguenta.",
            "CPU em {percent}%. Vocês dois precisam de descanso.",
            "CPU em {percent}%. O PC tá mais cansado que eu, e olha que...",
            "CPU em {percent}%. Fecha algo antes que ele desmaie.",
            "CPU em {percent}%. O ventilador tá gritando, eu só traduzo.",
            "CPU em {percent}%. Isso é um PC ou uma torradeira?",
            "CPU em {percent}%. Salva tudo e dá um respiro pra ele.",
            "CPU em {percent}%. Meus pêsames pro processador.",
        },
    };

    private static readonly MoodPhrases RamHigh = new()
    {
        Leve = new[]
        {
            "RAM em {percent}%! Hora de fechar algumas coisas.",
            "RAM em {percent}%! A memória tá cheinha.",
            "Opa, RAM em {percent}%! Fecha umas abas, se der.",
            "RAM em {percent}%! O PC tá lembrando de coisa demais.",
            "RAM em {percent}%! Dá um respiro pra memória.",
            "RAM em {percent}%! Tem aba esquecida aí, aposto.",
            "RAM em {percent}%! Tô suando só de olhar.",
            "RAM em {percent}%! Libera um espacinho, vai.",
        },
        Medio = new[]
        {
            "RAM em {percent}%. Quantas abas abertas, hein?",
            "RAM em {percent}%. Memória lotada tipo fim de expediente.",
            "RAM em {percent}%. Fecha o que não tá usando, ele agradece.",
            "RAM em {percent}%. O navegador tá comendo tudo, aposto.",
            "RAM em {percent}%. Essa memória não é infinita, só dizendo.",
            "RAM em {percent}%. Tem programa aberto desde cedo aí, né?",
            "RAM em {percent}%. Arrumar as abas conta como produtividade.",
            "RAM em {percent}%. A memória pediu socorro.",
        },
        Acido = new[]
        {
            "RAM em {percent}%. Fecha umas abas, eu não sou de ferro.",
            "RAM em {percent}%. A memória do PC também quer cama.",
            "RAM em {percent}%. Quantas abas? Não conta, não quero saber.",
            "RAM em {percent}%. Fecha alguma coisa antes que o PC trave.",
            "RAM em {percent}%. O navegador tá de banquete.",
            "RAM em {percent}%. Salva tudo, só por precaução.",
            "RAM em {percent}%. Até eu tô com falta de memória agora.",
            "RAM em {percent}%. O PC tá mais lotado que ônibus às 18h.",
        },
    };

    public static string ForVideoWatch(int minutes) => VideoWatch.PickNow().Replace("{min}", minutes.ToString());

    public static string ForSocialWatch(int minutes) => SocialWatch.PickNow().Replace("{min}", minutes.ToString());

    public static string ForCpu(int percent) => CpuHigh.PickNow().Replace("{percent}", percent.ToString());

    public static string ForRam(int percent) => RamHigh.PickNow().Replace("{percent}", percent.ToString());
}
