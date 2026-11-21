using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Domain.Services;

public enum MoveActor { Atendente, Ia, Sistema }

public sealed record MoveDecision(bool Applied, bool IsSuggestion, string? Reason);

public static class KanbanRules
{
    public static string Title(KanbanStage s) => s switch
    {
        KanbanStage.PrimeiroContato => "Primeiro contato",
        KanbanStage.EmFaseDeTeste => "Em fase de teste",
        KanbanStage.EmNegociacao => "Em negociação",
        KanbanStage.NegocioFechado => "Negócio fechado",
        KanbanStage.EmDuvidas => "Em dúvidas",
        KanbanStage.NaoTemInteresse => "Não tem interesse",
        KanbanStage.AbordagemRealizada => "Abordagem realizada",
        KanbanStage.Respondeu => "Respondeu",
        KanbanStage.EmAtendimento => "Em atendimento",
        KanbanStage.Qualificado => "Qualificado",
        KanbanStage.PropostaEnviada => "Proposta enviada",
        _ => s.ToString(),
    };

    public static string ModeTitle(ConversationMode m) => m switch
    {
        ConversationMode.AguardandoContato => "Aguardando contato",
        ConversationMode.Ia => "IA atendendo",
        ConversationMode.AtendimentoHumano => "Atendimento humano",
        ConversationMode.Pausado => "Pausado",
        ConversationMode.Encerrado => "Encerrado",
        ConversationMode.Prospeccao => "Prospecção (aguardando resposta)",
        _ => m.ToString(),
    };

    /// <summary>
    /// Avalia uma movimentação. A IA nunca marca "Negócio fechado" — apenas sugere. "Negócio fechado" exige
    /// ação humana com confirmação comercial (ou integração de pagamento validada, via Sistema).
    /// </summary>
    public static MoveDecision Evaluate(Lead lead, KanbanStage to, MoveActor actor, bool commercialConfirmation)
    {
        if (lead.Stage == to) return new(false, false, "Lead já está nesta etapa");
        if (to == KanbanStage.NegocioFechado)
        {
            if (actor == MoveActor.Ia) return new(false, true, "IA só pode sugerir 'Negócio fechado'; exige confirmação comercial");
            if (!commercialConfirmation) return new(false, false, "Confirme a contratação (confirmação comercial ou pagamento validado)");
        }
        if (actor == MoveActor.Ia && lead.Mode == ConversationMode.AtendimentoHumano)
            return new(false, true, "Conversa em atendimento humano: mudança fica como sugestão");
        return new(true, false, null);
    }

    /// <summary>Pontuação mínima para o Sistema mover o card para "Qualificado".</summary>
    public const int QualifiedScore = 60;

    /// <summary>
    /// Movimentação automática do funil (sempre pelo Sistema, com registro em StageChanges). Só avança: nunca tira o card
    /// de uma etapa posterior nem de "Negócio fechado"/"Não tem interesse". Retorna a mudança aplicada ou null.
    /// </summary>
    public static StageChange? AutoAdvance(Lead lead, KanbanStage to, string reason)
    {
        if (lead.Stage == to || !AutoFrom(to).Contains(lead.Stage)) return null;
        return Apply(lead, to, "Sistema", reason);
    }

    private static KanbanStage[] AutoFrom(KanbanStage to) => to switch
    {
        KanbanStage.AbordagemRealizada => [KanbanStage.PrimeiroContato],
        KanbanStage.Respondeu => [KanbanStage.AbordagemRealizada],
        KanbanStage.EmAtendimento => [KanbanStage.PrimeiroContato, KanbanStage.AbordagemRealizada, KanbanStage.Respondeu],
        KanbanStage.Qualificado => [KanbanStage.PrimeiroContato, KanbanStage.AbordagemRealizada, KanbanStage.Respondeu, KanbanStage.EmAtendimento, KanbanStage.EmDuvidas],
        _ => [],
    };

    public static StageChange Apply(Lead lead, KanbanStage to, string changedBy, string? reason)
    {
        var change = new StageChange { LeadId = lead.Id, From = lead.Stage, To = to, ChangedBy = changedBy, Reason = reason };
        lead.Stage = to;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        lead.StageChanges.Add(change);
        return change;
    }
}
