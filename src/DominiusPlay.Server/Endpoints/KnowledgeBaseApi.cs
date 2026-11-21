using System.Security.Claims;
using System.Text.Json;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Security;
using DominiusPlay.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Endpoints;

/// <summary>
/// Agentes IA → Base de Conhecimento → Dominius Play (v0.4.1): perguntas e respostas, configuração comercial,
/// acesso dos agentes e Laboratório de Atendimento IA. Leitura para toda a equipe; alterações só para administradores.
/// Tudo é lido do banco a cada mensagem: publicar não exige reiniciar o servidor.
/// </summary>
public static class KnowledgeBaseApi
{
    private static string Actor(ClaimsPrincipal u) => u.Identity?.Name ?? "";

    private static KbEntryDto ToDto(KbEntry e, KbSnapshot kb)
    {
        var r = KnowledgeRenderer.Render(e.Answer, kb.Config, kb.Plans);
        return new KbEntryDto(e.Id, e.Base, e.Code, e.Category, e.Question, e.Variations, e.Answer, r.Text, e.Keywords, e.Products, e.Priority, e.Status, e.Source,
            e.NeedsConfirmation, e.Version, e.UpdatedAt, e.UpdatedBy, r.Warnings);
    }

    private static void Apply(KbEntry e, KbEntryUpsert r)
    {
        e.Code = r.Code.Trim().ToUpperInvariant(); e.Category = r.Category.Trim(); e.Question = r.Question.Trim();
        e.Variations = string.Join("\n", (r.Variations ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(30));
        e.Answer = r.Answer.Trim(); e.Keywords = (r.Keywords ?? "").Trim(); e.Products = (r.Products ?? "").Replace(" ", "").ToUpperInvariant();
        e.Priority = r.Priority; e.Status = r.Status; e.Source = (r.Source ?? "").Trim(); e.NeedsConfirmation = r.NeedsConfirmation;
    }

    public static void MapKnowledgeBaseApi(this WebApplication app)
    {
        var kbApi = app.MapGroup("/api/kb").RequireAuthorization();

        kbApi.MapGet("", async (KnowledgeBaseService svc, CrmDbContext db, string? q, string? category, KbStatus? status) =>
        {
            var kb = await svc.LoadAsync(includeDrafts: true);
            var list = await db.KbEntries.AsNoTracking().Where(e => e.Base == DominiusKnowledge.BaseName).ToListAsync();
            if (status is not null) list = list.Where(e => e.Status == status).ToList();
            if (!string.IsNullOrWhiteSpace(category)) list = list.Where(e => e.Category == category).ToList();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var nq = KnowledgeMatcher.Normalize(q);
                var hits = KnowledgeMatcher.Search(q, list, take: 200, minScore: 0.2).Select(m => m.Entry.Id).ToList();
                list = list.Where(e => hits.Contains(e.Id) || KnowledgeMatcher.Normalize(e.Code + " " + e.Question + " " + e.Answer + " " + e.Keywords).Contains(nq))
                    .OrderBy(e => hits.IndexOf(e.Id) is var i && i >= 0 ? i : 999).ToList();
            }
            else list = list.OrderBy(e => e.Code).ToList();
            return list.Select(e => ToDto(e, kb)).ToList();
        });

        kbApi.MapGet("/categories", () => DominiusKnowledge.Categories);
        kbApi.MapGet("/placeholders", () => KnowledgeRenderer.Placeholders.Select(p => new ReportRow(p.Key, 0, p.Description)).ToList());

        kbApi.MapGet("/{id:guid}/history", async (Guid id, CrmDbContext db) =>
            (await db.KbEntryRevisions.AsNoTracking().Where(r => r.EntryId == id).ToListAsync()).OrderByDescending(r => r.CreatedAt).Select(r =>
            {
                var s = JsonSerializer.Deserialize<KbHistory.Snapshot>(r.Snapshot);
                return new KbRevisionDto(r.Version, r.Action, r.ChangedBy, r.CreatedAt, s?.Question ?? "", s?.Answer ?? "", s?.Status.ToString() ?? "");
            }).ToList());

        kbApi.MapPost("", async (KbEntryUpsert r, CrmDbContext db, KnowledgeBaseService svc, ClaimsPrincipal user) =>
        {
            var errors = KbHistory.Validate(r.Code, r.Category, r.Question, r.Answer, r.Priority);
            if (errors.Count > 0) return Results.BadRequest(new OperationResult(false, string.Join("; ", errors)));
            var code = r.Code.Trim().ToUpperInvariant();
            if (await db.KbEntries.AnyAsync(e => e.Base == DominiusKnowledge.BaseName && e.Code == code)) return Results.Conflict(new OperationResult(false, $"Já existe a pergunta {code}"));
            var e = new KbEntry { Base = DominiusKnowledge.BaseName, UpdatedBy = Actor(user) };
            Apply(e, r);
            db.KbEntries.Add(e);
            db.KbEntryRevisions.Add(KbHistory.Revision(e, "criada", Actor(user)));
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "kb.create", EntityType = "KbEntry", EntityId = e.Id.ToString(), Details = e.Code });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(e, await svc.LoadAsync(true)));
        }).RequireAuthorization(Api.AdminPolicy);

        kbApi.MapPut("/{id:guid}", async (Guid id, KbEntryUpsert r, CrmDbContext db, KnowledgeBaseService svc, ClaimsPrincipal user) =>
        {
            var e = await db.KbEntries.FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            var errors = KbHistory.Validate(r.Code, r.Category, r.Question, r.Answer, r.Priority);
            if (errors.Count > 0) return Results.BadRequest(new OperationResult(false, string.Join("; ", errors)));
            var code = r.Code.Trim().ToUpperInvariant();
            if (code != e.Code && await db.KbEntries.AnyAsync(x => x.Base == e.Base && x.Code == code)) return Results.Conflict(new OperationResult(false, $"Já existe a pergunta {code}"));
            var before = e.Status;
            Apply(e, r);
            e.Version++; e.UpdatedAt = DateTimeOffset.UtcNow; e.UpdatedBy = Actor(user);
            db.KbEntryRevisions.Add(KbHistory.Revision(e, before != e.Status ? (e.Status == KbStatus.Publicado ? "publicada" : e.Status == KbStatus.Inativo ? "desativada" : "editada") : "editada", Actor(user)));
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "kb.update", EntityType = "KbEntry", EntityId = e.Id.ToString(), Details = $"{e.Code} v{e.Version}" });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(e, await svc.LoadAsync(true)));
        }).RequireAuthorization(Api.AdminPolicy);

        kbApi.MapPost("/{id:guid}/status", async (Guid id, KbStatus status, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var e = await db.KbEntries.FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            if (e.Status == status) return Results.Ok(new OperationResult(true, "Sem alteração."));
            e.Status = status; e.Version++; e.UpdatedAt = DateTimeOffset.UtcNow; e.UpdatedBy = Actor(user);
            var action = status switch { KbStatus.Publicado => "publicada", KbStatus.Inativo => "desativada", _ => "voltou para rascunho" };
            db.KbEntryRevisions.Add(KbHistory.Revision(e, action, Actor(user)));
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "kb.status", EntityType = "KbEntry", EntityId = e.Id.ToString(), Details = $"{e.Code} {action}" });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, status == KbStatus.Publicado ? $"{e.Code} publicada: a IA já usa a partir da próxima mensagem." : $"{e.Code} {action}: a IA deixa de usar."));
        }).RequireAuthorization(Api.AdminPolicy);

        kbApi.MapDelete("/{id:guid}", async (Guid id, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var e = await db.KbEntries.FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            db.KbEntryRevisions.Add(KbHistory.Revision(e, "excluída", Actor(user))); // o histórico fica (cópia completa)
            db.KbEntries.Remove(e);
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "kb.delete", EntityType = "KbEntry", EntityId = e.Id.ToString(), Details = e.Code + ": " + e.Question });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, $"{e.Code} excluída (cópia mantida no histórico)."));
        }).RequireAuthorization(Api.AdminPolicy);

        // Testar antes de publicar: inclui rascunhos e mostra perfil, gatilho e oferta sugerida.
        kbApi.MapPost("/test", async (KbTestRequest r, KnowledgeBaseService svc) =>
        {
            var kb = await svc.LoadAsync(includeDrafts: r.IncludeDrafts);
            var matches = KnowledgeMatcher.Search(r.Question, kb.Entries, r.Products?.ToList(), take: 5, minScore: 0.2);
            var trig = CommercialTriggers.Detect(r.Question);
            return new KbTestResponse(matches.Select(m =>
            {
                var rr = KnowledgeRenderer.Render(m.Entry.Answer, kb.Config, kb.Plans);
                return new KbTestMatch(m.Entry.Code, m.Entry.Category, m.Entry.Question, m.Score, rr.Text, m.Entry.Status, m.Entry.NeedsConfirmation,
                    rr.Warnings.Concat(ReplySafetyGuard.Check(rr.Text, kb.Config)).ToList());
            }).ToList(), SalesProfiles.Title(SalesProfiles.Detect(r.Question)), CommercialTriggers.Title(trig),
                CommercialTriggers.OfferMessage(trig, kb.Config, kb.Plans), IntentDetector.Title(IntentDetector.Detect(r.Question)));
        });

        kbApi.MapGet("/export", async (CrmDbContext db) =>
            (await db.KbEntries.AsNoTracking().Where(e => e.Base == DominiusKnowledge.BaseName).ToListAsync()).OrderBy(e => e.Code)
                .Select(e => new KbEntryUpsert(e.Code, e.Category, e.Question, e.Variations, e.Answer, e.Keywords, e.Products, e.Priority, e.Status, e.Source, e.NeedsConfirmation)).ToList());

        kbApi.MapPost("/import", async (KbImportRequest r, CrmDbContext db, ClaimsPrincipal user) =>
        {
            if (r.Entries is null || r.Entries.Count == 0) return Results.BadRequest(new OperationResult(false, "Arquivo sem perguntas"));
            if (r.Entries.Count > 2000) return Results.BadRequest(new OperationResult(false, "No máximo 2000 perguntas por importação"));
            var existing = await db.KbEntries.Where(e => e.Base == DominiusKnowledge.BaseName).ToDictionaryAsync(e => e.Code);
            int created = 0, updated = 0, skipped = 0; var errors = new List<string>();
            foreach (var (item, i) in r.Entries.Select((x, i) => (x, i + 1)))
            {
                var problems = KbHistory.Validate(item.Code, item.Category, item.Question, item.Answer, item.Priority);
                if (problems.Count > 0) { errors.Add($"Linha {i} ({item.Code}): {string.Join("; ", problems)}"); skipped++; continue; }
                var code = item.Code.Trim().ToUpperInvariant();
                if (existing.TryGetValue(code, out var e))
                {
                    if (!r.Overwrite) { skipped++; continue; }
                    Apply(e, item); e.Version++; e.UpdatedAt = DateTimeOffset.UtcNow; e.UpdatedBy = Actor(user);
                    db.KbEntryRevisions.Add(KbHistory.Revision(e, "importada", Actor(user))); updated++;
                }
                else
                {
                    e = new KbEntry { Base = DominiusKnowledge.BaseName, UpdatedBy = Actor(user) };
                    Apply(e, item); db.KbEntries.Add(e); existing[code] = e;
                    db.KbEntryRevisions.Add(KbHistory.Revision(e, "importada", Actor(user))); created++;
                }
            }
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "kb.import", EntityType = "KbEntry", Details = $"{created} criadas, {updated} atualizadas, {skipped} ignoradas" });
            await db.SaveChangesAsync();
            return Results.Ok(new KbImportResult(created, updated, skipped, errors));
        }).RequireAuthorization(Api.AdminPolicy);

        kbApi.MapGet("/access", async (KnowledgeBaseService svc) => { var (a, d) = await svc.AccessAsync(); return new KbAccessDto(a, d); });
        kbApi.MapPut("/access", async (KbAccessDto r, SettingsStore s, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var v = string.Join(",", new[] { r.Attendance ? "Atendimento" : null, r.Dispatch ? "Disparo" : null }.Where(x => x is not null));
            await s.SetAsync(SettingKeys.KbAccess, v);
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "kb.access", EntityType = "Setting", Details = v.Length == 0 ? "nenhum agente" : v });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, "Acesso dos agentes à base atualizado."));
        }).RequireAuthorization(Api.AdminPolicy);

        // ---------- Configuração comercial (Pix, links, prazos, regras) ----------
        app.MapGet("/api/commercial", async (KnowledgeBaseService svc) => await svc.ConfigAsync()).RequireAuthorization();
        app.MapPut("/api/commercial", async (CommercialConfig r, SettingsStore s, KnowledgeBaseService svc, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var errors = r.Validate();
            if (errors.Count > 0) return Results.BadRequest(new OperationResult(false, string.Join("; ", errors)));
            var before = await svc.ConfigAsync();
            var diff = typeof(CommercialConfig).GetProperties().Where(p => !Equals(p.GetValue(before), p.GetValue(r))).Select(p => p.Name).ToList();
            await s.SetAsync(SettingKeys.CommercialConfig, r.ToJson());
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "commercial.update", EntityType = "Setting",
                Details = diff.Count == 0 ? "sem alterações" : "Alterado: " + string.Join(", ", diff) + (diff.Contains(nameof(CommercialConfig.PixKey)) ? $" (Pix: {before.PixKey} → {r.PixKey})" : "") });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, "Configuração comercial salva. A IA usa os novos dados a partir da próxima mensagem."));
        }).RequireAuthorization(Api.AdminPolicy);
        app.MapGet("/api/commercial/history", async (CrmDbContext db) =>
            (await db.AuditLogs.AsNoTracking().Where(a => a.Action == "commercial.update" || a.Action == "commercial.upgrade" || a.Action == "kb.access").ToListAsync())
                .OrderByDescending(a => a.CreatedAt).Take(100).Select(a => new CommercialHistoryDto(a.CreatedAt, a.Actor, a.Details ?? "")).ToList()).RequireAuthorization();

        // ---------- Laboratório de Atendimento IA (nada é salvo nem enviado; nenhum pagamento) ----------
        app.MapGet("/api/lab/scenarios", () => AttendanceLab.Scenarios.Select(s => new LabScenarioDto(s.Key, s.Title, s.Messages)).ToList()).RequireAuthorization();
        app.MapPost("/api/lab/run", async (LabRequest r, KnowledgeBaseService svc, AiAgent agent, CrmDbContext db) =>
        {
            var scenario = r.Scenario is null ? null : AttendanceLab.Scenarios.FirstOrDefault(s => s.Key == r.Scenario);
            var messages = (r.Messages is { Count: > 0 } ? r.Messages : scenario?.Messages ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Take(20).Select(m => m.Length > 1000 ? m[..1000] : m).ToList();
            if (messages.Count == 0) return Results.BadRequest(new OperationResult(false, "Escolha um perfil de cliente ou escreva as mensagens"));
            var kb = await svc.LoadAsync();
            var steps = AttendanceLab.Run(messages, kb.Entries, kb.Config, kb.Plans);

            var aiReplies = new Dictionary<int, AgentResult>();
            string? aiError = null;
            var cfg = await agent.LoadConfigAsync();
            var useAi = r.UseAi && !string.IsNullOrWhiteSpace(cfg.ApiKey);
            if (r.UseAi && !useAi) aiError = "Chave da Anthropic não configurada: mostrando só a análise da base.";
            if (useAi)
            {
                var questions = await db.QualificationQuestions.AsNoTracking().Where(q => q.Active && q.AgentId == null).OrderBy(q => q.Order).ToListAsync();
                var lead = new Lead { Name = "", Phone = "5500000000000", Mode = ConversationMode.Ia };
                var history = new List<Message>();
                var t0 = DateTimeOffset.UtcNow.AddMinutes(-messages.Count * 2);
                for (var i = 0; i < messages.Count; i++)
                {
                    if (lead.Mode != ConversationMode.Ia) break; // transferida/encerrada: a IA para de responder
                    history.Add(new Message { LeadId = lead.Id, Text = messages[i], Direction = MessageDirection.Entrada, Author = MessageAuthor.Contato, Timestamp = t0.AddMinutes(i * 2) });
                    var intent = IntentDetector.Detect(messages[i], i == 0);
                    if (intent == IntentCategory.Descadastro) { ConversationStateMachine.Apply(lead, ConversationTrigger.Encerrar); break; } // regra fixa: sem IA
                    var res = await agent.RunAsync(lead, history, cfg, trackUsage: true, default, new AgentContext(null, questions, intent));
                    aiReplies[i] = res;
                    if (res.Error is not null && res.Reply is null) { aiError = res.Error; break; }
                    if (res.Reply is not null) history.Add(new Message { LeadId = lead.Id, Text = res.Reply, Direction = MessageDirection.Saida, Author = MessageAuthor.Ia, Timestamp = t0.AddMinutes(i * 2 + 1) });
                }
            }
            return Results.Ok(new LabResponse(scenario?.Title ?? "Conversa personalizada", steps.Select((s, i) =>
            {
                aiReplies.TryGetValue(i, out var ai);
                return new LabStepDto(i + 1, s.Message, s.IntentTitle, SalesProfiles.Title(s.Profile), CommercialTriggers.Title(s.Trigger), s.Category, s.MatchedCode, s.MatchedQuestion,
                    s.Score, s.Reply, s.Product, s.MemoryUsed, s.SuggestedAction, s.Alerts, s.Handoff, s.OptOut,
                    ai?.Reply, ai is null ? [] : ai.Alerts.ToList(), ai is null ? [] : ai.Actions.ToList());
            }).ToList(), useAi && aiReplies.Count > 0, aiError));
        }).RequireAuthorization();
    }
}
