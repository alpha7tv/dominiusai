using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Server.Hubs;
using DominiusPlay.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Endpoints;

/// <summary>Sessões do "Testar Agente": lead fictício em memória por até 2 horas (nada vai para o banco nem para o WhatsApp).</summary>
public class AgentTestSessions
{
    private readonly ConcurrentDictionary<Guid, (Lead Lead, List<Message> History, DateTimeOffset Touched)> _s = new();

    public (Lead Lead, List<Message> History) Get(Guid? id, out Guid sessionId)
    {
        foreach (var old in _s.Where(kv => DateTimeOffset.UtcNow - kv.Value.Touched > TimeSpan.FromHours(2)).Select(kv => kv.Key).ToList()) _s.TryRemove(old, out _);
        if (id is { } i && _s.TryGetValue(i, out var e)) { sessionId = i; _s[i] = (e.Lead, e.History, DateTimeOffset.UtcNow); return (e.Lead, e.History); }
        sessionId = Guid.NewGuid();
        var lead = new Lead { Name = "", Phone = "5500000000000", Mode = ConversationMode.Ia };
        _s[sessionId] = (lead, new List<Message>(), DateTimeOffset.UtcNow);
        return (lead, _s[sessionId].History);
    }

    public void Remove(Guid id) => _s.TryRemove(id, out _);
}

public static partial class AgentsApi
{
    [GeneratedRegex("^[a-z0-9_]{2,40}$")] private static partial Regex KeyRx();

    private static string Actor(ClaimsPrincipal u) => u.Identity?.Name ?? "";

    public static AgentDto ToDto(AgentProfile a, int conversations = 0) => new(a.Id, a.Kind, a.Name, a.AssistantName, a.Description, a.Objective, a.Company, a.Personality, a.Tone,
        a.Prompt, a.ProductsAndServices, a.AllowedAnswers, a.NegotiationRestrictions, a.HandoffCriteria, a.Model, a.AdvancedModel, a.Status, a.IsDefault,
        a.StartTime.ToString("HH:mm"), a.EndTime.ToString("HH:mm"), a.AllowedDays, a.TimeZoneId, a.IntentActions, a.InstanceName, a.DailyLimit, a.MinIntervalSeconds,
        a.AttendanceAgentId, conversations);

    public static QualificationQuestionDto ToDto(QualificationQuestion q) => new(q.Id, q.AgentId, q.Order, q.Key, q.Question, q.Weight, q.ScoringHint, q.Active);

    /// <summary>Valida e aplica os campos. Retorna mensagem de erro ou null.</summary>
    public static string? Apply(AgentProfile a, AgentUpsert r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Informe o nome do agente";
        if (r.Name.Trim().Length > 120) return "Nome muito longo (máximo 120 caracteres)";
        if (!TimeOnly.TryParse(r.StartTime, CultureInfo.InvariantCulture, out var st)) return "Horário inicial inválido (use HH:mm)";
        if (!TimeOnly.TryParse(r.EndTime, CultureInfo.InvariantCulture, out var et)) return "Horário final inválido (use HH:mm)";
        if (et <= st) return "O horário final deve ser maior que o inicial";
        if (BusinessHours.ParseDays(r.AllowedDays ?? "").Count == 0) return "Selecione ao menos um dia da semana";
        foreach (var m in new[] { r.Model, r.AdvancedModel })
            if (!string.IsNullOrWhiteSpace(m) && !m.Trim().StartsWith("claude-")) return "Use um modelo Claude (ex.: claude-haiku-5-5) ou deixe em branco para usar o da configuração global";
        var tz = string.IsNullOrWhiteSpace(r.TimeZoneId) ? "America/Sao_Paulo" : r.TimeZoneId.Trim();
        try { TimeZoneInfo.FindSystemTimeZoneById(tz); } catch (Exception) { return $"Fuso horário desconhecido: {tz}"; }
        if (r.DailyLimit is < 1 or > 1000) return "Limite diário deve ficar entre 1 e 1.000";
        a.Name = r.Name.Trim(); a.AssistantName = r.AssistantName?.Trim() ?? ""; a.Description = r.Description?.Trim() ?? ""; a.Objective = r.Objective?.Trim() ?? ""; a.Company = r.Company?.Trim() ?? "";
        a.Personality = r.Personality?.Trim() ?? ""; a.Tone = r.Tone?.Trim() ?? ""; a.Prompt = r.Prompt?.Trim() ?? "";
        a.ProductsAndServices = r.ProductsAndServices?.Trim() ?? ""; a.AllowedAnswers = r.AllowedAnswers?.Trim() ?? "";
        a.NegotiationRestrictions = r.NegotiationRestrictions?.Trim() ?? ""; a.HandoffCriteria = r.HandoffCriteria?.Trim() ?? "";
        a.Model = r.Model?.Trim() ?? ""; a.AdvancedModel = r.AdvancedModel?.Trim() ?? ""; a.Status = r.Status; a.IsDefault = r.IsDefault;
        a.StartTime = st; a.EndTime = et; a.AllowedDays = string.Join(',', BusinessHours.ParseDays(r.AllowedDays!).Select(d => (int)d).Order()); a.TimeZoneId = tz;
        a.IntentActions = IntentDetector.FormatActions(IntentDetector.ParseActions(r.IntentActions));
        a.InstanceName = r.InstanceName?.Trim() ?? ""; a.DailyLimit = r.DailyLimit; a.MinIntervalSeconds = Math.Max(300, r.MinIntervalSeconds);
        a.AttendanceAgentId = r.AttendanceAgentId;
        a.UpdatedAt = DateTimeOffset.UtcNow;
        return null;
    }

    public static void MapAgentsApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/agents", async (AgentKind? kind, CrmDbContext db) =>
        {
            var q = db.AgentProfiles.AsNoTracking();
            if (kind is { } k) q = q.Where(a => a.Kind == k);
            var list = await q.OrderBy(a => a.Kind).ThenByDescending(a => a.IsDefault).ThenBy(a => a.Name).ToListAsync();
            var counts = await db.Leads.AsNoTracking().Where(l => l.AttendanceAgentId != null && l.Mode != ConversationMode.Encerrado)
                .GroupBy(l => l.AttendanceAgentId).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
            return list.Select(a => ToDto(a, counts.FirstOrDefault(c => c.Key == a.Id)?.N ?? 0)).ToList();
        });

        api.MapGet("/agents/intents", () => Enum.GetValues<IntentCategory>().Where(i => i != IntentCategory.Nenhuma)
            .Select(i => new IntentInfo(i, IntentDetector.Title(i), i == IntentCategory.Descadastro ? IntentAction.Encerrar : IntentDetector.DefaultAction(i))).ToList());

        api.MapPost("/agents", async (AgentUpsert r, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var a = new AgentProfile { Kind = r.Kind };
            if (Apply(a, r) is { } err) return Results.BadRequest(new OperationResult(false, err));
            if (a.IsDefault) await ClearDefaultAsync(db, a.Kind, a.Id);
            else if (!await db.AgentProfiles.AnyAsync(x => x.Kind == a.Kind)) a.IsDefault = true; // primeiro agente do tipo vira o padrão
            db.AgentProfiles.Add(a);
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "agent.create", EntityType = "AgentProfile", EntityId = a.Id.ToString(), Details = $"{a.Kind}: {a.Name}" });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(a));
        }).RequireAuthorization(Api.AdminPolicy);

        api.MapPut("/agents/{id:guid}", async (Guid id, AgentUpsert r, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var a = await db.AgentProfiles.FindAsync(id);
            if (a is null) return Results.NotFound();
            if (r.Kind != a.Kind) return Results.BadRequest(new OperationResult(false, "O tipo do agente não pode ser alterado"));
            var before = a.Status;
            if (Apply(a, r) is { } err) return Results.BadRequest(new OperationResult(false, err));
            if (a.IsDefault) await ClearDefaultAsync(db, a.Kind, a.Id);
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "agent.update", EntityType = "AgentProfile", EntityId = a.Id.ToString(),
                Details = $"{a.Name}" + (before != a.Status ? $", estado {before} → {a.Status}" : "") });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(a));
        }).RequireAuthorization(Api.AdminPolicy);

        api.MapDelete("/agents/{id:guid}", async (Guid id, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var a = await db.AgentProfiles.FindAsync(id);
            if (a is null) return Results.NotFound();
            // conversas e campanhas voltam para o agente padrão / configuração global; perguntas exclusivas do agente são removidas
            await db.Leads.Where(l => l.AttendanceAgentId == id).ExecuteUpdateAsync(u => u.SetProperty(l => l.AttendanceAgentId, (Guid?)null));
            await db.Campaigns.Where(c => c.DispatchAgentId == id).ExecuteUpdateAsync(u => u.SetProperty(c => c.DispatchAgentId, (Guid?)null));
            await db.AgentProfiles.Where(x => x.AttendanceAgentId == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.AttendanceAgentId, (Guid?)null));
            await db.QualificationQuestions.Where(q => q.AgentId == id).ExecuteDeleteAsync();
            db.AgentProfiles.Remove(a);
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "agent.delete", EntityType = "AgentProfile", EntityId = id.ToString(), Details = a.Name });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, $"Agente \"{a.Name}\" excluído. As conversas dele passam para o agente padrão."));
        }).RequireAuthorization(Api.AdminPolicy);

        // Testar Agente: conversa simulada com memória entre os turnos. Não salva no banco e não envia mensagens (só consome tokens da IA).
        api.MapPost("/agents/{id:guid}/test", async (Guid id, AgentTestRequest r, CrmDbContext db, AiAgent agent, AgentResolver agents, AgentTestSessions sessions) =>
            await TestAsync(await db.AgentProfiles.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id), r, agent, agents, sessions));
        // mesma simulação com a configuração global (sem agente cadastrado)
        api.MapPost("/agents/global/test", async (AgentTestRequest r, AiAgent agent, AgentResolver agents, AgentTestSessions sessions) =>
            await TestAsync(null, r, agent, agents, sessions));

        // ---------- perguntas de qualificação ----------
        api.MapGet("/qualification-questions", async (Guid? agentId, bool? all, CrmDbContext db) =>
        {
            var q = db.QualificationQuestions.AsNoTracking();
            if (all != true) q = q.Where(x => x.AgentId == agentId);
            return (await q.ToListAsync()).OrderBy(x => x.AgentId.HasValue).ThenBy(x => x.Order).Select(ToDto).ToList();
        });

        api.MapPost("/qualification-questions", async (QualificationQuestionUpsert r, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var q = new QualificationQuestion();
            if (await ApplyQuestionAsync(db, q, r) is { } err) return Results.BadRequest(new OperationResult(false, err));
            db.QualificationQuestions.Add(q);
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "qualification.create", EntityType = "QualificationQuestion", EntityId = q.Id.ToString(), Details = q.Question });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(q));
        }).RequireAuthorization(Api.AdminPolicy);

        api.MapPut("/qualification-questions/{id:guid}", async (Guid id, QualificationQuestionUpsert r, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var q = await db.QualificationQuestions.FindAsync(id);
            if (q is null) return Results.NotFound();
            if (await ApplyQuestionAsync(db, q, r) is { } err) return Results.BadRequest(new OperationResult(false, err));
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "qualification.update", EntityType = "QualificationQuestion", EntityId = q.Id.ToString(), Details = q.Question });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(q));
        }).RequireAuthorization(Api.AdminPolicy);

        api.MapDelete("/qualification-questions/{id:guid}", async (Guid id, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var q = await db.QualificationQuestions.FindAsync(id);
            if (q is null) return Results.NotFound();
            db.QualificationQuestions.Remove(q);
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "qualification.delete", EntityType = "QualificationQuestion", EntityId = id.ToString(), Details = q.Question });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, "Pergunta excluída."));
        }).RequireAuthorization(Api.AdminPolicy);

        // ---------- campanhas: validar e pré-visualizar abordagens ----------
        api.MapPost("/campaigns/{id:guid}/validate", async (Guid id, CrmDbContext db, CampaignService svc) =>
            await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id) is { } c ? Results.Ok(await svc.ValidateAsync(c)) : Results.NotFound());
        api.MapPost("/campaigns/{id:guid}/preview", async (Guid id, CampaignService svc) => Results.Ok(await svc.PreviewAsync(id)));

        // ---------- transferência manual para humano ----------
        api.MapPost("/leads/{id:guid}/handoff", async (Guid id, HandoffRequest r, CrmDbContext db, ConversationLocks locks, HandoffService handoff, IHubContext<CrmHub> hub, ClaimsPrincipal user) =>
        {
            using var _ = await locks.AcquireAsync(id);
            var lead = await db.Leads.Include(l => l.Facts).FirstOrDefaultAsync(l => l.Id == id);
            if (lead is null) return Results.NotFound();
            if (lead.Mode == ConversationMode.AtendimentoHumano && lead.HandoffAt is not null) return Results.Ok(new OperationResult(false, "A conversa já está em atendimento humano."));
            var assigned = await handoff.ApplyAsync(lead, string.IsNullOrWhiteSpace(r.Reason) ? "Transferência manual" : r.Reason.Trim(), Actor(user), noticeToCustomer: false);
            await db.SaveChangesAsync();
            await handoff.NotifyAsync(id, assigned);
            await hub.Clients.All.SendAsync(HubEvents.LeadChanged, id);
            return Results.Ok(new OperationResult(true, assigned is null ? "Transferida. Nenhum atendente ativo disponível: a conversa ficou na fila humana." : $"Transferida para {assigned}."));
        });
    }

    private static async Task<IResult> TestAsync(AgentProfile? profile, AgentTestRequest r, AiAgent agent, AgentResolver agents, AgentTestSessions sessions)
    {
        if (r.Reset && r.SessionId is { } old) sessions.Remove(old);
        var (lead, history) = sessions.Get(r.Reset ? null : r.SessionId, out var sid);
        if (string.IsNullOrWhiteSpace(r.Message))
            return Results.Ok(new AgentTestResponse(sid, true, null, [], false, false, null, 0, "", "", lead.QualificationScore, lead.QualificationExplanation, null, Memory(lead), ""));
        var firstInbound = history.All(m => m.Direction != MessageDirection.Entrada);
        var intent = IntentDetector.Detect(r.Message, firstInbound);
        var now = DateTimeOffset.UtcNow;
        history.Add(new Message { LeadId = lead.Id, Direction = MessageDirection.Entrada, Author = MessageAuthor.Contato, Text = r.Message.Trim(), Timestamp = now, CreatedAt = now, Intent = intent });

        var actions = new List<string>();
        // agente de disparo: a resposta do contato à campanha é atendida pelo agente de atendimento vinculado (máquina de estados: só um agente fala)
        if (profile?.Kind == AgentKind.Disparo)
        {
            var dispatchName = profile.Name;
            profile = await agents.ForDispatchAsync(profile);
            actions.Add($"Resposta à campanha de \"{dispatchName}\" → atendida por \"{profile?.Name ?? "configuração global"}\"");
        }
        // descadastro: o sistema registra o opt-out e para de falar, sem chamar a IA (igual ao recebimento real)
        if (lead.Mode == ConversationMode.Ia && OptOutDetector.IsOptOut(r.Message))
        {
            lead.Mode = ConversationMode.Encerrado;
            actions.Add("Pedido de descadastro: número entra na lista de opt-out, consentimento revogado, nenhum envio futuro");
            return Results.Ok(new AgentTestResponse(sid, true, null, actions, false, true, null, 0, IntentDetector.Title(intent), IntentDetector.Title(intent),
                lead.QualificationScore, lead.QualificationExplanation, null, Memory(lead), "(regra do sistema, sem IA)"));
        }

        var cfg = await agent.LoadConfigAsync();
        var questions = await agents.QuestionsAsync(profile?.Id);
        string? handoffSummary = null;
        AgentResult res;
        // pedido de humano por intenção: o sistema transfere sem chamar a IA (igual ao atendimento real)
        var action = IntentDetector.ParseActions(profile?.IntentActions).GetValueOrDefault(intent, IntentAction.Continuar);
        if (lead.Mode == ConversationMode.Ia && action == IntentAction.TransferirHumano)
        {
            res = new AgentResult { Reply = HandoffService.DefaultNotice, Transferred = true, Intent = intent, HandoffReason = IntentDetector.Title(intent), Model = "(regra do sistema, sem IA)" };
            actions.Add("Regra de intenção: " + IntentDetector.Title(intent) + " → transferir para humano");
        }
        else if (lead.Mode != ConversationMode.Ia)
        {
            res = new AgentResult { Intent = intent, Model = "—" };
            actions.Add(lead.Mode == ConversationMode.AtendimentoHumano ? "Conversa já está com atendente humano: a IA não responde (use Reiniciar)" : "Conversa encerrada: a IA não responde (use Reiniciar)");
        }
        else
        {
            if (action == IntentAction.PriorizarEContinuar) actions.Add("Regra de intenção: prioridade alta");
            res = await agent.RunAsync(lead, history, cfg, trackUsage: true, default, new AgentContext(profile, questions, intent, AgentHours.OutOfHoursNote(profile, now)));
        }
        actions.AddRange(res.Actions);
        if (res.Transferred)
        {
            lead.Mode = ConversationMode.AtendimentoHumano;
            handoffSummary = HandoffService.BuildSummary(lead, lead.Facts, questions, res.HandoffReason ?? "Solicitado", "Teste", null, now);
        }
        if (res.Reply is { } reply)
            history.Add(new Message { LeadId = lead.Id, Direction = MessageDirection.Saida, Author = MessageAuthor.Ia, Text = reply, Timestamp = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow });
        var score = QualificationScorer.Compute(lead, questions);
        return Results.Ok(new AgentTestResponse(sid, res.Error is null, res.Reply, actions, res.Transferred, res.Closed, res.Error, res.Tokens,
            IntentDetector.Title(intent), IntentDetector.Title(res.Intent == IntentCategory.Nenhuma ? intent : res.Intent), score.Score, score.Explanation, handoffSummary, Memory(lead), res.Model ?? ""));
    }

    private static List<string> Memory(Lead l)
    {
        var m = new List<string>();
        if (!string.IsNullOrWhiteSpace(l.Name)) m.Add("Nome: " + l.Name);
        if (l.Experience != IptvExperience.NaoInformado) m.Add("Experiência: " + l.Experience);
        if (!string.IsNullOrWhiteSpace(l.PanelUsed)) m.Add("Painel: " + l.PanelUsed);
        if (l.ActiveClients is { } c) m.Add("Clientes ativos: " + c);
        if (l.MainPain != PainCategory.Nenhuma) m.Add("Dor principal: " + PainClassifier.Title(l.MainPain));
        if (!string.IsNullOrWhiteSpace(l.InterestPlanCode)) m.Add("Plano de interesse: " + l.InterestPlanCode);
        if (!string.IsNullOrWhiteSpace(l.ConversationSummary)) m.Add("Resumo: " + l.ConversationSummary);
        m.Add("Etapa: " + KanbanRules.Title(l.Stage));
        m.AddRange(l.Facts.OrderBy(f => f.CreatedAt).Select(f => $"{f.Key}: {f.Value}"));
        return m;
    }

    private static async Task ClearDefaultAsync(CrmDbContext db, AgentKind kind, Guid except) =>
        await db.AgentProfiles.Where(a => a.Kind == kind && a.Id != except && a.IsDefault).ExecuteUpdateAsync(u => u.SetProperty(a => a.IsDefault, false));

    private static async Task<string?> ApplyQuestionAsync(CrmDbContext db, QualificationQuestion q, QualificationQuestionUpsert r)
    {
        if (string.IsNullOrWhiteSpace(r.Question)) return "Escreva a pergunta";
        var key = (r.Key ?? "").Trim().ToLowerInvariant();
        if (!KeyRx().IsMatch(key)) return "Chave inválida: use de 2 a 40 letras minúsculas, números ou _ (ex.: tem_clientes)";
        if (r.Weight is < 0 or > 40) return "Peso deve ficar entre 0 e 40";
        if (r.AgentId is { } aid && !await db.AgentProfiles.AnyAsync(a => a.Id == aid && a.Kind == AgentKind.Atendimento)) return "Agente de atendimento não encontrado";
        if (await db.QualificationQuestions.AnyAsync(x => x.Id != q.Id && x.Key == key && x.AgentId == r.AgentId)) return $"Já existe uma pergunta com a chave {key}";
        q.AgentId = r.AgentId; q.Order = r.Order; q.Key = key; q.Question = r.Question.Trim(); q.Weight = r.Weight; q.ScoringHint = r.ScoringHint?.Trim() ?? ""; q.Active = r.Active;
        q.UpdatedAt = DateTimeOffset.UtcNow;
        return null;
    }
}
