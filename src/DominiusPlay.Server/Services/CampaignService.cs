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
/// cada envio continua passando pelo SendGuard (consentimento, horário, limite diário, intervalo de 15 min etc.).
/// </summary>
public class CampaignService(CrmDbContext db, SettingsStore settings, EvolutionGateway gateway, ApproachGenerator approaches, TimeProvider clock)
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

        var batches = ParseIds(c.ImportBatchIds);
        var to = c.ImportedTo?.AddDays(1); // até o fim do dia escolhido

        var leads = await db.Leads.Where(l => l.ConsentStatus == RequiredConsent && l.Stage == KanbanStage.PrimeiroContato
            && l.Mode == ConversationMode.AguardandoContato).ToListAsync(ct);
        return leads.Where(l => !optOuts.Contains(l.Phone) && !contacted.Contains(l.Id) && !inbound.Contains(l.Id) && !queued.Contains(l.Id))
            .Where(l => tags.Count == 0 || l.Tags.Split(',', StringSplitOptions.TrimEntries).Any(t => tags.Contains(t.ToLowerInvariant())))
            .Where(l => string.IsNullOrWhiteSpace(c.Segment) || string.Equals(l.Segment?.Trim(), c.Segment.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(l => batches.Count == 0 || (l.ImportBatchId is { } b && batches.Contains(b)))
            .Where(l => c.ImportedFrom is null || (l.ImportedAt ?? l.CreatedAt) >= c.ImportedFrom)
            .Where(l => to is null || (l.ImportedAt ?? l.CreatedAt) < to)
            .DistinctBy(l => l.Phone) // nunca duas abordagens para o mesmo número
            .OrderByDescending(l => l.Priority).ThenBy(l => l.ConsentAt ?? l.CreatedAt).ToList();
    }

    /// <summary>Lista de Ids separados por vírgula ("a,b") → Ids válidos, sem repetição.</summary>
    public static List<Guid> ParseIds(string? ids) =>
        (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();

    public async Task<List<MessageTemplateVersion>> ApprovedVersionsAsync(Campaign c, CancellationToken ct = default) =>
        c.TemplateId is { } tid
            ? await db.MessageTemplateVersions.Where(v => v.TemplateId == tid && v.Approval == ApprovalStatus.Aprovado).OrderBy(v => v.Version).ToListAsync(ct)
            : [];

    /// <summary>Dias de cada etapa do acompanhamento ("1,3,7" → [1,3,7]); ignora valores inválidos.</summary>
    public static List<int> FollowUpDayList(string? days) =>
        (days ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => int.TryParse(d, out var n) ? n : -1).Where(n => n is >= 1 and <= 60).Distinct().Order().Take(5).ToList();

    /// <summary>Confere tudo o que impede ou recomenda ajuste antes de iniciar (botão Validar e início da campanha).</summary>
    public async Task<CampaignValidation> ValidateAsync(Campaign c, CancellationToken ct = default)
    {
        var problems = new List<string>(); var warnings = new List<string>();
        if (await gateway.GetConfigAsync(ct) is null) problems.Add("Configure a Evolution API (Integrações) antes de iniciar uma campanha");
        if (CampaignWindow.Days(c).Count == 0) problems.Add("Selecione ao menos um dia da semana");
        if (c.EndTime <= c.StartTime) problems.Add("O horário final deve ser maior que o inicial");
        var versions = await ApprovedVersionsAsync(c, ct);
        if (versions.Count == 0) problems.Add("Escolha um modelo de mensagem com ao menos uma versão APROVADA");
        AgentProfile? agent = null;
        if (c.DispatchAgentId is { } aid)
        {
            agent = await db.AgentProfiles.AsNoTracking().FirstOrDefaultAsync(a => a.Id == aid, ct);
            if (agent is null || agent.Kind != AgentKind.Disparo) problems.Add("Agente de disparo não encontrado");
            else if (agent.Status != AgentStatus.Ativo) problems.Add($"O agente de disparo \"{agent.Name}\" está {agent.Status.ToString().ToLowerInvariant()}");
        }
        var official = await settings.GetAsync(SettingKeys.EvolutionIntegration, ct) == "WHATSAPP-BUSINESS";
        if (c.AiPersonalization && official)
            problems.Add("No modo WhatsApp Business (Cloud API oficial) a abordagem fora da janela de 24h precisa ser o template aprovado pela Meta, sem variação: desligue a personalização por IA");
        if (c.AiPersonalization && string.IsNullOrWhiteSpace(await settings.GetAsync(SettingKeys.AiApiKey, ct)))
            warnings.Add("Personalização ligada, mas a chave da IA não está configurada: será enviado o modelo aprovado");
        var days = FollowUpDayList(c.FollowUpDays);
        if (days.Count > 0)
        {
            var fu = c.FollowUpTemplateId is { } ft ? await db.MessageTemplateVersions.AsNoTracking().CountAsync(v => v.TemplateId == ft && v.Approval == ApprovalStatus.Aprovado, ct) : 0;
            if (fu == 0) problems.Add("Acompanhamento configurado sem modelo de acompanhamento aprovado (cada etapa usa uma versão aprovada)");
            else if (fu < days.Count) warnings.Add($"O modelo de acompanhamento tem {fu} versão(ões) aprovada(s) para {days.Count} etapa(s): só {fu} etapa(s) serão enviadas");
        }
        var eligible = (await EligibleLeadsAsync(c, ct)).Count;
        if (eligible == 0) problems.Add("Nenhum lead elegível: é preciso consentimento válido, etapa 'Primeiro contato', nunca contatado, fora da lista de bloqueio" + (string.IsNullOrWhiteSpace(c.Segment) ? "" : $" e segmento \"{c.Segment}\"")
            + (ParseIds(c.ImportBatchIds).Count > 0 || c.ImportedFrom is not null || c.ImportedTo is not null ? " (confira também as listas e o período de importação escolhidos)" : ""));
        if (official) warnings.Add("Modo Cloud API oficial: verifique se o modelo aprovado tem o nome do template aprovado pela Meta");
        else warnings.Add("Modo WhatsApp Web (Baileys): não é a API oficial; volumes altos de prospecção aumentam o risco de bloqueio do número");
        if (c.StartDate is { } sd && sd > clock.GetUtcNow()) warnings.Add($"A campanha só começa a enviar em {sd.ToOffset(TimeSpan.FromHours(-3)):dd/MM/yyyy HH:mm}");
        return new(problems.Count == 0, eligible, problems, warnings);
    }

    public async Task<OperationResult> StartAsync(Guid id, string actor, CancellationToken ct = default)
    {
        var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return new(false, "Campanha não encontrada");
        if (c.Status is CampaignStatus.Ativa) return new(false, "Campanha já está ativa");
        if (c.Status is CampaignStatus.Cancelada or CampaignStatus.Concluida) return new(false, "Campanha encerrada. Crie uma nova campanha.");
        if (c.Status is CampaignStatus.Pausada) return new(false, "Campanha pausada: use Retomar");
        var validation = await ValidateAsync(c, ct);
        if (!validation.Ok) return new(false, validation.Problems[0], string.Join("\n", validation.Problems));
        var cfg = (await gateway.GetConfigAsync(ct))!;
        var versions = await ApprovedVersionsAsync(c, ct);
        var eligible = (await EligibleLeadsAsync(c, ct)).Take(Math.Max(0, c.MaxContacts)).ToList();
        var agent = c.DispatchAgentId is { } aid ? await db.AgentProfiles.AsNoTracking().FirstOrDefaultAsync(a => a.Id == aid, ct) : null;
        var instance = string.IsNullOrWhiteSpace(agent?.InstanceName) ? cfg.InstanceName : agent!.InstanceName;

        var assistant = await settings.GetAsync(SettingKeys.AssistantName, ct) is { Length: > 0 } n ? n : "Assistente Dominius Play";
        if (!string.IsNullOrWhiteSpace(agent?.AssistantName)) assistant = agent!.AssistantName;
        var globalInterval = int.TryParse(await settings.GetAsync(SettingKeys.MinInitialIntervalSeconds, ct), out var gi) ? gi : SendLimits.MinInitialIntervalSeconds;
        var interval = Math.Max(Math.Max(SendLimits.MinInitialIntervalSeconds, globalInterval), Math.Max(c.MinIntervalSeconds, agent?.MinIntervalSeconds ?? 0)); // nunca abaixo de 15 minutos
        c.MinIntervalSeconds = interval;
        var days = FollowUpDayList(c.FollowUpDays);
        var fuVersions = days.Count > 0 && c.FollowUpTemplateId is { } ft
            ? await db.MessageTemplateVersions.Where(v => v.TemplateId == ft && v.Approval == ApprovalStatus.Aprovado).OrderBy(v => v.Version).ToListAsync(ct) : [];
        var now = clock.GetUtcNow();
        var first = c.StartDate is { } sd && sd > now ? sd : now;
        var followUps = 0;
        for (var i = 0; i < eligible.Count; i++)
        {
            var lead = eligible[i];
            var v = versions[i % versions.Count]; // revezamento entre versões aprovadas
            var at = first.AddSeconds((double)interval * i); // escalonamento; o SendGuard ainda reavalia cada item
            db.OutboundQueue.Add(new OutboundQueueItem
            {
                LeadId = lead.Id, Kind = OutboundKind.AbordagemInicial, InstanceName = instance, CampaignId = c.Id, TemplateVersionId = v.Id,
                Text = TemplateRenderer.Render(v.Body, lead.Name, assistant), NotBefore = at, Personalize = c.AiPersonalization,
                IdempotencyKey = $"camp:{c.Id:N}:{lead.Id:N}",
            });
            for (var step = 0; step < Math.Min(days.Count, fuVersions.Count); step++)
            {
                db.OutboundQueue.Add(new OutboundQueueItem
                {
                    LeadId = lead.Id, Kind = OutboundKind.Acompanhamento, InstanceName = instance, CampaignId = c.Id, TemplateVersionId = fuVersions[step].Id,
                    Text = TemplateRenderer.Render(fuVersions[step].Body, lead.Name, assistant), NotBefore = at.AddDays(days[step]), FollowUpStep = step + 1,
                    IdempotencyKey = $"camp:{c.Id:N}:{lead.Id:N}:fu{step + 1}",
                });
                followUps++;
            }
        }
        c.InstanceName = instance;
        c.Status = CampaignStatus.Ativa;
        c.StartedAt = now; c.PauseReason = null;
        c.UpdatedAt = now;
        db.AuditLogs.Add(new AuditLog { Actor = actor, Action = "campaign.start", EntityType = "Campaign", EntityId = c.Id.ToString(),
            Details = $"{eligible.Count} lead(s) na fila, intervalo {interval}s, {followUps} acompanhamento(s), personalização IA {(c.AiPersonalization ? "ligada" : "desligada")}" });
        await db.SaveChangesAsync(ct);
        return new(true, $"Campanha iniciada: {eligible.Count} abordagem(ns) na fila, uma a cada {interval / 60} min, somente no horário e dias definidos."
            + (followUps > 0 ? $" {followUps} acompanhamento(s) agendado(s); param sozinhos quando o contato responde." : ""));
    }

    /// <summary>Prévia da abordagem para até 3 contatos elegíveis (com IA se a personalização estiver ligada). Nada é salvo nem enviado.</summary>
    public async Task<List<ApproachPreview>> PreviewAsync(Guid id, CancellationToken ct = default)
    {
        var c = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return [];
        var versions = await ApprovedVersionsAsync(c, ct);
        if (versions.Count == 0) return [new("—", "", "Escolha um modelo com versão aprovada")];
        var agent = c.DispatchAgentId is { } aid ? await db.AgentProfiles.AsNoTracking().FirstOrDefaultAsync(a => a.Id == aid, ct) : null;
        var assistant = !string.IsNullOrWhiteSpace(agent?.AssistantName) ? agent!.AssistantName
            : await settings.GetAsync(SettingKeys.AssistantName, ct) is { Length: > 0 } n ? n : "Assistente Dominius Play";
        var leads = (await EligibleLeadsAsync(c, ct)).Take(3).ToList();
        if (leads.Count == 0) leads = [new Lead { Name = "", Phone = "5500000000000", City = "Bauru", State = "SP" }];
        var list = new List<ApproachPreview>();
        for (var i = 0; i < leads.Count; i++)
        {
            var l = leads[i];
            var text = TemplateRenderer.Render(versions[i % versions.Count].Body, l.Name, assistant);
            var who = string.IsNullOrWhiteSpace(l.Name) ? PhoneNormalizer.Format(l.Phone) : l.Name;
            if (!c.AiPersonalization) { list.Add(new(who, text, "Modelo aprovado (personalização desligada)")); continue; }
            var gen = await approaches.GenerateAsync(l, text, c, agent, ct);
            list.Add(new(who, gen.Text, gen.Note));
        }
        return list;
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
        if (to == CampaignStatus.Ativa) c.PauseReason = null;
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
        var items = await db.OutboundQueue.AsNoTracking().Where(q => q.CampaignId != null).Select(q => new { q.CampaignId, q.Status, q.LeadId, q.MessageId, q.Kind }).ToListAsync(ct);
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
            var contactedPhones = await db.Leads.AsNoTracking().Where(l => firstByLead.Keys.Contains(l.Id)).Select(l => l.Phone).ToListAsync(ct);
            var optOuts = contactedPhones.Count == 0 ? 0 : await db.OptOuts.AsNoTracking().CountAsync(o => contactedPhones.Contains(o.Phone), ct);
            var similar = await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "campaign.similarity-alert" && a.EntityId == c.Id.ToString(), ct);
            result.Add(ToDto(c, its.Count(i => i.Status is MessageStatus.NaFila or MessageStatus.Enviando),
                its.Count(i => i.Status == MessageStatus.EnviadaServidor),
                ms.Count(m => m.Status is MessageStatus.Entregue or MessageStatus.Lida), replied,
                its.Count(i => i.Status == MessageStatus.Falhou), its.Count(i => i.Status == MessageStatus.Cancelada), eligible)
                with { OptOuts = optOuts, SimilarityAlerts = similar, FollowUpsSent = its.Count(i => i.Status == MessageStatus.EnviadaServidor && i.Kind == OutboundKind.Acompanhamento) });
        }
        return result;
    }

    public static CampaignDto ToDto(Campaign c, int queued = 0, int sent = 0, int delivered = 0, int replied = 0, int failed = 0, int cancelled = 0, int eligible = 0) =>
        new(c.Id, c.Name, c.Status, c.InstanceName, c.StartTime.ToString("HH:mm"), c.EndTime.ToString("HH:mm"), c.AllowedDays, c.MaxContacts,
            c.MinIntervalSeconds, c.DailyLimit, c.Objective, c.TemplateId, c.TagFilter, c.StartedAt, queued, sent, delivered, replied, failed, cancelled, eligible,
            c.DispatchAgentId, c.Segment, c.Prompt, c.StartDate, c.AiPersonalization, c.FollowUpTemplateId, c.FollowUpDays, c.MaxFailurePercent, c.MaxOptOutPercent, c.PauseReason,
            ImportBatchIds: c.ImportBatchIds, ImportedFrom: c.ImportedFrom, ImportedTo: c.ImportedTo);

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
        c.MaxContacts = r.MaxContacts; c.MinIntervalSeconds = Math.Max(SendLimits.MinInitialIntervalSeconds, r.MinIntervalSeconds); c.DailyLimit = r.DailyLimit;
        c.Objective = r.Objective; c.TemplateId = r.TemplateId; c.TagFilter = r.TagFilter?.Trim() ?? "";
        if (r.MaxFailurePercent is < 1 or > 100 || r.MaxOptOutPercent is < 1 or > 100) return "Limites de pausa automática devem ficar entre 1% e 100%";
        c.DispatchAgentId = r.DispatchAgentId; c.Segment = r.Segment?.Trim() ?? ""; c.Prompt = r.Prompt?.Trim() ?? ""; c.StartDate = r.StartDate;
        c.AiPersonalization = r.AiPersonalization; c.FollowUpTemplateId = r.FollowUpTemplateId;
        c.FollowUpDays = string.Join(",", FollowUpDayList(r.FollowUpDays)); c.MaxFailurePercent = r.MaxFailurePercent; c.MaxOptOutPercent = r.MaxOptOutPercent;
        if (r.ImportedFrom is { } f && r.ImportedTo is { } t && t < f) return "Período de importação inválido: a data final é anterior à inicial";
        c.ImportBatchIds = string.Join(",", ParseIds(r.ImportBatchIds)); c.ImportedFrom = r.ImportedFrom; c.ImportedTo = r.ImportedTo;
        return null;
    }
}
