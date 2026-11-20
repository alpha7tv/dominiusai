using System.Collections.Concurrent;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Evolution;
using DominiusPlay.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Services;

/// <summary>
/// Agenda respostas da IA com uma pequena espera (debounce) para agrupar mensagens seguidas do cliente
/// e evitar várias respostas consecutivas.
/// </summary>
public class AiDispatcher
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _due = new();
    public void Schedule(Guid leadId, DateTimeOffset at) => _due[leadId] = at;
    public IReadOnlyList<Guid> TakeDue(DateTimeOffset now)
    {
        var list = _due.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
        foreach (var id in list) _due.TryRemove(id, out _);
        return list;
    }
    public int Pending => _due.Count;
}

public enum AiReplyOutcome { Replied, Skipped, Disabled, Failed, Superseded }

/// <summary>Executa o atendimento real: gera a resposta, aplica as ações e coloca a mensagem na fila (tipo Resposta).</summary>
public class AiResponder(CrmDbContext db, AiAgent agent, ConversationLocks locks, EvolutionGateway gateway, IEvolutionApi evolution,
    IHubContext<CrmHub> hub, TimeProvider clock, ILogger<AiResponder> log)
{
    public async Task<AiReplyOutcome> RespondAsync(Guid leadId, CancellationToken ct = default)
    {
        var cfg = await agent.LoadConfigAsync(ct);
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.ApiKey)) return AiReplyOutcome.Disabled;

        var lead = await db.Leads.Include(l => l.Facts).FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null || lead.Mode != ConversationMode.Ia) { log.LogInformation("IA ignorou {Lead}: modo {Mode}", leadId, lead?.Mode); return AiReplyOutcome.Skipped; }
        var history = await db.Messages.AsNoTracking().Where(m => m.LeadId == leadId).ToListAsync(ct);
        var ordered = history.Where(m => m.Author != MessageAuthor.Sistema).OrderBy(m => m.CreatedAt).ToList(); // ordem de chegada
        var last = ordered.LastOrDefault();
        if (last is null || last.Direction != MessageDirection.Entrada) { log.LogInformation("IA ignorou {Lead}: última mensagem não é do cliente", leadId); return AiReplyOutcome.Skipped; }
        if (clock.GetUtcNow() - last.Timestamp > Domain.Services.SendGuard.CustomerServiceWindow) { log.LogInformation("IA ignorou {Lead}: fora da janela", leadId); return AiReplyOutcome.Skipped; }
        // já existe resposta pendente na fila?
        if (await db.OutboundQueue.AnyAsync(q => q.LeadId == leadId && q.Kind == OutboundKind.Resposta && q.Status == MessageStatus.NaFila, ct)) return AiReplyOutcome.Skipped;

        var evo = await gateway.GetConfigAsync(ct);
        if (cfg.Typing && evo is not null) _ = evolution.SendPresenceAsync(evo, lead.Phone, "composing", 4000, CancellationToken.None);

        var result = await agent.RunAsync(lead, history, cfg, trackUsage: true, ct);

        using var convLock = await locks.AcquireAsync(leadId, ct);
        // Estado mudou enquanto a IA pensava? (humano assumiu, nova mensagem, pausa)
        var current = await db.Leads.AsNoTracking().Where(l => l.Id == leadId).Select(l => new { l.Mode, l.ConcurrencyStamp }).FirstAsync(ct);
        var newer = await db.Messages.AsNoTracking().AnyAsync(m => m.LeadId == leadId && m.Direction == MessageDirection.Entrada && m.Id != last.Id && !history.Select(h => h.Id).Contains(m.Id), ct);
        if (current.Mode != ConversationMode.Ia || newer)
        {
            log.LogInformation("IA descartou resposta {Lead}: modo {Mode}, nova mensagem {Newer}", leadId, current.Mode, newer);
            db.ChangeTracker.Clear();
            return AiReplyOutcome.Superseded;
        }

        if (result.Error is not null && result.Reply is null)
        {
            log.LogWarning("IA não respondeu {Lead}: {Error}", leadId, result.Error);
            db.ChangeTracker.Clear();
            var l = await db.Leads.FirstAsync(x => x.Id == leadId, ct);
            l.Mode = ConversationMode.AtendimentoHumano; l.Priority = Priority.Alta; l.NextAction = "IA indisponível: " + result.Error;
            l.ConcurrencyStamp = Guid.NewGuid();
            db.Messages.Add(new Message { LeadId = leadId, Direction = MessageDirection.Saida, Author = MessageAuthor.Sistema, Status = MessageStatus.Recebida,
                Text = "IA não conseguiu responder (" + result.Error + "). Conversa encaminhada para atendimento humano.", Timestamp = clock.GetUtcNow() });
            db.AuditLogs.Add(new AuditLog { Actor = "IA", Action = "ai.error", EntityType = "Lead", EntityId = leadId.ToString(), Details = result.Error });
            await db.SaveChangesAsync(ct);
            await hub.Clients.All.SendAsync(HubEvents.LeadChanged, leadId, ct);
            return AiReplyOutcome.Failed;
        }

        if (result.Reply is { } reply && evo is not null)
            db.OutboundQueue.Add(new OutboundQueueItem { LeadId = leadId,
                // ao transferir/encerrar, a mensagem de aviso sai mesmo com a IA já pausada
                Kind = result.Transferred || result.Closed ? OutboundKind.Transicao : OutboundKind.Resposta, Text = reply, InstanceName = evo.InstanceName, NotBefore = clock.GetUtcNow() });
        lead.UpdatedAt = clock.GetUtcNow();
        lead.ConcurrencyStamp = Guid.NewGuid();
        if (result.Actions.Count > 0)
            db.AuditLogs.Add(new AuditLog { Actor = "IA", Action = "ai.actions", EntityType = "Lead", EntityId = leadId.ToString(), Details = string.Join(" | ", result.Actions) });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { log.LogInformation("IA descartou resposta {Lead}: conflito de concorrência", leadId); db.ChangeTracker.Clear(); return AiReplyOutcome.Superseded; }
        await hub.Clients.All.SendAsync(HubEvents.LeadChanged, leadId, ct);
        return result.Reply is null ? AiReplyOutcome.Failed : AiReplyOutcome.Replied;
    }
}

public class AiWorker(IServiceScopeFactory scopes, AiDispatcher dispatcher, TimeProvider clock, ILogger<AiWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Recuperação: conversas com IA cuja última mensagem é do cliente (últimas 24h) voltam para a agenda.
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
            var since = clock.GetUtcNow().AddHours(-24);
            var leads = await db.Leads.AsNoTracking().Where(l => l.Mode == ConversationMode.Ia).Select(l => l.Id).ToListAsync(stoppingToken);
            foreach (var id in leads)
            {
                var last = await db.Messages.AsNoTracking().Where(m => m.LeadId == id && m.Author != MessageAuthor.Sistema)
                    .OrderByDescending(m => m.CreatedAt).Select(m => new { m.Direction, m.Timestamp }).FirstOrDefaultAsync(stoppingToken);
                if (last?.Direction == MessageDirection.Entrada && last.Timestamp > since) dispatcher.Schedule(id, clock.GetUtcNow());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Falha na recuperação da IA"); }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var id in dispatcher.TakeDue(clock.GetUtcNow()))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<AiResponder>().RespondAsync(id, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Erro ao gerar resposta da IA para {Lead}", id); }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ContinueWith(_ => { });
        }
    }
}
