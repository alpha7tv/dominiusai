using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Services;

/// <summary>
/// Campanhas de abordagem inicial (seção 9). A campanha apenas coloca itens na fila persistente;
/// cada envio continua passando pelo SendGuard (consentimento, horário, limite diário, intervalo de 5 min etc.).
/// </summary>
public class CampaignService(CrmDbContext db, SettingsStore settings, EvolutionGateway gateway, TimeProvider clock)
{
    public static readonly ConsentStatus RequiredConsent = ConsentStatus.Valido;

    /// <summary>Leads que podem receber a abordagem inicial desta campanha agora.</summary>
    public async Task<List<Lead>> EligibleLeadsAsync(Campaign c, CancellationToken ct = default)
    {
        var optOuts = (await db.OptOuts.Select(o => o.Phone).ToListAsync(ct)).ToHashSet();
        var contacted = (await db.Messages.Where(m => m.Direction == MessageDirection.Saida && m.Author != MessageAuthor.Sistema)
            .Select(m => m.LeadId).Distinct().ToListAsync(ct)).ToHashSet();
        var inbound = (await db.Messages.Where(m => m.Direction == MessageDirection.Entrada).Select(m => m.LeadId).Distinct().ToListAsync(ct)).ToHashSet();
        var queued = (await db.OutboundQueue.Where(q => q.Status == MessageStatus.NaFila || q.Status == MessageStatus.Enviando)
            .Select(q => q.LeadId).Distinct().ToListAsync(ct)).ToHashSet();
        var tags = c.TagFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()).ToList();

        var leads = await db.Leads.Where(l => l.ConsentStatus == RequiredConsent && l.Stage == KanbanStage.PrimeiroContato
            && l.Mode == ConversationMode.AguardandoContato).ToListAsync(ct);
        return leads.Where(l => !optOuts.Contains(l.Phone) && !contacted.Contains(l.Id) && !inbound.Contains(l.Id) && !queued.Contains(l.Id))
            .Where(l => tags.Count == 0 || l.Tags.Split(',', StringSplitOptions.TrimEntries).Any(t => tags.Contains(t.ToLowerInvariant())))
            .OrderByDescending(l => l.Priority).ThenBy(l => l.ConsentAt ?? l.CreatedAt).ToList();
    }

    public async Task<List<MessageTemplateVersion>> ApprovedVersionsAsync(Campaign c, CancellationToken ct = default) =>
        c.TemplateId is { } tid
            ? await db.MessageTemplateVersions.Where(v => v.TemplateId == tid && v.Approval == ApprovalStatus.Aprovado).OrderBy(v => v.Version).ToListAsync(ct)
            : [];

    public async Task<OperationResult> StartAsync(Guid id, string actor, CancellationToken ct = default)
    {
        var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return new(false, "Campanha não encontrada");
        if (c.Status is CampaignStatus.Ativa) return new(false, "Campanha já está ativa");
        if (c.Status is CampaignStatus.Cancelada or CampaignStatus.Concluida) return new(false, "Campanha encerrada. Crie uma nova campanha.");
        if (c.Status is CampaignStatus.Pausada) return new(false, "Campanha pausada: use Retomar");
        var cfg = await gateway.GetConfigAsync(ct);
        if (cfg is null) return new(false, "Configure a Evolution API (Integrações) antes de iniciar uma campanha");
        if (CampaignWindow.Days(c).Count == 0) return new(false, "Selecione ao menos um dia da semana");
        if (c.EndTime <= c.StartTime) return new(false, "O horário final deve ser maior que o inicial");
        var versions = await ApprovedVersionsAsync(c, ct);
        if (versions.Count == 0) return new(false, "Escolha um modelo de mensagem com ao menos uma versão APROVADA");

        var eligible = (await EligibleLeadsAsync(c, ct)).Take(Math.Max(0, c.MaxContacts)).ToList();
        if (eligible.Count == 0) return new(false, "Nenhum lead elegível: é preciso consentimento válido, etapa 'Primeiro contato', nunca contatado e fora da lista de bloqueio");

        var assistant = await settings.GetAsync(SettingKeys.AssistantName, ct) is { Length: > 0 } n ? n : "Assistente Dominius Play";
        var globalInterval = int.TryParse(await settings.GetAsync(SettingKeys.MinInitialIntervalSeconds, ct), out var gi) ? gi : 300;
        var interval = Math.Max(Math.Max(300, globalInterval), c.MinIntervalSeconds); // nunca abaixo de 5 minutos
        c.MinIntervalSeconds = interval;
        var now = clock.GetUtcNow();
        for (var i = 0; i < eligible.Count; i++)
        {
            var lead = eligible[i];
            var v = versions[i % versions.Count]; // revezamento entre versões aprovadas
            db.OutboundQueue.Add(new OutboundQueueItem
            {
                LeadId = lead.Id, Kind = OutboundKind.AbordagemInicial, InstanceName = cfg.InstanceName, CampaignId = c.Id, TemplateVersionId = v.Id,
                Text = TemplateRenderer.Render(v.Body, lead.Name, assistant),
                NotBefore = now.AddSeconds((double)interval * i), // escalonamento; o SendGuard ainda reavalia cada item
                IdempotencyKey = $"camp:{c.Id:N}:{lead.Id:N}",
            });
        }
        c.InstanceName = cfg.InstanceName;
        c.Status = CampaignStatus.Ativa;
        c.StartedAt = now;
        c.UpdatedAt = now;
        db.AuditLogs.Add(new AuditLog { Actor = actor, Action = "campaign.start", EntityType = "Campaign", EntityId = c.Id.ToString(), Details = $"{eligible.Count} lead(s) na fila, intervalo {interval}s" });
        await db.SaveChangesAsync(ct);
        return new(true, $"Campanha iniciada: {eligible.Count} abordagem(ns) na fila, uma a cada {interval / 60} min, somente no horário e dias definidos.");
    }

    public async Task<OperationResult> SetStatusAsync(Guid id, CampaignStatus to, string actor, CancellationToken ct = default)
    {
        var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return new(false, "Campanha não encontrada");
        var ok = (c.Status, to) switch
        {
            (CampaignStatus.Ativa, CampaignStatus.Pausada) => true,
            (CampaignStatus.Pausada, CampaignStatus.Ativa) => true,
            (CampaignStatus.Rascunho or CampaignStatus.Ativa or CampaignStatus.Pausada, CampaignStatus.Cancelada) => true,
            _ => false,
        };
        if (!ok) return new(false, $"Não é possível passar de {c.Status} para {to}");
        var cancelled = 0;
        if (to == CampaignStatus.Cancelada)
        {
            var items = await db.OutboundQueue.Where(q => q.CampaignId == id && q.Status == MessageStatus.NaFila).ToListAsync(ct);
            foreach (var q in items) { q.Status = MessageStatus.Cancelada; q.LastError = "Campanha cancelada"; q.UpdatedAt = clock.GetUtcNow(); }
            cancelled = items.Count;
        }
        c.Status = to; c.UpdatedAt = clock.GetUtcNow();
        db.AuditLogs.Add(new AuditLog { Actor = actor, Action = $"campaign.{to.ToString().ToLowerInvariant()}", EntityType = "Campaign", EntityId = id.ToString(), Details = cancelled > 0 ? $"{cancelled} item(ns) cancelados" : null });
        await db.SaveChangesAsync(ct);
        return new(true, to switch
        {
            CampaignStatus.Pausada => "Campanha pausada. Nenhuma nova abordagem será enviada até retomar.",
            CampaignStatus.Ativa => "Campanha retomada.",
            _ => $"Campanha cancelada. {cancelled} envio(s) pendente(s) cancelado(s).",
        });
    }

    public async Task<List<CampaignDto>> ListAsync(CancellationToken ct = default)
    {
        var camps = await db.Campaigns.AsNoTracking().OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
        var items = await db.OutboundQueue.AsNoTracking().Where(q => q.CampaignId != null).Select(q => new { q.CampaignId, q.Status, q.LeadId, q.MessageId }).ToListAsync(ct);
        var msgs = await db.Messages.AsNoTracking().Where(m => m.CampaignId != null).Select(m => new { m.CampaignId, m.Status, m.LeadId, m.Timestamp }).ToListAsync(ct);
        var inbound = await db.Messages.AsNoTracking().Where(m => m.Direction == MessageDirection.Entrada).Select(m => new { m.LeadId, m.Timestamp }).ToListAsync(ct);
        var result = new List<CampaignDto>();
        foreach (var c in camps)
        {
            var its = items.Where(i => i.CampaignId == c.Id).ToList();
            var ms = msgs.Where(m => m.CampaignId == c.Id).ToList();
            var firstByLead = ms.Where(m => m.Status != MessageStatus.Falhou).GroupBy(m => m.LeadId).ToDictionary(g => g.Key, g => g.Min(x => x.Timestamp));
            var replied = inbound.Where(i => firstByLead.TryGetValue(i.LeadId, out var t) && i.Timestamp > t).Select(i => i.LeadId).Distinct().Count();
            var eligible = c.Status == CampaignStatus.Rascunho ? (await EligibleLeadsAsync(c, ct)).Count : 0;
            result.Add(ToDto(c, its.Count(i => i.Status is MessageStatus.NaFila or MessageStatus.Enviando),
                its.Count(i => i.Status == MessageStatus.EnviadaServidor),
                ms.Count(m => m.Status is MessageStatus.Entregue or MessageStatus.Lida), replied,
                its.Count(i => i.Status == MessageStatus.Falhou), its.Count(i => i.Status == MessageStatus.Cancelada), eligible));
        }
        return result;
    }

    public static CampaignDto ToDto(Campaign c, int queued = 0, int sent = 0, int delivered = 0, int replied = 0, int failed = 0, int cancelled = 0, int eligible = 0) =>
        new(c.Id, c.Name, c.Status, c.InstanceName, c.StartTime.ToString("HH:mm"), c.EndTime.ToString("HH:mm"), c.AllowedDays, c.MaxContacts,
            c.MinIntervalSeconds, c.DailyLimit, c.Objective, c.TemplateId, c.TagFilter, c.StartedAt, queued, sent, delivered, replied, failed, cancelled, eligible);

    /// <summary>Valida e aplica os campos editáveis. Retorna mensagem de erro ou null.</summary>
    public static string? Apply(Campaign c, CampaignUpsert r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Informe o nome da campanha";
        if (!TimeOnly.TryParse(r.StartTime, System.Globalization.CultureInfo.InvariantCulture, out var st)) return "Horário inicial inválido (use HH:mm)";
        if (!TimeOnly.TryParse(r.EndTime, System.Globalization.CultureInfo.InvariantCulture, out var et)) return "Horário final inválido (use HH:mm)";
        if (et <= st) return "O horário final deve ser maior que o inicial";
        var days = r.AllowedDays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (days.Count == 0 || days.Any(d => !int.TryParse(d, out var n) || n < 0 || n > 6)) return "Dias da semana inválidos";
        if (r.MaxContacts < 1 || r.MaxContacts > 10000) return "Máximo de contatos deve estar entre 1 e 10.000";
        if (r.DailyLimit < 1 || r.DailyLimit > 1000) return "Limite diário deve estar entre 1 e 1.000";
        c.Name = r.Name.Trim(); c.StartTime = st; c.EndTime = et; c.AllowedDays = string.Join(',', days.Distinct());
        c.MaxContacts = r.MaxContacts; c.MinIntervalSeconds = Math.Max(300, r.MinIntervalSeconds); c.DailyLimit = r.DailyLimit;
        c.Objective = r.Objective; c.TemplateId = r.TemplateId; c.TagFilter = r.TagFilter?.Trim() ?? "";
        return null;
    }
}
