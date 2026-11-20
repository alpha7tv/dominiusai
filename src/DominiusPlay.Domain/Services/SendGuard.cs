using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Domain.Services;

/// <summary>Contexto avaliado imediatamente antes de cada envio.</summary>
public sealed record SendContext(
    Lead Lead,
    OutboundKind Kind,
    DateTimeOffset Now,
    bool IsOptedOut,
    bool InstanceConnected,
    DateTimeOffset? LastInboundAt,
    DateTimeOffset? LastInitialSendOnInstanceAt,
    bool AlreadyContactedInCampaign,
    int SentTodayOnInstance,
    int DailyLimit,
    TimeSpan MinInitialInterval,
    Campaign? Campaign,
    bool TemplateApproved,
    int ConsecutiveFailures,
    TimeZoneInfo TimeZone);

public sealed record GuardResult(bool Allowed, string? Reason, DateTimeOffset? RetryAt = null)
{
    public static GuardResult Ok() => new(true, null);
    public static GuardResult Block(string reason) => new(false, reason);
    public static GuardResult Defer(string reason, DateTimeOffset at) => new(false, reason, at);
}

/// <summary>
/// Regras obrigatórias antes de qualquer envio (seção 7 do escopo). Retorna bloqueio definitivo
/// (item cancelado) ou adiamento (RetryAt) — nunca envia "por cima" de uma regra.
/// </summary>
public static class SendGuard
{
    /// <summary>Janela de atendimento do WhatsApp: 24h após a última mensagem do contato.</summary>
    public static readonly TimeSpan CustomerServiceWindow = TimeSpan.FromHours(24);
    public const int MaxConsecutiveFailures = 3;

    public static GuardResult Evaluate(SendContext c)
    {
        if (c.IsOptedOut) return GuardResult.Block("Contato está na lista de descadastro/bloqueio");
        var bypassMode = c.Kind is OutboundKind.Humana or OutboundKind.Transicao;
        if (c.Lead.Stage == KanbanStage.NaoTemInteresse && !bypassMode)
            return GuardResult.Block("Lead marcado como 'Não tem interesse'");
        if (c.Lead.Mode == ConversationMode.Encerrado && !bypassMode)
            return GuardResult.Block("Conversa encerrada");

        // Automação nunca fala por cima de um humano
        var automated = c.Kind is OutboundKind.AbordagemInicial or OutboundKind.Resposta or OutboundKind.Acompanhamento;
        if (automated && c.Lead.Mode == ConversationMode.AtendimentoHumano)
            return GuardResult.Block("Atendimento humano em andamento — IA bloqueada para esta conversa");
        if (automated && c.Lead.Mode == ConversationMode.Pausado)
            return GuardResult.Block("Conversa pausada");

        if (c.ConsecutiveFailures >= MaxConsecutiveFailures)
            return GuardResult.Block($"{c.ConsecutiveFailures} falhas consecutivas para este contato — revisão manual necessária");
        if (!c.InstanceConnected)
            return GuardResult.Defer("Instância do WhatsApp desconectada", c.Now.AddMinutes(2));

        var inWindow = c.LastInboundAt is { } li && c.Now - li <= CustomerServiceWindow;

        switch (c.Kind)
        {
            case OutboundKind.AbordagemInicial:
            case OutboundKind.Acompanhamento:
                if (c.Lead.ConsentStatus != ConsentStatus.Valido)
                    return GuardResult.Block("Sem consentimento válido e verificável para mensagens comerciais");
                if (c.Kind == OutboundKind.AbordagemInicial && c.AlreadyContactedInCampaign)
                    return GuardResult.Block("Contato inicial já realizado nesta campanha");
                if (!inWindow && !c.TemplateApproved)
                    return GuardResult.Block("Fora da janela de 24h: exige modelo de mensagem aprovado");
                if (c.Campaign is { } camp)
                {
                    if (camp.Status == CampaignStatus.Pausada) return GuardResult.Defer("Campanha pausada", c.Now.AddMinutes(5));
                    if (camp.Status != CampaignStatus.Ativa) return GuardResult.Block($"Campanha não está ativa ({camp.Status})");
                    var next = CampaignWindow.NextAllowed(camp, c.Now, c.TimeZone);
                    if (next > c.Now) return GuardResult.Defer("Fora do horário/dia permitido da campanha", next);
                }
                if (c.SentTodayOnInstance >= c.DailyLimit)
                    return GuardResult.Defer("Limite diário de envios da instância atingido", CampaignWindow.StartOfNextDay(c.Now, c.TimeZone));
                if (c.Kind == OutboundKind.AbordagemInicial && c.LastInitialSendOnInstanceAt is { } last && c.Now - last < c.MinInitialInterval)
                    return GuardResult.Defer("Intervalo mínimo entre abordagens iniciais", last + c.MinInitialInterval);
                return GuardResult.Ok();

            case OutboundKind.Resposta:
            case OutboundKind.Transicao:
                // Respostas não dependem do intervalo de campanha, mas só existem dentro da janela.
                if (!inWindow) return GuardResult.Block("Resposta fora da janela de 24h após a última mensagem do contato");
                return GuardResult.Ok();

            case OutboundKind.Humana:
                if (!inWindow && !c.TemplateApproved && c.Lead.ConsentStatus != ConsentStatus.Valido)
                    return GuardResult.Block("Fora da janela de 24h e sem consentimento: atendente deve usar modelo aprovado");
                return GuardResult.Ok();

            case OutboundKind.Teste:
                return GuardResult.Ok();
        }
        return GuardResult.Block("Tipo de envio desconhecido");
    }
}

public static class CampaignWindow
{
    public static HashSet<DayOfWeek> Days(Campaign c) =>
        c.AllowedDays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => (DayOfWeek)int.Parse(d)).ToHashSet();

    /// <summary>Retorna "now" se dentro da janela; senão o próximo início permitido.</summary>
    public static DateTimeOffset NextAllowed(Campaign c, DateTimeOffset now, TimeZoneInfo tz)
    {
        var days = Days(c);
        if (days.Count == 0) return DateTimeOffset.MaxValue;
        var local = TimeZoneInfo.ConvertTime(now, tz);
        for (var i = 0; i < 8; i++)
        {
            var date = DateOnly.FromDateTime(local.Date).AddDays(i);
            if (!days.Contains(date.DayOfWeek)) continue;
            var start = ToOffset(date, c.StartTime, tz);
            var end = ToOffset(date, c.EndTime, tz);
            if (now < start) return start;
            if (now >= start && now < end) return now;
        }
        return DateTimeOffset.MaxValue;
    }

    public static DateTimeOffset StartOfNextDay(DateTimeOffset now, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(now, tz);
        return ToOffset(DateOnly.FromDateTime(local.Date).AddDays(1), TimeOnly.MinValue, tz);
    }

    private static DateTimeOffset ToOffset(DateOnly d, TimeOnly t, TimeZoneInfo tz)
    {
        var dt = d.ToDateTime(t, DateTimeKind.Unspecified);
        return new DateTimeOffset(dt, tz.GetUtcOffset(dt));
    }
}
