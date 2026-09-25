namespace Brobot.Sender;

/// <summary>
/// Pausa's "go stretch your legs" reminders (NOTIFY COFFEE) — one random
/// pick per trigger, toned by Peemo's mood (see PeemoMood and
/// specs/voice-guide.md). Pausa is a reminder card, so every level still
/// actually suggests the break; the mood only changes how it's said.
/// </summary>
internal static class PausaMessages
{
    private static readonly MoodPhrases Phrases = new()
    {
        Leve = new[]
        {
            "Pausa pro café! Estica as pernas que eu seguro as pontas.",
            "Bora reabastecer o café!",
            "Que tal uma pausinha? Levanta e dá uma volta.",
            "Seus olhos merecem um descanso. Bora de café?",
            "Intervalo chegou! Estica essas pernas aí.",
            "Vai lá, um cafezinho cai bem agora.",
            "Pausa estratégica: levanta, anda um pouco, hidrata.",
            "Cadê aquele café? Hora de uma pausa.",
            "Seu corpo agradece: levanta e dá uma esticada.",
            "Um café rápido e volta com a bateria cheia.",
            "Hora da pausa! Eu fico aqui de olho em tudo.",
            "Levanta um pouquinho, a cadeira não vai fugir.",
            "Pausa! Café, água, janela. Nessa ordem.",
            "Olha pra longe um pouco, seus olhos pedem.",
            "Hora de esticar! Eu estico os pixels daqui.",
            "Pausinha pro café? Eu recomendo muito.",
            "Dá uma volta rapidinho, eu guardo seu lugar.",
            "Intervalo! Ombros pra trás e respira fundo.",
            "Bora de café? Eu vou só na torcida.",
            "Hora de recarregar! Você com café, eu com elétron.",
        },
        Medio = new[]
        {
            "Pausa. Mais um café e esse dia termina rapidinho.",
            "Levanta um pouco, o fim do dia agradece.",
            "Pausa pro café. A tarde ainda não acabou, infelizmente.",
            "Bora de café? Minha bateria tá na metade, a sua também.",
            "Estica as pernas, que o dia já tá pesando.",
            "Pausa! O trabalho espera, prometo que não foge.",
            "Um café agora e a reta final fica mais fácil.",
            "Hora da pausa. Até o monitor precisa de um tempo de você.",
            "Levanta, dá uma volta. A cadeira já tá com o seu formato.",
            "Pausa estratégica: café antes que o dia te vença.",
            "Intervalo! Seus olhos tão pedindo arrego.",
            "Um café pra aguentar o resto do dia? Eu acho justo.",
            "Pausa. Respira, que ainda tem chão até o fim do dia.",
            "Levanta um pouco. Se alguém perguntar, foi ordem médica.",
            "Hora do café. O e-mail pode esperar 5 minutinhos.",
            "Pausa pro café. Metade da bateria, metade do dia.",
            "Dá uma esticada, que a coluna tá mandando sinais.",
            "Bora de café? O fim de tarde pede.",
            "Pausa! E água também conta, só pra constar.",
            "Levanta e anda um pouco, eu seguro o forte.",
        },
        Acido = new[]
        {
            "Pausa a essa hora? Café agora só se for descafeinado.",
            "Pausa. A essa hora devia ser a pausa final, mas tá.",
            "Levanta um pouco. Sua coluna já desistiu de reclamar.",
            "Hora da pausa. Até eu já tô no modo economia.",
            "Pausa pro café. Ou pro travesseiro, fica a dica.",
            "Estica as pernas. Elas já tão dormindo antes de você.",
            "Pausa! Seus olhos tão mais vermelhos que o meu LED.",
            "Levanta, dá uma volta. De preferência até a cama.",
            "Pausa. Café a essa hora é coragem, água é sabedoria.",
            "Hora da pausa. Meus sensores veem um humano cansado.",
        },
    };

    public static string RandomNow() => Phrases.PickNow();
}
