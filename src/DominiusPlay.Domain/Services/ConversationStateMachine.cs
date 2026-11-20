using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Domain.Services;

/// <summary>
/// Máquina de estados da conversa: define quem fala em cada estado e quais transições são permitidas.
/// Garante que Agente de Disparo, Agente de Atendimento e humano nunca respondam ao mesmo tempo.
/// <code>
/// AguardandoContato --AbordagemEnviada--> Prospeccao      (Disparo)
/// AguardandoContato/Prospeccao --ContatoRespondeu--> Ia   (Atendimento; acompanhamentos cancelados)
/// Ia/Prospeccao/AguardandoContato --TransferirHumano/HumanoAssumiu/MensagemManual--> AtendimentoHumano
/// AtendimentoHumano/Pausado --DevolverIa--> Ia
/// qualquer --Pausar--> Pausado ; Pausado --Retomar--> AguardandoContato
/// qualquer --Descadastro/Encerrar/Bloquear--> Encerrado   (nada automático sai depois)
/// </code>
/// </summary>
public static class ConversationStateMachine
{
    public static ConversationSpeaker Speaker(ConversationMode m) => m switch
    {
        ConversationMode.AguardandoContato or ConversationMode.Prospeccao => ConversationSpeaker.AgenteDisparo,
        ConversationMode.Ia => ConversationSpeaker.AgenteAtendimento,
        ConversationMode.AtendimentoHumano => ConversationSpeaker.Humano,
        _ => ConversationSpeaker.Ninguem,
    };

    public static string SpeakerTitle(ConversationSpeaker s) => s switch
    {
        ConversationSpeaker.AgenteDisparo => "Agente de Disparo",
        ConversationSpeaker.AgenteAtendimento => "Agente de Atendimento IA",
        ConversationSpeaker.Humano => "Atendente humano",
        _ => "Ninguém (conversa parada)",
    };

    /// <summary>Próximo estado, ou null se a transição não é permitida a partir do estado atual.</summary>
    public static ConversationMode? Next(ConversationMode from, ConversationTrigger t) => (from, t) switch
    {
        (_, ConversationTrigger.Descadastro or ConversationTrigger.Encerrar or ConversationTrigger.Bloquear) => ConversationMode.Encerrado,
        (ConversationMode.Encerrado, ConversationTrigger.HumanoAssumiu or ConversationTrigger.MensagemManual) => ConversationMode.AtendimentoHumano,
        (ConversationMode.Encerrado, _) => null,
        (_, ConversationTrigger.Pausar) => ConversationMode.Pausado,
        (ConversationMode.Pausado, ConversationTrigger.Retomar) => ConversationMode.AguardandoContato,
        (ConversationMode.Pausado, ConversationTrigger.DevolverIa) => ConversationMode.Ia,
        (ConversationMode.Pausado, ConversationTrigger.HumanoAssumiu) => ConversationMode.AtendimentoHumano,
        (ConversationMode.Pausado, _) => null,
        (ConversationMode.AguardandoContato, ConversationTrigger.AbordagemEnviada) => ConversationMode.Prospeccao,
        (ConversationMode.Prospeccao, ConversationTrigger.AbordagemEnviada) => ConversationMode.Prospeccao,
        (ConversationMode.AguardandoContato or ConversationMode.Prospeccao, ConversationTrigger.ContatoRespondeu) => ConversationMode.Ia,
        (ConversationMode.Ia, ConversationTrigger.ContatoRespondeu) => ConversationMode.Ia,
        (ConversationMode.AtendimentoHumano, ConversationTrigger.ContatoRespondeu) => ConversationMode.AtendimentoHumano,
        (not ConversationMode.AtendimentoHumano, ConversationTrigger.TransferirHumano or ConversationTrigger.HumanoAssumiu or ConversationTrigger.MensagemManual)
            => ConversationMode.AtendimentoHumano,
        (ConversationMode.AtendimentoHumano, ConversationTrigger.HumanoAssumiu or ConversationTrigger.MensagemManual or ConversationTrigger.TransferirHumano)
            => ConversationMode.AtendimentoHumano,
        (ConversationMode.AtendimentoHumano, ConversationTrigger.DevolverIa) => ConversationMode.Ia,
        _ => null,
    };

    /// <summary>Aplica a transição no lead. Retorna false (sem alterar) se não for permitida.</summary>
    public static bool Apply(Lead lead, ConversationTrigger t)
    {
        if (Next(lead.Mode, t) is not { } to) return false;
        lead.Mode = to;
        return true;
    }

    /// <summary>Tipo de envio permitido para quem tem a palavra (Humana/Transição são do sistema e têm regras próprias no SendGuard).</summary>
    public static bool MaySend(ConversationMode m, OutboundKind kind) => kind switch
    {
        OutboundKind.AbordagemInicial => m == ConversationMode.AguardandoContato,
        OutboundKind.Acompanhamento => m is ConversationMode.Prospeccao or ConversationMode.AguardandoContato, // antes da abordagem o SendGuard adia
        OutboundKind.Resposta => m == ConversationMode.Ia,
        _ => true,
    };
}
