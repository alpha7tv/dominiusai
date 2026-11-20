using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Evolution;
using DominiusPlay.Infrastructure.Import;
using DominiusPlay.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Services;

public enum InboundOutcome { Stored, Duplicate, Ignored, StatusUpdated, ConnectionUpdated, Failed }

/// <summary>
/// Processa webhooks da Evolution API: deduplica pelo ID real, grava mensagens, reconhece mensagens
/// enviadas pelo próprio sistema, detecta mensagens manuais do aparelho (pausa a IA), atualiza status de
/// entrega e detecta pedidos de descadastro.
/// </summary>
public class InboundProcessor(CrmDbContext db, ConversationLocks locks, ConversationService conversations,
    InstanceStatusCache status, IHubContext<CrmHub> hub, ILogger<InboundProcessor> log)
{
    public async Task<InboundOutcome> ProcessAsync(string json, CancellationToken ct = default)
    {
        ParsedWebhook p;
        try { p = EvolutionWebhookParser.Parse(json); }
        catch (Exception ex) { log.LogWarning(ex, "Webhook inválido"); return InboundOutcome.Failed; }

        // Deduplicação no nível do evento (índice único em EventKey)
        if (await db.WebhookEvents.AnyAsync(e => e.EventKey == p.DedupKey, ct)) return InboundOutcome.Duplicate;
        var ev = new WebhookEvent { EventKey = p.DedupKey, EventType = p.EventName, InstanceName = p.Instance, Payload = json.Length > 20000 ? json[..20000] : json };
        db.WebhookEvents.Add(ev);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return InboundOutcome.Duplicate; } // corrida entre réplicas do mesmo evento

        InboundOutcome outcome;
        try
        {
            outcome = p.Kind switch
            {
                WebhookKind.MessageUpsert or WebhookKind.SendMessage => await HandleMessageAsync(p, ct),
                WebhookKind.MessageStatusUpdate => await HandleStatusAsync(p, ct),
                WebhookKind.ConnectionUpdate => HandleConnection(p),
                _ => InboundOutcome.Ignored,
            };
            ev.Processed = true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Falha ao processar webhook {Key}", p.DedupKey);
            ev.Error = ex.Message;
            outcome = InboundOutcome.Failed;
        }
        db.WebhookEvents.Update(ev);
        await db.SaveChangesAsync(ct);
        return outcome;
    }

    private InboundOutcome HandleConnection(ParsedWebhook p)
    {
        status.State = p.ConnectionState;
        status.CheckedAt = DateTimeOffset.UtcNow;
        _ = hub.Clients.All.SendAsync(HubEvents.ConnectionChanged, p.ConnectionState);
        return InboundOutcome.ConnectionUpdated;
    }

    private async Task<InboundOutcome> HandleStatusAsync(ParsedWebhook p, CancellationToken ct)
    {
        var st = EvolutionWebhookParser.MapStatus(p.Status);
        if (st is null || string.IsNullOrEmpty(p.MessageId)) return InboundOutcome.Ignored;
        var msg = await db.Messages.FirstOrDefaultAsync(m => m.ExternalId == p.MessageId && m.InstanceName == p.Instance, ct);
        if (msg is null) return InboundOutcome.Ignored;
        // Status só avança (evita regressão por eventos fora de ordem)
        if (st == MessageStatus.Falhou || (int)st > (int)msg.Status) { msg.Status = st.Value; msg.UpdatedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync(HubEvents.MessageAdded, msg.LeadId, ct);
        return InboundOutcome.StatusUpdated;
    }

    private async Task<InboundOutcome> HandleMessageAsync(ParsedWebhook p, CancellationToken ct)
    {
        if (p.IsGroup || p.Phone is null || string.IsNullOrEmpty(p.MessageId)) return InboundOutcome.Ignored;
        var norm = PhoneNormalizer.Normalize("+" + p.Phone);
        var phone = norm.IsValid ? norm.Normalized : p.Phone;

        // Mensagem já registrada (ex.: enviada pelo próprio sistema e ecoada pelo webhook)
        if (await db.Messages.AnyAsync(m => m.ExternalId == p.MessageId && m.InstanceName == p.Instance, ct)) return InboundOutcome.Duplicate;

        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Phone == phone, ct);
        if (lead is null)
        {
            if (p.FromMe) return InboundOutcome.Ignored; // mensagem manual para número fora do CRM
            // Contato iniciou a conversa: cria lead. Isso NÃO equivale a consentimento para prospecção.
            lead = new Lead { Name = p.PushName ?? "", Phone = phone, Source = "WhatsApp (contato iniciou)", Mode = ConversationMode.AguardandoContato };
            db.Leads.Add(lead);
            await db.SaveChangesAsync(ct);
        }

        using var _ = await locks.AcquireAsync(lead.Id, ct);
        var ts = p.Timestamp ?? DateTimeOffset.UtcNow;
        var message = new Message
        {
            LeadId = lead.Id, ExternalId = p.MessageId, InstanceName = p.Instance, Text = p.Text ?? "", Timestamp = ts,
            Direction = p.FromMe ? MessageDirection.Saida : MessageDirection.Entrada,
            Author = p.FromMe ? MessageAuthor.ManualNoAparelho : MessageAuthor.Contato,
            Status = p.FromMe ? (EvolutionWebhookParser.MapStatus(p.Status) ?? MessageStatus.EnviadaServidor) : MessageStatus.Recebida,
        };
        db.Messages.Add(message);

        if (p.FromMe)
        {
            // fromMe sem registro no sistema = alguém respondeu pelo celular/WhatsApp Web → IA não pode falar junto
            await conversations.MarkManualActivityAsync(lead, ct);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(lead.Name) && !string.IsNullOrWhiteSpace(p.PushName)) lead.Name = p.PushName!;
            lead.LastMessagePreview = Preview(p.Text);
            if (OptOutDetector.IsOptOut(p.Text ?? ""))
            {
                LeadImportService.RevokeConsent(db, lead, "Pedido do contato via WhatsApp: \"" + Preview(p.Text) + "\"", "Sistema");
                if (!await db.OptOuts.AnyAsync(o => o.Phone == lead.Phone, ct))
                    db.OptOuts.Add(new OptOut { Phone = lead.Phone, Reason = "Pedido do contato", Source = "WhatsApp" });
                lead.Stage = KanbanStage.NaoTemInteresse;
                lead.Mode = ConversationMode.Encerrado;
                lead.StageChanges.Add(new StageChange { LeadId = lead.Id, To = KanbanStage.NaoTemInteresse, ChangedBy = "Sistema", Reason = "Pedido de descadastro" });
                db.AuditLogs.Add(new AuditLog { Actor = "Sistema", Action = "lead.optout", EntityType = "Lead", EntityId = lead.Id.ToString(), Details = Preview(p.Text) });
            }
            else if (lead.Mode == ConversationMode.AguardandoContato)
            {
                lead.Mode = ConversationMode.Ia;
            }
            // Classificação inicial de dores (inferência — a IA da Fase 5 refina)
            foreach (var pain in PainClassifier.Classify(p.Text ?? ""))
            {
                if (lead.MainPain == PainCategory.Nenhuma) lead.MainPain = pain;
                else if (pain != lead.MainPain && !lead.SecondaryPains.Split(',').Contains(pain.ToString()))
                    lead.SecondaryPains = string.Join(",", new[] { lead.SecondaryPains, pain.ToString() }.Where(s => s.Length > 0));
            }
        }
        lead.LastInteractionAt = ts;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        lead.ConcurrencyStamp = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync(HubEvents.MessageAdded, lead.Id, ct);
        await hub.Clients.All.SendAsync(HubEvents.LeadChanged, lead.Id, ct);
        return InboundOutcome.Stored;
    }

    private static string Preview(string? t) => t is null ? "" : t.Length > 120 ? t[..120] + "…" : t;
}
