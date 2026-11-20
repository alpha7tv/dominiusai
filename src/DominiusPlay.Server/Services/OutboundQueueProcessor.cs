using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Evolution;
using DominiusPlay.Infrastructure.Security;
using DominiusPlay.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Services;

/// <summary>
/// Processa a fila persistente. Ordem de prioridade: mensagens humanas → respostas → acompanhamentos → abordagens iniciais.
/// Cada item é reavaliado pelo SendGuard imediatamente antes do envio, dentro do bloqueio da conversa.
/// </summary>
public class OutboundQueueProcessor(CrmDbContext db, IEvolutionApi evolution, EvolutionGateway gateway, SettingsStore settings,
    ConversationLocks locks, InstanceStatusCache status, IHubContext<CrmHub> hub, ApproachGenerator approaches, TimeProvider clock, ILogger<OutboundQueueProcessor> log)
{
    public const int MaxAttempts = 4;

    /// <summary>Processa até <paramref name="max"/> itens prontos. Retorna quantos foram efetivamente enviados.</summary>
    public async Task<int> ProcessDueAsync(int max = 10, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var cfg = await gateway.GetConfigAsync(ct);
        if (cfg is null) return 0;

        var due = await db.OutboundQueue
            .Where(q => q.Status == MessageStatus.NaFila)
            .ToListAsync(ct); // filtro de data em memória: compatível com SQLite e PostgreSQL
        var batch = due.Where(q => q.NotBefore <= now)
            .OrderBy(q => q.Kind switch { OutboundKind.Humana => 0, OutboundKind.Teste => 0, OutboundKind.Resposta => 1, OutboundKind.Transicao => 1, OutboundKind.Acompanhamento => 2, _ => 3 })
            .ThenBy(q => q.NotBefore).Take(max).Select(q => q.Id).ToList();

        var sent = 0;
        foreach (var id in batch)
        {
            if (ct.IsCancellationRequested) break;
            if (await ProcessOneAsync(id, cfg, ct)) sent++;
        }
        await CompleteCampaignsAsync(ct); // também quando a fila esvaziou por cancelamento (ex.: contato respondeu)
        return sent;
    }

    /// <summary>Campanhas ativas sem itens pendentes passam a "Concluída".</summary>
    private async Task CompleteCampaignsAsync(CancellationToken ct)
    {
        var active = await db.Campaigns.Where(c => c.Status == CampaignStatus.Ativa).ToListAsync(ct);
        foreach (var c in active)
            if (!await db.OutboundQueue.AnyAsync(q => q.CampaignId == c.Id && (q.Status == MessageStatus.NaFila || q.Status == MessageStatus.Enviando), ct))
            { c.Status = CampaignStatus.Concluida; c.UpdatedAt = clock.GetUtcNow(); }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> ProcessOneAsync(Guid itemId, EvolutionConfig globalCfg, CancellationToken ct)
    {
        var sentOk = await ProcessOneCoreAsync(itemId, globalCfg, ct);
        var campaignId = await db.OutboundQueue.AsNoTracking().Where(q => q.Id == itemId).Select(q => q.CampaignId).FirstOrDefaultAsync(ct);
        if (campaignId is { } cid) await CheckAutoPauseAsync(cid, ct);
        return sentOk;
    }

    /// <summary>
    /// Condições de pausa da campanha (a partir de 10 envios): taxa de falhas acima do limite ou pico de descadastros.
    /// A campanha pausada não envia mais nada até alguém revisar e retomar.
    /// </summary>
    public async Task CheckAutoPauseAsync(Guid campaignId, CancellationToken ct)
    {
        var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == campaignId, ct);
        if (c is null || c.Status != CampaignStatus.Ativa) return;
        var items = await db.OutboundQueue.AsNoTracking().Where(q => q.CampaignId == campaignId && (q.Status == MessageStatus.EnviadaServidor || q.Status == MessageStatus.Falhou))
            .Select(q => new { q.Status, q.LeadId }).ToListAsync(ct);
        var sent = items.Count(i => i.Status == MessageStatus.EnviadaServidor);
        var failed = items.Count(i => i.Status == MessageStatus.Falhou);
        string? reason = null;
        if (sent + failed >= 10 && failed * 100 > c.MaxFailurePercent * (sent + failed))
            reason = $"Pausa automática: {failed} de {sent + failed} envios falharam (limite {c.MaxFailurePercent}%). Verifique a conexão do WhatsApp e a lista.";
        else if (sent >= 10)
        {
            var leadIds = items.Where(i => i.Status == MessageStatus.EnviadaServidor).Select(i => i.LeadId).Distinct().ToList();
            var phones = await db.Leads.AsNoTracking().Where(l => leadIds.Contains(l.Id)).Select(l => l.Phone).ToListAsync(ct);
            var optOuts = await db.OptOuts.AsNoTracking().Where(o => phones.Contains(o.Phone)).CountAsync(ct);
            if (optOuts * 100 > c.MaxOptOutPercent * leadIds.Count)
                reason = $"Pausa automática: {optOuts} de {leadIds.Count} contatos pediram descadastro (limite {c.MaxOptOutPercent}%). Revise a lista e a abordagem.";
        }
        if (reason is null) return;
        c.Status = CampaignStatus.Pausada; c.PauseReason = reason; c.UpdatedAt = clock.GetUtcNow();
        db.AuditLogs.Add(new AuditLog { Actor = "Sistema", Action = "campaign.autopause", EntityType = "Campaign", EntityId = c.Id.ToString(), Details = reason });
        await db.SaveChangesAsync(ct);
        log.LogWarning("Campanha {Campaign} pausada: {Reason}", c.Name, reason);
    }

    private async Task<bool> ProcessOneCoreAsync(Guid itemId, EvolutionConfig globalCfg, CancellationToken ct)
    {
        var item = await db.OutboundQueue.Include(q => q.Lead).FirstOrDefaultAsync(q => q.Id == itemId, ct);
        if (item?.Lead is null || item.Status != MessageStatus.NaFila) return false;
        using var _ = await locks.AcquireAsync(item.LeadId, ct);
        await db.Entry(item).ReloadAsync(ct);
        await db.Entry(item.Lead).ReloadAsync(ct); // estado mais recente (ex.: humano assumiu há instantes)
        if (item.Status != MessageStatus.NaFila) return false;
        // cada agente de disparo pode usar a própria instância (mesmo servidor Evolution)
        var cfg = !string.IsNullOrWhiteSpace(item.InstanceName) && item.InstanceName != globalCfg.InstanceName ? globalCfg with { InstanceName = item.InstanceName } : globalCfg;

        var now = clock.GetUtcNow();
        if (item.ExpiresAt is { } exp && exp < now) { Finish(item, MessageStatus.Cancelada, "Item expirado", now); await db.SaveChangesAsync(ct); return false; }

        var ctx = await BuildContextAsync(item, cfg, now, ct);
        var decision = SendGuard.Evaluate(ctx);
        if (!decision.Allowed)
        {
            if (decision.RetryAt is { } retry) { item.NotBefore = retry; item.LastError = decision.Reason; }
            else Finish(item, MessageStatus.Cancelada, decision.Reason, now);
            await db.SaveChangesAsync(ct);
            return false;
        }

        if (item.Personalize && item.Kind == OutboundKind.AbordagemInicial && ctx.Campaign is { } camp)
        {
            var dispatcher = camp.DispatchAgentId is { } did ? await db.AgentProfiles.AsNoTracking().FirstOrDefaultAsync(a => a.Id == did, ct) : null;
            try
            {
                var gen = await approaches.GenerateAsync(item.Lead, item.Text, camp, dispatcher, ct);
                item.Text = gen.Text; item.PersonalizationNote = gen.Note;
                if (gen.MaxSimilarity >= ApproachGenerator.SimilarityAlert && gen.Personalized)
                    db.AuditLogs.Add(new AuditLog { Actor = "IA", Action = "campaign.similarity-alert", EntityType = "Campaign", EntityId = camp.Id.ToString(), Details = gen.Note });
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { item.PersonalizationNote = "IA indisponível: enviado o modelo aprovado"; log.LogWarning(ex, "Falha ao personalizar {Item}", item.Id); }
            item.Personalize = false; // personaliza uma vez só (retentativas reutilizam o mesmo texto)
        }

        item.Status = MessageStatus.Enviando;
        item.Attempts++;
        await db.SaveChangesAsync(ct);

        var result = await evolution.SendTextAsync(cfg, item.Lead.Phone, item.Text, ct);
        if (result.Success)
        {
            var msg = new Message
            {
                LeadId = item.LeadId, Direction = MessageDirection.Saida, Text = item.Text, ExternalId = result.MessageId, InstanceName = cfg.InstanceName,
                Author = AuthorOf(item.Kind),
                Status = EvolutionWebhookParser.MapStatus(result.Status) ?? MessageStatus.Pendente, SentByUserId = item.RequestedByUserId,
                CampaignId = item.CampaignId, TemplateVersionId = item.TemplateVersionId, Timestamp = now,
            };
            db.Messages.Add(msg);
            item.MessageId = msg.Id;
            if (item.TemplateVersionId is { } tvId && await db.MessageTemplateVersions.FindAsync([tvId], ct) is { } tv) tv.TimesUsed++;
            Finish(item, MessageStatus.EnviadaServidor, null, now);
            item.Lead.LastInteractionAt = now;
            item.Lead.LastMessagePreview = item.Text.Length > 120 ? item.Text[..120] + "…" : item.Text;
            if (item.Lead.Mode == ConversationMode.AguardandoContato && item.Kind == OutboundKind.AbordagemInicial) item.Lead.Mode = ConversationMode.Ia;
            if (item.Kind == OutboundKind.AbordagemInicial && item.CampaignId is { } origin) item.Lead.OriginCampaignId ??= origin;
            await db.SaveChangesAsync(ct);
            await hub.Clients.All.SendAsync(HubEvents.MessageAdded, item.LeadId, ct);
            return true;
        }

        // Falha: erros 4xx (exceto 429) não são retentados — número inexistente, payload inválido etc.
        var retryable = result.HttpStatus is 0 or 429 or >= 500;
        log.LogWarning("Falha no envio {Item}: {Error}", item.Id, result.Error);
        if (retryable && item.Attempts < MaxAttempts)
        {
            item.Status = MessageStatus.NaFila;
            item.NotBefore = now.AddSeconds(30 * Math.Pow(2, item.Attempts)); // 60s, 120s, 240s
            item.LastError = result.Error;
        }
        else
        {
            Finish(item, MessageStatus.Falhou, result.Error, now);
            // Falha fica como nota do Sistema (não entra no histórico da IA), mas guarda a campanha para o relatório de mensagens enviadas
            db.Messages.Add(new Message { LeadId = item.LeadId, Direction = MessageDirection.Saida, Author = MessageAuthor.Sistema, Text = item.Text,
                Status = MessageStatus.Falhou, Error = result.Error, InstanceName = cfg.InstanceName, Timestamp = now, SentByUserId = item.RequestedByUserId,
                CampaignId = item.CampaignId, TemplateVersionId = item.TemplateVersionId });
        }
        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync(HubEvents.MessageAdded, item.LeadId, ct);
        return false;
    }

    private static MessageAuthor AuthorOf(OutboundKind kind) =>
        kind switch { OutboundKind.Humana => MessageAuthor.Atendente, OutboundKind.Teste => MessageAuthor.Sistema, _ => MessageAuthor.Ia };

    private async Task<SendContext> BuildContextAsync(OutboundQueueItem item, EvolutionConfig cfg, DateTimeOffset now, CancellationToken ct)
    {
        var lead = item.Lead!;
        var leadMsgs = await db.Messages.Where(m => m.LeadId == lead.Id).Select(m => new { m.Direction, m.Timestamp, m.Status, m.Author, m.CampaignId }).ToListAsync(ct);
        var lastInbound = leadMsgs.Where(m => m.Direction == MessageDirection.Entrada).Select(m => (DateTimeOffset?)m.Timestamp).Max();
        var failures = 0;
        foreach (var m in leadMsgs.Where(m => m.Direction == MessageDirection.Saida && m.Author != MessageAuthor.Sistema || m.Status == MessageStatus.Falhou).OrderByDescending(m => m.Timestamp))
        { if (m.Status == MessageStatus.Falhou) failures++; else break; }

        var tz = ResolveTz(await settings.GetAsync(SettingKeys.TimeZone, ct));
        var localToday = TimeZoneInfo.ConvertTime(now, tz).Date;
        var dayStart = new DateTimeOffset(localToday, tz.GetUtcOffset(localToday));

        var initialSends = await db.OutboundQueue
            .Where(q => q.InstanceName == cfg.InstanceName && q.Kind == OutboundKind.AbordagemInicial && q.Status == MessageStatus.EnviadaServidor)
            .Select(q => q.UpdatedAt).ToListAsync(ct);
        var automatedToday = await db.OutboundQueue
            .Where(q => q.InstanceName == cfg.InstanceName && q.Status == MessageStatus.EnviadaServidor
                        && (q.Kind == OutboundKind.AbordagemInicial || q.Kind == OutboundKind.Acompanhamento))
            .Select(q => q.UpdatedAt).ToListAsync(ct);

        Campaign? campaign = item.CampaignId is { } cid ? await db.Campaigns.FindAsync([cid], ct) : null;
        var templateApproved = false;
        if (item.TemplateVersionId is { } tv)
            templateApproved = await db.MessageTemplateVersions.AnyAsync(v => v.Id == tv && v.Approval == ApprovalStatus.Aprovado, ct);
        var alreadyInCampaign = item.CampaignId is not null && leadMsgs.Any(m => m.CampaignId == item.CampaignId && m.Status != MessageStatus.Falhou);

        var minInterval = int.TryParse(await settings.GetAsync(SettingKeys.MinInitialIntervalSeconds, ct), out var s) ? s : 300;
        if (campaign is not null) minInterval = Math.Max(minInterval, campaign.MinIntervalSeconds);
        var dailyLimit = int.TryParse(await settings.GetAsync(SettingKeys.DailyLimit, ct), out var dl) ? dl : 50;
        if (campaign is not null) dailyLimit = Math.Min(dailyLimit, campaign.DailyLimit);
        if (campaign?.DispatchAgentId is { } agentId && await db.AgentProfiles.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct) is { } dispatcher)
        {
            dailyLimit = Math.Min(dailyLimit, dispatcher.DailyLimit);
            minInterval = Math.Max(minInterval, dispatcher.MinIntervalSeconds);
        }

        // acompanhamento: depende da abordagem inicial desta campanha e para se o contato respondeu
        DateTimeOffset? initialAt = null; var initialPending = false;
        if (item.Kind == OutboundKind.Acompanhamento && item.CampaignId is { } fcid)
        {
            initialAt = await db.Messages.AsNoTracking().Where(m => m.LeadId == lead.Id && m.CampaignId == fcid && m.Direction == MessageDirection.Saida
                    && m.Author != MessageAuthor.Sistema && m.Status != MessageStatus.Falhou)
                .Select(m => m.Timestamp).ToListAsync(ct) is { Count: > 0 } ts ? ts.Min() : (DateTimeOffset?)null;
            initialPending = await db.OutboundQueue.AnyAsync(q => q.LeadId == lead.Id && q.CampaignId == fcid && q.Kind == OutboundKind.AbordagemInicial
                && (q.Status == MessageStatus.NaFila || q.Status == MessageStatus.Enviando), ct);
        }

        return new SendContext(lead, item.Kind, now,
            IsOptedOut: await db.OptOuts.AnyAsync(o => o.Phone == lead.Phone, ct),
            InstanceConnected: status.State is null || status.IsOpen, // desconhecido → tenta; a API rejeita se desconectada
            LastInboundAt: lastInbound,
            LastInitialSendOnInstanceAt: initialSends.Count == 0 ? null : initialSends.Max(),
            AlreadyContactedInCampaign: alreadyInCampaign,
            SentTodayOnInstance: automatedToday.Count(t => t >= dayStart),
            DailyLimit: dailyLimit,
            MinInitialInterval: TimeSpan.FromSeconds(minInterval),
            Campaign: campaign, TemplateApproved: templateApproved, ConsecutiveFailures: failures, TimeZone: tz,
            InitialSentInCampaignAt: initialAt, InitialPendingInCampaign: initialPending);
    }

    public static TimeZoneInfo ResolveTz(string? id)
    {
        foreach (var candidate in new[] { id, "America/Sao_Paulo", "E. South America Standard Time" })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); } catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.Utc;
    }

    private static void Finish(OutboundQueueItem item, MessageStatus st, string? err, DateTimeOffset now)
    { item.Status = st; item.LastError = err; item.UpdatedAt = now; }
}

/// <summary>Serviço em segundo plano: roda independente da janela do aplicativo.</summary>
public class OutboundQueueWorker(IServiceScopeFactory scopes, ILogger<OutboundQueueWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Após reinicialização, itens que ficaram "Enviando" voltam para a fila e são reavaliados.
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
            var stuck = await db.OutboundQueue.Where(q => q.Status == MessageStatus.Enviando).ToListAsync(stoppingToken);
            foreach (var q in stuck) { q.Status = MessageStatus.NaFila; q.LastError = "Recuperado após reinicialização"; }
            if (stuck.Count > 0) { await db.SaveChangesAsync(stoppingToken); log.LogWarning("{N} item(ns) recuperado(s) após reinicialização", stuck.Count); }
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<OutboundQueueProcessor>().ProcessDueAsync(10, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Erro no processamento da fila"); }
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ContinueWith(_ => { });
        }
    }
}

/// <summary>Monitora o estado da instância da Evolution API a cada 60 segundos.</summary>
public class InstanceMonitorWorker(IServiceScopeFactory scopes, InstanceStatusCache cache, IHubContext<CrmHub> hub) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var cfg = await scope.ServiceProvider.GetRequiredService<EvolutionGateway>().GetConfigAsync(stoppingToken);
                if (cfg is not null)
                {
                    var st = await scope.ServiceProvider.GetRequiredService<IEvolutionApi>().GetConnectionStateAsync(cfg, stoppingToken);
                    var previous = cache.State;
                    cache.State = st.Reachable ? st.State : "unreachable";
                    cache.Error = st.Error; cache.CheckedAt = DateTimeOffset.UtcNow;
                    if (previous != cache.State) await hub.Clients.All.SendAsync(HubEvents.ConnectionChanged, cache.State, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { cache.Error = ex.Message; }
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ContinueWith(_ => { });
        }
    }
}
