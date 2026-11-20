using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Services;

/// <summary>Transferência IA ⇄ humano, envio manual e pausa (seção 14).</summary>
public class ConversationService(CrmDbContext db, ConversationLocks locks, IHubContext<CrmHub> hub)
{
    public static readonly OutboundKind[] AutomatedKinds = [OutboundKind.AbordagemInicial, OutboundKind.Resposta, OutboundKind.Acompanhamento];

    /// <summary>Humano assume: IA interrompida, envios automáticos pendentes cancelados, responsável registrado.</summary>
    public async Task<OperationResult> TakeOverAsync(Guid leadId, User user, string reason = "Assumido pelo atendente", CancellationToken ct = default)
    {
        using var _ = await locks.AcquireAsync(leadId, ct);
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return new(false, "Lead não encontrado");
        if (lead.Mode == ConversationMode.AtendimentoHumano && lead.AssignedUserId is { } other && other != user.Id)
        {
            var name = await db.Users.Where(u => u.Id == other).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
            return new(false, $"Conversa já está com {name}");
        }
        var cancelled = await CancelAutomatedAsync(leadId, reason, ct);
        lead.Mode = ConversationMode.AtendimentoHumano;
        lead.AssignedUserId = user.Id;
        lead.ConcurrencyStamp = Guid.NewGuid();
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        db.Messages.Add(new Message { LeadId = leadId, Direction = MessageDirection.Saida, Author = MessageAuthor.Sistema, Status = MessageStatus.Recebida,
            Text = $"{user.DisplayName} assumiu o atendimento. IA pausada nesta conversa." });
        db.AuditLogs.Add(new AuditLog { Actor = user.UserName, Action = "conversation.takeover", EntityType = "Lead", EntityId = leadId.ToString(), Details = $"{cancelled} envio(s) automático(s) cancelado(s)" });
        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync(HubEvents.LeadChanged, leadId, ct);
        return new(true, cancelled > 0 ? $"Atendimento assumido. {cancelled} envio(s) automático(s) cancelado(s)." : "Atendimento assumido.");
    }

    /// <summary>Marca conversa como humana por detecção de mensagem manual enviada pelo aparelho.</summary>
    public async Task MarkManualActivityAsync(Lead lead, CancellationToken ct)
    {
        if (lead.Mode == ConversationMode.AtendimentoHumano) return;
        var cancelled = await CancelAutomatedAsync(lead.Id, "Mensagem manual detectada no aparelho", ct);
        lead.Mode = ConversationMode.AtendimentoHumano;
        lead.ConcurrencyStamp = Guid.NewGuid();
        db.AuditLogs.Add(new AuditLog { Actor = "Sistema", Action = "conversation.manual-detected", EntityType = "Lead", EntityId = lead.Id.ToString(), Details = $"{cancelled} envio(s) automático(s) cancelado(s)" });
    }

    public async Task<OperationResult> ReturnToAiAsync(Guid leadId, User user, CancellationToken ct = default)
    {
        using var _ = await locks.AcquireAsync(leadId, ct);
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return new(false, "Lead não encontrado");
        if (lead.Mode != ConversationMode.AtendimentoHumano && lead.Mode != ConversationMode.Pausado) return new(false, "Conversa não está em atendimento humano nem pausada");
        if (lead.AssignedUserId is { } owner && owner != user.Id && user.Role == UserRole.Atendente)
            return new(false, "Somente o atendente responsável ou um supervisor pode devolver esta conversa para a IA");
        lead.Mode = ConversationMode.Ia;
        lead.ConcurrencyStamp = Guid.NewGuid();
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        db.Messages.Add(new Message { LeadId = leadId, Direction = MessageDirection.Saida, Author = MessageAuthor.Sistema, Status = MessageStatus.Recebida,
            Text = $"{user.DisplayName} devolveu a conversa para a IA." });
        db.AuditLogs.Add(new AuditLog { Actor = user.UserName, Action = "conversation.return-to-ai", EntityType = "Lead", EntityId = leadId.ToString() });
        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync(HubEvents.LeadChanged, leadId, ct);
        return new(true, "Conversa devolvida para a IA.");
    }

    public async Task<OperationResult> PauseAsync(Guid leadId, User user, bool pause, CancellationToken ct = default)
    {
        using var _ = await locks.AcquireAsync(leadId, ct);
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return new(false, "Lead não encontrado");
        if (pause)
        {
            var cancelled = await CancelAutomatedAsync(leadId, "Conversa pausada", ct);
            lead.Mode = ConversationMode.Pausado;
            db.AuditLogs.Add(new AuditLog { Actor = user.UserName, Action = "conversation.pause", EntityType = "Lead", EntityId = leadId.ToString(), Details = $"{cancelled} cancelado(s)" });
        }
        else
        {
            if (lead.Mode != ConversationMode.Pausado) return new(false, "Conversa não está pausada");
            lead.Mode = ConversationMode.AguardandoContato;
            db.AuditLogs.Add(new AuditLog { Actor = user.UserName, Action = "conversation.resume", EntityType = "Lead", EntityId = leadId.ToString() });
        }
        lead.ConcurrencyStamp = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync(HubEvents.LeadChanged, leadId, ct);
        return new(true, pause ? "Conversa pausada." : "Conversa retomada.");
    }

    /// <summary>Atendente envia mensagem: entra na fila como prioridade máxima (tipo Humana); o envio respeita as regras do canal.</summary>
    public async Task<SendMessageResponse> EnqueueHumanMessageAsync(Guid leadId, User user, string text, string instance, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(false, "Mensagem vazia", null);
        using var _ = await locks.AcquireAsync(leadId, ct);
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return new(false, "Lead não encontrado", null);
        if (lead.Mode != ConversationMode.AtendimentoHumano)
            return new(false, "Clique em 'Assumir atendimento' antes de responder — isso pausa a IA nesta conversa.", null);
        if (lead.AssignedUserId != user.Id && user.Role == UserRole.Atendente)
            return new(false, "Conversa atribuída a outro atendente.", null);
        var item = new OutboundQueueItem { LeadId = leadId, Kind = OutboundKind.Humana, Text = text.Trim(), InstanceName = instance, RequestedByUserId = user.Id };
        db.OutboundQueue.Add(item);
        await db.SaveChangesAsync(ct);
        return new(true, null, item.Id);
    }

    private async Task<int> CancelAutomatedAsync(Guid leadId, string reason, CancellationToken ct)
    {
        var pending = await db.OutboundQueue
            .Where(q => q.LeadId == leadId && (q.Status == MessageStatus.NaFila) && AutomatedKinds.Contains(q.Kind)).ToListAsync(ct);
        foreach (var q in pending) { q.Status = MessageStatus.Cancelada; q.LastError = reason; q.UpdatedAt = DateTimeOffset.UtcNow; }
        return pending.Count;
    }
}
