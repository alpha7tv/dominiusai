using System.Security.Claims;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Evolution;
using DominiusPlay.Infrastructure.Import;
using DominiusPlay.Infrastructure.Security;
using DominiusPlay.Server.Hubs;
using DominiusPlay.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Endpoints;

public static class Api
{
    public const string AdminPolicy = "admin";
    private static readonly System.Globalization.CultureInfo PtBr = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
    public const string SupervisorPolicy = "supervisor";

    public static void MapCrmApi(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok", version = typeof(Api).Assembly.GetName().Version?.ToString() }));

        // ---------- Autenticação ----------
        var auth = app.MapGroup("/api/auth");
        auth.MapGet("/setup", async (CrmDbContext db) => new SetupStatus(!await db.Users.AnyAsync()));
        auth.MapPost("/setup", async (SetupRequest r, CrmDbContext db, AuthService svc) =>
        {
            if (await db.Users.AnyAsync()) return Results.Conflict(new OperationResult(false, "Configuração inicial já realizada"));
            if (r.Password.Length < 8) return Results.BadRequest(new OperationResult(false, "A senha deve ter pelo menos 8 caracteres"));
            var u = new User { UserName = r.UserName.Trim().ToLowerInvariant(), DisplayName = r.DisplayName.Trim(), PasswordHash = PasswordHasher.Hash(r.Password), Role = UserRole.Administrador };
            db.Users.Add(u);
            db.AuditLogs.Add(new AuditLog { Actor = u.UserName, Action = "auth.setup", EntityType = "User", EntityId = u.Id.ToString() });
            await db.SaveChangesAsync();
            return Results.Ok(svc.Issue(u));
        });
        auth.MapPost("/login", async (LoginRequest r, CrmDbContext db, AuthService svc) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.UserName == r.UserName.Trim().ToLowerInvariant() && x.Active);
            if (u is null || !PasswordHasher.Verify(r.Password, u.PasswordHash)) return Results.Unauthorized();
            return Results.Ok(svc.Issue(u));
        });

        var api = app.MapGroup("/api").RequireAuthorization();

        // ---------- Leads ----------
        api.MapGet("/leads", async (CrmDbContext db, string? q, KanbanStage? stage, ConsentStatus? consent, int? take) =>
        {
            var query = db.Leads.Include(l => l.AssignedUser).AsNoTracking().AsQueryable();
            if (stage is not null) query = query.Where(l => l.Stage == stage);
            if (consent is not null) query = query.Where(l => l.ConsentStatus == consent);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var digits = new string(q.Where(char.IsAsciiDigit).ToArray());
                var lower = q.ToLower();
                query = query.Where(l => l.Name.ToLower().Contains(lower) || (digits.Length >= 4 && l.Phone.Contains(digits)) || l.Tags.Contains(lower));
            }
            var list = await query.OrderByDescending(l => l.CreatedAt).Take(Math.Clamp(take ?? 500, 1, 5000)).ToListAsync();
            return list.Select(l => l.ToCard());
        });

        api.MapGet("/leads/{id:guid}", async (Guid id, CrmDbContext db) =>
        {
            var l = await db.Leads.Include(x => x.AssignedUser).Include(x => x.Facts).Include(x => x.StageChanges).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound();
            var plans = await db.Plans.AsNoTracking().ToListAsync();
            var rec = PlanRecommender.Recommend(l, plans, DateTimeOffset.UtcNow);
            return Results.Ok(new LeadDetail(l.Id, l.Name, l.Phone, PhoneNormalizer.Format(l.Phone), l.City, l.State, l.Source, l.Notes, l.Tags,
                l.Experience, l.PanelUsed, l.ActiveClients, l.MainPain, l.SecondaryPains, l.InterestPlanCode, l.InvestmentRange, l.Stage, l.Mode,
                l.Priority, l.AssignedUserId, l.AssignedUser?.DisplayName, l.NextAction, l.LastInteractionAt, l.ConversationSummary,
                l.ConsentStatus, l.ConsentAt, l.ConsentSource, l.CreatedAt,
                l.Facts.OrderByDescending(f => f.CreatedAt).Select(f => new FactDto(f.Key, f.Value, f.Source, f.CreatedAt)).ToList(),
                l.StageChanges.OrderByDescending(s => s.CreatedAt).Select(s => new StageChangeDto(s.From, s.To, s.ChangedBy, s.Reason, s.CreatedAt)).ToList(),
                new RecommendationDto(rec.Profile.ToString(), rec.Plans.Select(p => $"{p.Name} — R$ {p.MonthlyPrice.ToString("N2", PtBr)}/mês").ToList(), rec.Rationale, rec.MissingInfo), l.SuggestedStage, l.SuggestedStageReason,
                l.Company, l.Segment, l.QualificationScore, l.QualificationExplanation, l.LastIntent, l.HandoffAt, l.HandoffSummary, l.AttendanceAgentId));
        });

        api.MapPost("/leads", async (LeadUpsert r, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var ph = PhoneNormalizer.Normalize(r.Phone);
            if (!ph.IsValid) return Results.BadRequest(new OperationResult(false, ph.Error ?? "Telefone inválido"));
            if (await db.Leads.AnyAsync(l => l.Phone == ph.Normalized)) return Results.Conflict(new OperationResult(false, "Já existe um lead com este WhatsApp"));
            var blocked = await db.OptOuts.AnyAsync(o => o.Phone == ph.Normalized);
            var l = new Lead { Phone = ph.Normalized, ImportedAt = DateTimeOffset.UtcNow, Source = r.Source ?? "Cadastro manual" };
            Apply(l, r);
            if (blocked) { l.ConsentStatus = ConsentStatus.Revogado; l.Stage = KanbanStage.NaoTemInteresse; ConversationStateMachine.Apply(l, ConversationTrigger.Bloquear); }
            db.Leads.Add(l);
            db.AuditLogs.Add(new AuditLog { Actor = user.Identity?.Name ?? "", Action = "lead.create", EntityType = "Lead", EntityId = l.Id.ToString() });
            await db.SaveChangesAsync();
            return Results.Ok(l.ToCard());
        });

        api.MapPut("/leads/{id:guid}", async (Guid id, LeadUpsert r, CrmDbContext db, ClaimsPrincipal user, IHubContext<CrmHub> hub) =>
        {
            var l = await db.Leads.FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound();
            var ph = PhoneNormalizer.Normalize(r.Phone);
            if (!ph.IsValid) return Results.BadRequest(new OperationResult(false, ph.Error ?? "Telefone inválido"));
            if (ph.Normalized != l.Phone && await db.Leads.AnyAsync(x => x.Phone == ph.Normalized)) return Results.Conflict(new OperationResult(false, "Já existe um lead com este WhatsApp"));
            l.Phone = ph.Normalized;
            Apply(l, r);
            l.UpdatedAt = DateTimeOffset.UtcNow;
            db.AuditLogs.Add(new AuditLog { Actor = user.Identity?.Name ?? "", Action = "lead.update", EntityType = "Lead", EntityId = l.Id.ToString() });
            await db.SaveChangesAsync();
            await hub.Clients.All.SendAsync(HubEvents.LeadChanged, id);
            return Results.Ok(new OperationResult(true, "Lead atualizado"));
        });

        api.MapPost("/leads/{id:guid}/consent", async (Guid id, ConsentRequest r, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var l = await db.Leads.FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(r.Source)) return Results.BadRequest(new OperationResult(false, "Informe a origem do consentimento"));
            if (await db.OptOuts.AnyAsync(o => o.Phone == l.Phone))
                return Results.BadRequest(new OperationResult(false, "Número está na lista de descadastro. Remova da lista somente com novo pedido expresso do contato."));
            var at = r.GrantedAt ?? DateTimeOffset.UtcNow;
            l.ConsentStatus = ConsentStatus.Valido; l.ConsentAt = at; l.ConsentSource = r.Source.Trim();
            db.ConsentRecords.Add(new ConsentRecord { LeadId = id, Status = ConsentStatus.Valido, Source = r.Source.Trim(), Evidence = r.Evidence, GrantedAt = at, RegisteredBy = user.Identity?.Name });
            db.AuditLogs.Add(new AuditLog { Actor = user.Identity?.Name ?? "", Action = "lead.consent", EntityType = "Lead", EntityId = id.ToString(), Details = r.Source });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, "Consentimento registrado"));
        });

        api.MapPost("/leads/{id:guid}/optout", async (Guid id, CrmDbContext db, ClaimsPrincipal user, IHubContext<CrmHub> hub) =>
        {
            var l = await db.Leads.FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound();
            LeadImportService.RevokeConsent(db, l, "Descadastro manual", user.Identity?.Name ?? "");
            if (!await db.OptOuts.AnyAsync(o => o.Phone == l.Phone)) db.OptOuts.Add(new OptOut { Phone = l.Phone, Reason = "Descadastro manual", Source = user.Identity?.Name ?? "" });
            await db.SaveChangesAsync();
            await hub.Clients.All.SendAsync(HubEvents.LeadChanged, id);
            return Results.Ok(new OperationResult(true, "Contato descadastrado"));
        });

        api.MapDelete("/leads/{id:guid}", async (Guid id, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var l = await db.Leads.FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound();
            db.Leads.Remove(l); // exclusão (LGPD) — remove mensagens, fatos e histórico em cascata
            db.AuditLogs.Add(new AuditLog { Actor = user.Identity?.Name ?? "", Action = "lead.delete", EntityType = "Lead", EntityId = id.ToString() });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, "Lead e dados associados excluídos"));
        }).RequireAuthorization(SupervisorPolicy);

        // ---------- Ações em lote (seleção na lista ou todos com uma tag) ----------
        api.MapPost("/leads/bulk", async (BulkLeadRequest r, CrmDbContext db, ClaimsPrincipal user, IHubContext<CrmHub> hub) =>
        {
            var actor = user.Identity?.Name ?? "";
            if (r.Action == BulkLeadAction.Excluir) return Results.BadRequest(new BulkLeadResponse(0, 0, "Use a exclusão em lote (supervisor)."));
            var (leads, error) = await LoadBulkTargets(db, r);
            if (error is not null) return Results.BadRequest(new BulkLeadResponse(0, 0, error));
            int affected = 0, skipped = 0;
            if (r.Action == BulkLeadAction.Autorizar)
            {
                if (string.IsNullOrWhiteSpace(r.ConsentSource))
                    return Results.BadRequest(new BulkLeadResponse(0, 0, "Informe como esses contatos autorizaram receber mensagens (origem do consentimento)."));
                var source = r.ConsentSource.Trim();
                var phones = leads.Select(l => l.Phone).ToList();
                var blocked = (await db.OptOuts.Where(o => phones.Contains(o.Phone)).Select(o => o.Phone).ToListAsync()).ToHashSet();
                var at = DateTimeOffset.UtcNow;
                foreach (var l in leads)
                {
                    if (blocked.Contains(l.Phone) || l.ConsentStatus == ConsentStatus.Revogado) { skipped++; continue; }
                    if (l.ConsentStatus == ConsentStatus.Valido) { skipped++; continue; }
                    l.ConsentStatus = ConsentStatus.Valido; l.ConsentAt = at; l.ConsentSource = source; l.UpdatedAt = at;
                    db.ConsentRecords.Add(new ConsentRecord { LeadId = l.Id, Status = ConsentStatus.Valido, Source = source, GrantedAt = at, RegisteredBy = actor });
                    affected++;
                }
                db.AuditLogs.Add(new AuditLog { Actor = actor, Action = "lead.consent.bulk", EntityType = "Lead", EntityId = "", Details = $"{affected} autorizados; origem: {source}" + (r.ByTag is null ? "" : $"; tag: {r.ByTag}") });
                await db.SaveChangesAsync();
                await hub.Clients.All.SendAsync(HubEvents.LeadChanged, Guid.Empty);
                var msg = $"{affected} leads autorizados";
                if (skipped > 0) msg += $" · {skipped} ignorados (já autorizados ou descadastrados)";
                return Results.Ok(new BulkLeadResponse(affected, skipped, msg));
            }
            // remover tag
            var tag = (r.Tag ?? "").Trim().ToLowerInvariant();
            if (tag.Length == 0) return Results.BadRequest(new BulkLeadResponse(0, 0, "Informe a tag a remover."));
            foreach (var l in leads)
            {
                var tags = SplitTags(l.Tags);
                if (!tags.Remove(tag)) { skipped++; continue; }
                l.Tags = string.Join(",", tags); l.UpdatedAt = DateTimeOffset.UtcNow;
                affected++;
            }
            db.AuditLogs.Add(new AuditLog { Actor = actor, Action = "lead.tag.remove.bulk", EntityType = "Lead", EntityId = "", Details = $"tag {tag} removida de {affected} leads" });
            await db.SaveChangesAsync();
            await hub.Clients.All.SendAsync(HubEvents.LeadChanged, Guid.Empty);
            return Results.Ok(new BulkLeadResponse(affected, skipped, $"Tag \"{tag}\" removida de {affected} leads" + (skipped > 0 ? $" · {skipped} não tinham a tag" : "")));
        });

        api.MapPost("/leads/bulk-delete", async (BulkLeadRequest r, CrmDbContext db, ClaimsPrincipal user, IHubContext<CrmHub> hub) =>
        {
            var (leads, error) = await LoadBulkTargets(db, r);
            if (error is not null) return Results.BadRequest(new BulkLeadResponse(0, 0, error));
            db.Leads.RemoveRange(leads); // exclusão (LGPD) — remove mensagens, fatos e histórico em cascata
            db.AuditLogs.Add(new AuditLog { Actor = user.Identity?.Name ?? "", Action = "lead.delete.bulk", EntityType = "Lead", EntityId = "", Details = $"{leads.Count} leads excluídos" });
            await db.SaveChangesAsync();
            await hub.Clients.All.SendAsync(HubEvents.LeadChanged, Guid.Empty);
            return Results.Ok(new BulkLeadResponse(leads.Count, 0, $"{leads.Count} leads excluídos com mensagens e histórico"));
        }).RequireAuthorization(SupervisorPolicy);

        // ---------- Importação ----------
        api.MapPost("/import/file", async (IFormFile file, [FromForm] string? source, [FromForm] string? tags, [FromForm] string? consentSource, LeadImportService svc, ClaimsPrincipal user) =>
        {
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            await using var s = file.OpenReadStream();
            List<RawLeadRow> rows;
            try
            {
                rows = ext switch
                {
                    ".csv" or ".txt" => LeadFileParser.ParseCsv(s),
                    ".xlsx" => LeadFileParser.ParseXlsx(s),
                    _ => throw new InvalidDataException("Formato não suportado. Use CSV ou XLSX."),
                };
            }
            catch (Exception ex) { return Results.BadRequest(new OperationResult(false, "Não foi possível ler o arquivo", ex.Message)); }
            if (rows.Count == 0) return Results.BadRequest(new OperationResult(false, "Nenhum número encontrado no arquivo. Use uma coluna com os números de WhatsApp (com DDD), um por linha."));
            var r = await svc.ImportAsync(rows, new(file.FileName, ext.TrimStart('.').ToUpperInvariant(), user.Identity?.Name ?? "", source, tags, BatchConsentSource: consentSource));
            return Results.Ok(ToResponse(r));
        }).DisableAntiforgery();

        api.MapPost("/import/paste", async (PasteImportRequest r, LeadImportService svc, ClaimsPrincipal user) =>
        {
            var rows = LeadFileParser.ParsePaste(r.Text);
            if (rows.Count == 0) return Results.BadRequest(new OperationResult(false, "Nada para importar"));
            var res = await svc.ImportAsync(rows, new("Copiar/colar", "COLAR", user.Identity?.Name ?? "", r.Source, r.Tags, BatchConsentSource: r.ConsentSource));
            return Results.Ok(ToResponse(res));
        });

        api.MapPost("/import/optouts", async (OptOutImportRequest r, LeadImportService svc, ClaimsPrincipal user) =>
        {
            var (added, invalid, affected) = await svc.ImportOptOutsAsync(r.Text.Split(['\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries), r.Reason, "Importação de lista de bloqueio", user.Identity?.Name ?? "");
            return Results.Ok(new OperationResult(true, $"{added} número(s) adicionados à lista de bloqueio; {invalid} inválido(s); {affected} lead(s) atualizados."));
        });

        // ---------- Kanban ----------
        api.MapGet("/kanban", async (CrmDbContext db) =>
        {
            var leads = await db.Leads.Include(l => l.AssignedUser).AsNoTracking().ToListAsync();
            return Enum.GetValues<KanbanStage>().Select(s => new KanbanColumn(s, KanbanRules.Title(s),
                leads.Where(l => l.Stage == s).OrderByDescending(l => l.Priority).ThenByDescending(l => l.LastInteractionAt ?? l.CreatedAt).Select(l => l.ToCard()).ToList()));
        });

        api.MapPost("/kanban/{id:guid}/move", async (Guid id, MoveCardRequest r, CrmDbContext db, ConversationLocks locks, ClaimsPrincipal user, IHubContext<CrmHub> hub) =>
        {
            using var _ = await locks.AcquireAsync(id);
            var l = await db.Leads.FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound();
            var d = KanbanRules.Evaluate(l, r.To, MoveActor.Atendente, r.CommercialConfirmation);
            if (!d.Applied) return Results.Ok(new MoveCardResponse(false, d.IsSuggestion, d.Reason));
            var change = KanbanRules.Apply(l, r.To, user.Identity?.Name ?? "", r.Reason);
            l.SuggestedStage = null; l.SuggestedStageReason = null;
            db.StageChanges.Add(change);
            if (r.To == KanbanStage.NaoTemInteresse && !string.IsNullOrWhiteSpace(r.Reason)) l.LossReason = r.Reason;
            l.ConcurrencyStamp = Guid.NewGuid();
            db.AuditLogs.Add(new AuditLog { Actor = user.Identity?.Name ?? "", Action = "kanban.move", EntityType = "Lead", EntityId = id.ToString(), Details = $"{change.From} → {change.To}" });
            await db.SaveChangesAsync();
            await hub.Clients.All.SendAsync(HubEvents.LeadChanged, id);
            return Results.Ok(new MoveCardResponse(true, false, $"Movido para {KanbanRules.Title(r.To)}"));
        });

        // ---------- Conversas ----------
        api.MapGet("/conversations", async (CrmDbContext db, string? q) =>
        {
            var query = db.Leads.Include(l => l.AssignedUser).AsNoTracking().Where(l => l.LastInteractionAt != null);
            if (!string.IsNullOrWhiteSpace(q)) { var lower = q.ToLower(); query = query.Where(l => l.Name.ToLower().Contains(lower) || l.Phone.Contains(q)); }
            var list = await query.ToListAsync();
            return list.OrderByDescending(l => l.LastInteractionAt).Take(300).Select(l => l.ToCard());
        });

        api.MapGet("/conversations/{id:guid}/messages", async (Guid id, CrmDbContext db) =>
        {
            var msgs = await db.Messages.AsNoTracking().Where(m => m.LeadId == id).ToListAsync();
            var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
            return msgs.OrderBy(m => m.Timestamp).ThenBy(m => m.CreatedAt).Select(m => m.ToDto(m.SentByUserId is { } u && users.TryGetValue(u, out var n) ? n : null));
        });

        api.MapPost("/conversations/{id:guid}/takeover", async (Guid id, ConversationService svc, AuthService a, ClaimsPrincipal p) =>
            await a.CurrentAsync(p) is { } u ? Results.Ok(await svc.TakeOverAsync(id, u)) : Results.Unauthorized());
        api.MapPost("/conversations/{id:guid}/return-to-ai", async (Guid id, ConversationService svc, AuthService a, ClaimsPrincipal p) =>
            await a.CurrentAsync(p) is { } u ? Results.Ok(await svc.ReturnToAiAsync(id, u)) : Results.Unauthorized());
        api.MapPost("/conversations/{id:guid}/pause", async (Guid id, bool pause, ConversationService svc, AuthService a, ClaimsPrincipal p) =>
            await a.CurrentAsync(p) is { } u ? Results.Ok(await svc.PauseAsync(id, u, pause)) : Results.Unauthorized());
        api.MapPost("/conversations/{id:guid}/send", async (Guid id, SendMessageRequest r, ConversationService svc, AuthService a, EvolutionGateway gw, ClaimsPrincipal p) =>
        {
            if (await a.CurrentAsync(p) is not { } u) return Results.Unauthorized();
            var cfg = await gw.GetConfigAsync();
            if (cfg is null) return Results.Ok(new SendMessageResponse(false, "Evolution API não configurada (Integrações).", null));
            return Results.Ok(await svc.EnqueueHumanMessageAsync(id, u, r.Text, cfg.InstanceName));
        });

        // ---------- Planos ----------
        api.MapGet("/plans", async (CrmDbContext db) =>
            (await db.Plans.AsNoTracking().ToListAsync()).OrderBy(p => p.SortOrder).ThenBy(p => p.Code).ThenByDescending(p => p.Version).Select(p => p.ToDto()));
        api.MapPost("/plans", async (PlanUpsert r, CrmDbContext db, ClaimsPrincipal user) =>
        {
            // Versionamento: nunca sobrescreve um plano aprovado; cria nova versão em rascunho
            var last = await db.Plans.Where(p => p.Code == r.Code).OrderByDescending(p => p.Version).FirstOrDefaultAsync();
            var p = new Plan { Code = r.Code.Trim().ToUpperInvariant(), Version = (last?.Version ?? 0) + 1, SortOrder = last?.SortOrder ?? 99 };
            Apply(p, r);
            db.Plans.Add(p);
            db.AuditLogs.Add(new AuditLog { Actor = user.Identity?.Name ?? "", Action = "plan.version", EntityType = "Plan", EntityId = p.Id.ToString(), Details = $"{p.Code} v{p.Version} R$ {p.MonthlyPrice}" });
            await db.SaveChangesAsync();
            return Results.Ok(p.ToDto());
        }).RequireAuthorization(AdminPolicy);
        api.MapPost("/plans/{id:guid}/approve", async (Guid id, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var p = await db.Plans.FindAsync(id);
            if (p is null) return Results.NotFound();
            foreach (var old in await db.Plans.Where(x => x.Code == p.Code && x.Id != id && x.Approval == ApprovalStatus.Aprovado).ToListAsync())
                old.Approval = ApprovalStatus.Arquivado;
            p.Approval = ApprovalStatus.Aprovado; p.ApprovedBy = user.Identity?.Name; p.UpdatedAt = DateTimeOffset.UtcNow;
            db.AuditLogs.Add(new AuditLog { Actor = user.Identity?.Name ?? "", Action = "plan.approve", EntityType = "Plan", EntityId = id.ToString(), Details = $"{p.Code} v{p.Version}" });
            await db.SaveChangesAsync();
            return Results.Ok(p.ToDto());
        }).RequireAuthorization(AdminPolicy);

        // ---------- Base de conhecimento e modelos ----------
        api.MapGet("/knowledge", async (CrmDbContext db) =>
            (await db.KnowledgeArticles.AsNoTracking().ToListAsync()).OrderBy(k => k.Category).Select(k => new KnowledgeDto(k.Id, k.Category, k.Title, k.Content, k.SourceUrl, k.Approval, k.Version)));

        // ---------- Dashboard ----------
        api.MapGet("/dashboard", async (CrmDbContext db) =>
        {
            var leads = await db.Leads.AsNoTracking().Select(l => new { l.Stage, l.ConsentStatus, l.Mode, l.MainPain, l.InterestPlanCode, l.LossReason, l.Id }).ToListAsync();
            var msgs = await db.Messages.AsNoTracking().Select(m => new { m.LeadId, m.Direction, m.Status, m.Author }).ToListAsync();
            var outbound = msgs.Where(m => m.Direction == MessageDirection.Saida && m.Author is MessageAuthor.Ia or MessageAuthor.Atendente or MessageAuthor.ManualNoAparelho).ToList();
            var contacted = outbound.Where(m => m.Status != MessageStatus.Falhou).Select(m => m.LeadId).ToHashSet();
            var replied = msgs.Where(m => m.Direction == MessageDirection.Entrada).Select(m => m.LeadId).ToHashSet();
            var takeovers = await db.AuditLogs.CountAsync(a => a.Action == "conversation.takeover" || a.Action == "conversation.manual-detected");
            return new DashboardDto(
                leads.Count, leads.Count(l => l.ConsentStatus == ConsentStatus.Valido), leads.Count(l => l.ConsentStatus == ConsentStatus.Pendente),
                contacted.Count, replied.Count,
                Enum.GetValues<KanbanStage>().ToDictionary(KanbanRules.Title, s => leads.Count(l => l.Stage == s)),
                takeovers,
                outbound.Count(m => m.Status is MessageStatus.EnviadaServidor or MessageStatus.Entregue or MessageStatus.Lida or MessageStatus.Pendente),
                outbound.Count(m => m.Status is MessageStatus.Entregue or MessageStatus.Lida),
                outbound.Count(m => m.Status == MessageStatus.Lida),
                msgs.Count(m => m.Status == MessageStatus.Falhou),
                await db.OutboundQueue.CountAsync(q => q.Status == MessageStatus.NaFila),
                leads.Where(l => l.MainPain != PainCategory.Nenhuma).GroupBy(l => PainClassifier.Title(l.MainPain)).OrderByDescending(g => g.Count()).Take(6).ToDictionary(g => g.Key, g => g.Count()),
                leads.Where(l => l.Stage == KanbanStage.NegocioFechado).GroupBy(l => l.InterestPlanCode ?? "Não informado").ToDictionary(g => g.Key, g => g.Count()),
                leads.Where(l => l.Stage == KanbanStage.NaoTemInteresse).GroupBy(l => l.LossReason ?? "Não informado").ToDictionary(g => g.Key, g => g.Count()));
        });

        // ---------- Integrações: Evolution API ----------
        var integ = api.MapGroup("/integrations/evolution");
        integ.MapGet("/", async (SettingsStore s) => new EvolutionSettingsDto(
            await s.GetAsync(SettingKeys.EvolutionUrl), await s.GetAsync(SettingKeys.EvolutionInstance), await s.HasAsync(SettingKeys.EvolutionApiKey),
            await s.GetAsync(SettingKeys.EvolutionIntegration), await s.GetAsync(SettingKeys.WebhookPublicUrl), "/webhooks/evolution", await s.HasAsync(SettingKeys.WebhookToken)));
        integ.MapPut("/", async (EvolutionSettingsUpdate r, SettingsStore s, CrmDbContext db, ClaimsPrincipal user) =>
        {
            if (!Uri.TryCreate(r.BaseUrl.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                return Results.BadRequest(new OperationResult(false, "URL do servidor inválida"));
            if (string.IsNullOrWhiteSpace(r.InstanceName)) return Results.BadRequest(new OperationResult(false, "Informe o nome da instância"));
            await s.SetAsync(SettingKeys.EvolutionUrl, uri.ToString().TrimEnd('/'));
            await s.SetAsync(SettingKeys.EvolutionInstance, r.InstanceName.Trim());
            if (!string.IsNullOrWhiteSpace(r.ApiKey)) await s.SetAsync(SettingKeys.EvolutionApiKey, r.ApiKey.Trim(), secret: true);
            if (r.IntegrationMode is not null) await s.SetAsync(SettingKeys.EvolutionIntegration, r.IntegrationMode);
            if (r.WebhookPublicUrl is not null) await s.SetAsync(SettingKeys.WebhookPublicUrl, r.WebhookPublicUrl.Trim().TrimEnd('/'));
            if (!await s.HasAsync(SettingKeys.WebhookToken)) await s.SetAsync(SettingKeys.WebhookToken, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)), secret: true);
            db.AuditLogs.Add(new AuditLog { Actor = user.Identity?.Name ?? "", Action = "integration.evolution.update", EntityType = "Setting", Details = $"{uri.Host} / {r.InstanceName}" + (string.IsNullOrWhiteSpace(r.ApiKey) ? "" : " (chave alterada)") });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, "Configuração salva"));
        }).RequireAuthorization(AdminPolicy);
        integ.MapGet("/status", async (EvolutionGateway gw, IEvolutionApi evo, InstanceStatusCache cache) =>
        {
            var cfg = await gw.GetConfigAsync();
            if (cfg is null) return new EvolutionStatusDto(false, false, null, "Não configurado", DateTimeOffset.UtcNow);
            var st = await evo.GetConnectionStateAsync(cfg);
            cache.State = st.Reachable ? st.State : "unreachable"; cache.Error = st.Error; cache.CheckedAt = DateTimeOffset.UtcNow;
            return new EvolutionStatusDto(true, st.Reachable, st.State, st.Error, cache.CheckedAt);
        });
        integ.MapPost("/webhook", async (EvolutionGateway gw, IEvolutionApi evo, SettingsStore s) =>
        {
            var cfg = await gw.GetConfigAsync();
            if (cfg is null) return Results.Ok(new OperationResult(false, "Configure URL, instância e chave primeiro"));
            var publicUrl = await s.GetAsync(SettingKeys.WebhookPublicUrl);
            if (string.IsNullOrWhiteSpace(publicUrl)) return Results.Ok(new OperationResult(false, "Informe a URL pública deste servidor CRM (acessível pela VPS da Evolution)"));
            var token = await s.GetAsync(SettingKeys.WebhookToken) ?? "";
            var (ok, body) = await evo.SetWebhookAsync(cfg, publicUrl + "/webhooks/evolution", new Dictionary<string, string> { ["x-dominius-token"] = token }, EvolutionApiClient.DefaultWebhookEvents);
            return Results.Ok(new OperationResult(ok, ok ? "Webhook registrado na Evolution API" : "A Evolution API recusou a configuração", body));
        }).RequireAuthorization(AdminPolicy);
        integ.MapGet("/webhook", async (EvolutionGateway gw, IEvolutionApi evo) =>
        {
            var cfg = await gw.GetConfigAsync();
            if (cfg is null) return new OperationResult(false, "Não configurado");
            var (ok, body) = await evo.FindWebhookAsync(cfg);
            return new OperationResult(ok, ok ? "Webhook atual" : "Falha ao consultar", body);
        });
        integ.MapPost("/test-message", async (TestMessageRequest r, EvolutionGateway gw, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var cfg = await gw.GetConfigAsync();
            if (cfg is null) return Results.Ok(new OperationResult(false, "Evolution API não configurada"));
            var ph = PhoneNormalizer.Normalize(r.Phone);
            if (!ph.IsValid) return Results.Ok(new OperationResult(false, ph.Error ?? "Telefone inválido"));
            var lead = await db.Leads.FirstOrDefaultAsync(l => l.Phone == ph.Normalized);
            if (lead is null) { lead = new Lead { Name = "Teste interno", Phone = ph.Normalized, Source = "Teste de integração", Tags = "teste" }; db.Leads.Add(lead); }
            var uid = Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : (Guid?)null;
            var item = new OutboundQueueItem { LeadId = lead.Id, Kind = OutboundKind.Teste, Text = r.Text, InstanceName = cfg.InstanceName, RequestedByUserId = uid };
            db.OutboundQueue.Add(item);
            db.AuditLogs.Add(new AuditLog { Actor = user.Identity?.Name ?? "", Action = "integration.evolution.test-message", EntityType = "Lead", EntityId = lead.Id.ToString() });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, "Mensagem de teste colocada na fila (envio em segundos). Use somente o seu próprio número."));
        }).RequireAuthorization(AdminPolicy);
        integ.MapGet("/events", async (CrmDbContext db) =>
            (await db.WebhookEvents.AsNoTracking().ToListAsync()).OrderByDescending(e => e.CreatedAt).Take(200)
                .Select(e => new WebhookEventDto(e.CreatedAt, e.EventType, e.InstanceName, e.Processed, e.Error)));

        // ---------- Webhook público (chamado pela Evolution API) ----------
        app.MapPost("/webhooks/evolution", async (HttpRequest req, SettingsStore s, InboundProcessor proc) =>
        {
            var expected = await s.GetAsync(SettingKeys.WebhookToken);
            var got = req.Headers["x-dominius-token"].ToString();
            if (string.IsNullOrEmpty(expected) || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(got)))
                return Results.Unauthorized();
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            if (body.Length > 2_000_000) return Results.StatusCode(413);
            var outcome = await proc.ProcessAsync(body);
            return Results.Ok(new { outcome = outcome.ToString() });
        }).RequireRateLimiting("webhook");
    }

    private static ImportResponse ToResponse(ImportResult r) => new(r.BatchId, r.Total, r.Created, r.Updated, r.Duplicates, r.Invalid, r.Blocked, r.WithConsent,
        r.Rows.Where(x => x.Outcome is ImportRowOutcome.Invalid or ImportRowOutcome.DuplicateInFile or ImportRowOutcome.Blocked)
            .Take(500).Select(x => new ImportRowDto(x.Line, x.RawPhone, x.Outcome.ToString(), x.Message)).ToList());

    private static List<string> SplitTags(string? tags) =>
        (tags ?? "").Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()).Distinct().ToList();

    /// <summary>Leads alvo de uma ação em lote: por tag (todos que têm a tag exata) ou pela lista de ids.</summary>
    private static async Task<(List<Lead> Leads, string? Error)> LoadBulkTargets(CrmDbContext db, BulkLeadRequest r)
    {
        if (!string.IsNullOrWhiteSpace(r.ByTag))
        {
            var tag = r.ByTag.Trim().ToLowerInvariant();
            var candidates = await db.Leads.Where(l => l.Tags.Contains(tag)).ToListAsync();
            var byTag = candidates.Where(l => SplitTags(l.Tags).Contains(tag)).ToList();
            return byTag.Count == 0 ? (byTag, $"Nenhum lead com a tag \"{tag}\".") : (byTag, null);
        }
        var ids = (r.Ids ?? []).Distinct().ToList();
        if (ids.Count == 0) return ([], "Selecione pelo menos um lead.");
        var leads = new List<Lead>(ids.Count);
        foreach (var chunk in ids.Chunk(500)) leads.AddRange(await db.Leads.Where(l => chunk.Contains(l.Id)).ToListAsync());
        return (leads, null);
    }

    private static void Apply(Lead l, LeadUpsert r)
    {
        l.Name = (r.Name ?? "").Trim(); // nome é opcional: sem nome, o lead aparece pelo número l.City = r.City; l.State = r.State?.ToUpperInvariant(); l.Source = r.Source ?? l.Source; l.Notes = r.Notes;
        l.Tags = LeadImportService.MergeTags(r.Tags); l.Experience = r.Experience; l.PanelUsed = r.PanelUsed; l.ActiveClients = r.ActiveClients;
        l.MainPain = r.MainPain; l.InterestPlanCode = r.InterestPlanCode; l.InvestmentRange = r.InvestmentRange; l.Priority = r.Priority; l.NextAction = r.NextAction;
    }

    private static void Apply(Plan p, PlanUpsert r)
    {
        p.Name = r.Name; p.MonthlyPrice = r.MonthlyPrice; p.Credits = r.Credits; p.MaxActiveLogins = r.MaxActiveLogins; p.Description = r.Description;
        p.Benefits = r.Benefits; p.Limits = r.Limits; p.TechnicalRequirements = r.TechnicalRequirements; p.TargetProfile = r.TargetProfile;
        p.ValidFrom = r.ValidFrom; p.ValidUntil = r.ValidUntil; p.Approval = ApprovalStatus.Rascunho; p.IsCampaignPrice = true;
    }
}
