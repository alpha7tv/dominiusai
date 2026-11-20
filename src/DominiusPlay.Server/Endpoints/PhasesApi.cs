using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Security;
using DominiusPlay.Server.Hubs;
using DominiusPlay.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Endpoints;

/// <summary>Diretório de dados do servidor (banco, chaves, backups).</summary>
public sealed record DataPaths(string Root)
{
    public string Backups => Path.Combine(Root, "backups");
}

/// <summary>Fases 6 a 9: qualificação, equipe, campanhas/modelos, relatórios, LGPD e manutenção.</summary>
public static class PhasesApi
{
    private static string Actor(ClaimsPrincipal u) => u.Identity?.Name ?? "";

    public static void MapPhasesApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        // ================= Fase 6: qualificação, fatos e sugestões da IA =================
        api.MapPut("/leads/{id:guid}/qualification", async (Guid id, QualificationUpdate r, CrmDbContext db, ClaimsPrincipal user, IHubContext<CrmHub> hub) =>
        {
            var l = await db.Leads.FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound();
            if (r.ActiveClients is < 0) return Results.BadRequest(new OperationResult(false, "Clientes ativos não pode ser negativo"));
            if (r.InterestPlanCode is { Length: > 0 } code && !await db.Plans.AnyAsync(p => p.Code == code))
                return Results.BadRequest(new OperationResult(false, $"Plano '{code}' não existe"));
            l.Experience = r.Experience; l.PanelUsed = Trim(r.PanelUsed); l.ActiveClients = r.ActiveClients; l.MainPain = r.MainPain;
            l.InterestPlanCode = Trim(r.InterestPlanCode); l.InvestmentRange = Trim(r.InvestmentRange); l.Priority = r.Priority;
            l.NextAction = Trim(r.NextAction);
            if (r.Summary is not null) l.ConversationSummary = Trim(r.Summary);
            l.UpdatedAt = DateTimeOffset.UtcNow; l.ConcurrencyStamp = Guid.NewGuid();
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "lead.qualification", EntityType = "Lead", EntityId = id.ToString() });
            await db.SaveChangesAsync();
            await hub.Clients.All.SendAsync(HubEvents.LeadChanged, id);
            return Results.Ok(new OperationResult(true, "Qualificação salva"));
        });

        api.MapPost("/leads/{id:guid}/facts", async (Guid id, FactCreate r, CrmDbContext db, ClaimsPrincipal user) =>
        {
            if (!await db.Leads.AnyAsync(x => x.Id == id)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(r.Key) || string.IsNullOrWhiteSpace(r.Value))
                return Results.BadRequest(new OperationResult(false, "Informe o assunto e o conteúdo"));
            db.LeadFacts.Add(new LeadFact { LeadId = id, Key = r.Key.Trim().ToLowerInvariant().Replace(' ', '_'), Value = r.Value.Trim(), Source = FactSource.RegistradoPorAtendente });
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "lead.fact", EntityType = "Lead", EntityId = id.ToString(), Details = r.Key });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, "Informação registrada"));
        });

        api.MapPost("/leads/{id:guid}/suggestion/accept", async (Guid id, bool? confirm, CrmDbContext db, ConversationLocks locks, ClaimsPrincipal user, IHubContext<CrmHub> hub) =>
        {
            using var _ = await locks.AcquireAsync(id);
            var l = await db.Leads.FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound();
            if (l.SuggestedStage is not { } to) return Results.Ok(new MoveCardResponse(false, false, "Não há sugestão pendente"));
            var d = KanbanRules.Evaluate(l, to, MoveActor.Atendente, confirm == true);
            if (!d.Applied)
            {
                if (l.Stage == to) { l.SuggestedStage = null; l.SuggestedStageReason = null; await db.SaveChangesAsync(); }
                return Results.Ok(new MoveCardResponse(false, d.IsSuggestion, d.Reason));
            }
            var change = KanbanRules.Apply(l, to, $"{Actor(user)} (sugestão da IA aprovada)", l.SuggestedStageReason);
            db.StageChanges.Add(change);
            if (to == KanbanStage.NaoTemInteresse) l.LossReason ??= l.SuggestedStageReason;
            l.SuggestedStage = null; l.SuggestedStageReason = null; l.ConcurrencyStamp = Guid.NewGuid();
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "kanban.suggestion-accept", EntityType = "Lead", EntityId = id.ToString(), Details = $"{change.From} → {change.To}" });
            await db.SaveChangesAsync();
            await hub.Clients.All.SendAsync(HubEvents.LeadChanged, id);
            return Results.Ok(new MoveCardResponse(true, false, $"Movido para {KanbanRules.Title(to)}"));
        });

        api.MapPost("/leads/{id:guid}/suggestion/reject", async (Guid id, CrmDbContext db, ClaimsPrincipal user, IHubContext<CrmHub> hub) =>
        {
            var l = await db.Leads.FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound();
            if (l.SuggestedStage is null) return Results.Ok(new OperationResult(false, "Não há sugestão pendente"));
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "kanban.suggestion-reject", EntityType = "Lead", EntityId = id.ToString(), Details = l.SuggestedStage.ToString() });
            l.SuggestedStage = null; l.SuggestedStageReason = null;
            await db.SaveChangesAsync();
            await hub.Clients.All.SendAsync(HubEvents.LeadChanged, id);
            return Results.Ok(new OperationResult(true, "Sugestão descartada"));
        });

        // ================= Fase 7: equipe =================
        api.MapGet("/me", async (AuthService a, ClaimsPrincipal p) =>
            await a.CurrentAsync(p) is { } u ? Results.Ok(new UserDto(u.Id, u.UserName, u.DisplayName, u.Role, u.Active, 0)) : Results.Unauthorized());

        api.MapPost("/me/password", async (ChangePasswordRequest r, AuthService a, CrmDbContext db, ClaimsPrincipal p) =>
        {
            if (await a.CurrentAsync(p) is not { } u) return Results.Unauthorized();
            if (!PasswordHasher.Verify(r.Current, u.PasswordHash)) return Results.BadRequest(new OperationResult(false, "Senha atual incorreta"));
            if (r.NewPassword.Length < 8) return Results.BadRequest(new OperationResult(false, "A nova senha deve ter pelo menos 8 caracteres"));
            u.PasswordHash = PasswordHasher.Hash(r.NewPassword); u.UpdatedAt = DateTimeOffset.UtcNow;
            db.AuditLogs.Add(new AuditLog { Actor = u.UserName, Action = "user.password", EntityType = "User", EntityId = u.Id.ToString() });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, "Senha alterada"));
        });

        api.MapGet("/users", async (CrmDbContext db) =>
        {
            var users = await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync();
            var open = await db.Leads.AsNoTracking().Where(l => l.AssignedUserId != null && l.Mode == ConversationMode.AtendimentoHumano)
                .GroupBy(l => l.AssignedUserId).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
            return users.Select(u => new UserDto(u.Id, u.UserName, u.DisplayName, u.Role, u.Active, open.FirstOrDefault(o => o.Key == u.Id)?.N ?? 0));
        });

        api.MapPost("/users", async (UserCreate r, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var name = r.UserName.Trim().ToLowerInvariant();
            if (name.Length < 3 || name.Any(char.IsWhiteSpace)) return Results.BadRequest(new OperationResult(false, "Usuário deve ter ao menos 3 caracteres e sem espaços"));
            if (string.IsNullOrWhiteSpace(r.DisplayName)) return Results.BadRequest(new OperationResult(false, "Informe o nome de exibição"));
            if (r.Password.Length < 8) return Results.BadRequest(new OperationResult(false, "A senha deve ter pelo menos 8 caracteres"));
            if (await db.Users.AnyAsync(u => u.UserName == name)) return Results.Conflict(new OperationResult(false, "Já existe um usuário com este login"));
            var u = new User { UserName = name, DisplayName = r.DisplayName.Trim(), PasswordHash = PasswordHasher.Hash(r.Password), Role = r.Role };
            db.Users.Add(u);
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "user.create", EntityType = "User", EntityId = u.Id.ToString(), Details = $"{name} ({r.Role})" });
            await db.SaveChangesAsync();
            return Results.Ok(new UserDto(u.Id, u.UserName, u.DisplayName, u.Role, u.Active, 0));
        }).RequireAuthorization(Api.AdminPolicy);

        api.MapPut("/users/{id:guid}", async (Guid id, UserUpdate r, CrmDbContext db, AuthService a, ClaimsPrincipal p) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id);
            if (u is null) return Results.NotFound();
            var me = await a.CurrentAsync(p);
            if (me?.Id == id && (!r.Active || r.Role != UserRole.Administrador))
                return Results.BadRequest(new OperationResult(false, "Você não pode desativar nem rebaixar o próprio usuário"));
            var losesAdmin = u.Role == UserRole.Administrador && u.Active && (r.Role != UserRole.Administrador || !r.Active);
            if (losesAdmin && await db.Users.CountAsync(x => x.Role == UserRole.Administrador && x.Active) <= 1)
                return Results.BadRequest(new OperationResult(false, "É preciso manter ao menos um administrador ativo"));
            if (string.IsNullOrWhiteSpace(r.DisplayName)) return Results.BadRequest(new OperationResult(false, "Informe o nome de exibição"));
            if (r.NewPassword is { Length: > 0 and < 8 }) return Results.BadRequest(new OperationResult(false, "A senha deve ter pelo menos 8 caracteres"));
            u.DisplayName = r.DisplayName.Trim(); u.Role = r.Role; u.Active = r.Active; u.UpdatedAt = DateTimeOffset.UtcNow;
            if (r.NewPassword is { Length: >= 8 }) u.PasswordHash = PasswordHasher.Hash(r.NewPassword);
            var released = 0;
            if (!r.Active)
            {
                // Usuário desativado: conversas dele ficam sem responsável (continuam em atendimento humano — IA não volta sozinha)
                var leads = await db.Leads.Where(l => l.AssignedUserId == id).ToListAsync();
                foreach (var l in leads) l.AssignedUserId = null;
                released = leads.Count;
            }
            db.AuditLogs.Add(new AuditLog { Actor = me?.UserName ?? "", Action = "user.update", EntityType = "User", EntityId = id.ToString(),
                Details = $"{u.Role}, ativo={u.Active}{(r.NewPassword is { Length: >= 8 } ? ", senha redefinida" : "")}" });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, released > 0 ? $"Usuário atualizado. {released} conversa(s) ficaram sem responsável." : "Usuário atualizado"));
        }).RequireAuthorization(Api.AdminPolicy);

        api.MapPost("/conversations/{id:guid}/assign", async (Guid id, AssignRequest r, CrmDbContext db, ConversationLocks locks, ClaimsPrincipal user, IHubContext<CrmHub> hub) =>
        {
            using var _ = await locks.AcquireAsync(id);
            var l = await db.Leads.FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound();
            User? target = null;
            if (r.UserId is { } uid)
            {
                target = await db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.Active);
                if (target is null) return Results.BadRequest(new OperationResult(false, "Atendente não encontrado ou inativo"));
            }
            l.AssignedUserId = target?.Id; l.ConcurrencyStamp = Guid.NewGuid(); l.UpdatedAt = DateTimeOffset.UtcNow;
            db.Messages.Add(new Message { LeadId = id, Direction = MessageDirection.Saida, Author = MessageAuthor.Sistema, Status = MessageStatus.Recebida,
                Text = target is null ? $"Conversa ficou sem responsável ({Actor(user)})." : $"Conversa atribuída a {target.DisplayName} por {Actor(user)}." });
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "conversation.assign", EntityType = "Lead", EntityId = id.ToString(), Details = target?.UserName ?? "(nenhum)" });
            await db.SaveChangesAsync();
            await hub.Clients.All.SendAsync(HubEvents.LeadChanged, id);
            return Results.Ok(new OperationResult(true, target is null ? "Responsável removido" : $"Atribuída a {target.DisplayName}"));
        }).RequireAuthorization(Api.SupervisorPolicy);

        // ================= Fase 8: modelos de mensagem =================
        api.MapGet("/templates", async (CrmDbContext db) =>
        {
            var ts = await db.MessageTemplates.Include(t => t.Versions).AsNoTracking().ToListAsync();
            var all = ts.SelectMany(t => t.Versions.Select(v => (t, v))).ToList();
            return all.OrderBy(x => x.t.Name).ThenByDescending(x => x.v.Version).Select(x =>
            {
                var similar = all.Where(o => o.v.Id != x.v.Id && o.v.Approval != ApprovalStatus.Arquivado && x.v.Approval != ApprovalStatus.Arquivado)
                    .Select(o => (o, sim: TemplateRenderer.Similarity(x.v.Body, o.v.Body))).Where(s => s.sim >= TemplateRenderer.SimilarityWarning)
                    .OrderByDescending(s => s.sim).Select(s => $"{s.o.t.Name} v{s.o.v.Version} ({s.sim:P0})").FirstOrDefault();
                return new TemplateDto(x.t.Id, x.t.Name, x.t.Purpose, x.v.Version, x.v.Body, x.v.Approval, x.v.TimesUsed, x.v.Id, x.v.ChannelTemplateName, similar);
            });
        });

        api.MapPost("/templates", async (TemplateCreate r, CrmDbContext db, ClaimsPrincipal user) =>
        {
            if (ValidateBody(r.Body) is { } err) return Results.BadRequest(new OperationResult(false, err));
            if (string.IsNullOrWhiteSpace(r.Name)) return Results.BadRequest(new OperationResult(false, "Informe o nome do modelo"));
            var t = new MessageTemplate { Name = r.Name.Trim(), Purpose = string.IsNullOrWhiteSpace(r.Purpose) ? "Abordagem inicial" : r.Purpose.Trim() };
            var v = new MessageTemplateVersion { TemplateId = t.Id, Version = 1, Body = r.Body.Trim(), Approval = ApprovalStatus.EmRevisao };
            db.MessageTemplates.Add(t); db.MessageTemplateVersions.Add(v);
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "template.create", EntityType = "Template", EntityId = t.Id.ToString(), Details = t.Name });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, "Modelo criado em revisão. Um administrador precisa aprovar antes do uso em campanhas.", v.Id.ToString()));
        }).RequireAuthorization(Api.SupervisorPolicy);

        api.MapPost("/templates/{id:guid}/versions", async (Guid id, TemplateVersionCreate r, CrmDbContext db, ClaimsPrincipal user) =>
        {
            if (!await db.MessageTemplates.AnyAsync(t => t.Id == id)) return Results.NotFound();
            if (ValidateBody(r.Body) is { } err) return Results.BadRequest(new OperationResult(false, err));
            var last = await db.MessageTemplateVersions.Where(v => v.TemplateId == id).MaxAsync(v => (int?)v.Version) ?? 0;
            var v = new MessageTemplateVersion { TemplateId = id, Version = last + 1, Body = r.Body.Trim(), Approval = ApprovalStatus.EmRevisao };
            db.MessageTemplateVersions.Add(v);
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "template.version", EntityType = "Template", EntityId = id.ToString(), Details = $"v{v.Version}" });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, $"Versão {v.Version} criada em revisão", v.Id.ToString()));
        }).RequireAuthorization(Api.SupervisorPolicy);

        api.MapPost("/templates/versions/{vid:guid}/approve", async (Guid vid, TemplateApprove r, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var v = await db.MessageTemplateVersions.FirstOrDefaultAsync(x => x.Id == vid);
            if (v is null) return Results.NotFound();
            if (ValidateBody(v.Body) is { } err) return Results.BadRequest(new OperationResult(false, err));
            v.Approval = ApprovalStatus.Aprovado; v.ApprovedBy = Actor(user); v.UpdatedAt = DateTimeOffset.UtcNow;
            v.ChannelTemplateName = string.IsNullOrWhiteSpace(r.ChannelTemplateName) ? null : r.ChannelTemplateName.Trim();
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "template.approve", EntityType = "TemplateVersion", EntityId = vid.ToString(), Details = v.ChannelTemplateName });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, "Versão aprovada"));
        }).RequireAuthorization(Api.AdminPolicy);

        api.MapPost("/templates/versions/{vid:guid}/archive", async (Guid vid, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var v = await db.MessageTemplateVersions.FirstOrDefaultAsync(x => x.Id == vid);
            if (v is null) return Results.NotFound();
            v.Approval = ApprovalStatus.Arquivado; v.UpdatedAt = DateTimeOffset.UtcNow;
            // Itens ainda na fila com esta versão deixam de ter modelo aprovado → o SendGuard bloqueia fora da janela de 24h
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "template.archive", EntityType = "TemplateVersion", EntityId = vid.ToString() });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, "Versão arquivada (não será mais usada)"));
        }).RequireAuthorization(Api.AdminPolicy);

        api.MapPost("/templates/preview", async (TemplatePreviewRequest r, CrmDbContext db, SettingsStore s) =>
        {
            var assistant = await s.GetAsync(SettingKeys.AssistantName) is { Length: > 0 } n ? n : "Assistente Dominius Play";
            var name = r.LeadId is { } lid ? await db.Leads.Where(l => l.Id == lid).Select(l => l.Name).FirstOrDefaultAsync() : "Carlos";
            var bodies = await db.MessageTemplateVersions.AsNoTracking().Where(v => v.Approval != ApprovalStatus.Arquivado)
                .Join(db.MessageTemplates, v => v.TemplateId, t => t.Id, (v, t) => new { t.Name, v.Version, v.Body }).ToListAsync();
            var similar = bodies.Select(b => (b, sim: TemplateRenderer.Similarity(r.Body, b.Body)))
                .Where(x => x.sim >= TemplateRenderer.SimilarityWarning && x.b.Body.Trim() != r.Body.Trim())
                .OrderByDescending(x => x.sim).Select(x => $"{x.b.Name} v{x.b.Version} ({x.sim:P0})").FirstOrDefault();
            var unknown = TemplateRenderer.UnknownVariables(r.Body);
            var text = TemplateRenderer.Render(r.Body, name, assistant);
            if (unknown.Count > 0) text += $"\n\n⚠ Variáveis desconhecidas: {string.Join(", ", unknown)}";
            return new TemplatePreview(text, similar);
        });

        // ================= Fase 8: campanhas =================
        api.MapGet("/campaigns", async (CampaignService svc) => await svc.ListAsync());

        api.MapPost("/campaigns", async (CampaignUpsert r, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var c = new Campaign();
            if (CampaignService.Apply(c, r) is { } err) return Results.BadRequest(new OperationResult(false, err));
            db.Campaigns.Add(c);
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "campaign.create", EntityType = "Campaign", EntityId = c.Id.ToString(), Details = c.Name });
            await db.SaveChangesAsync();
            return Results.Ok(CampaignService.ToDto(c));
        }).RequireAuthorization(Api.SupervisorPolicy);

        api.MapPut("/campaigns/{id:guid}", async (Guid id, CampaignUpsert r, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == id);
            if (c is null) return Results.NotFound();
            if (c.Status is not (CampaignStatus.Rascunho or CampaignStatus.Pausada))
                return Results.BadRequest(new OperationResult(false, "Só é possível editar campanhas em rascunho ou pausadas"));
            if (c.Status == CampaignStatus.Pausada && r.TemplateId != c.TemplateId)
                return Results.BadRequest(new OperationResult(false, "O modelo não pode ser trocado depois de iniciada. Cancele e crie outra campanha."));
            if (CampaignService.Apply(c, r) is { } err) return Results.BadRequest(new OperationResult(false, err));
            c.UpdatedAt = DateTimeOffset.UtcNow;
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "campaign.update", EntityType = "Campaign", EntityId = id.ToString() });
            await db.SaveChangesAsync();
            return Results.Ok(CampaignService.ToDto(c));
        }).RequireAuthorization(Api.SupervisorPolicy);

        api.MapPost("/campaigns/{id:guid}/start", async (Guid id, CampaignService svc, ClaimsPrincipal user) => Results.Ok(await svc.StartAsync(id, Actor(user))))
            .RequireAuthorization(Api.SupervisorPolicy);
        api.MapPost("/campaigns/{id:guid}/pause", async (Guid id, CampaignService svc, ClaimsPrincipal user) => Results.Ok(await svc.SetStatusAsync(id, CampaignStatus.Pausada, Actor(user))))
            .RequireAuthorization(Api.SupervisorPolicy);
        api.MapPost("/campaigns/{id:guid}/resume", async (Guid id, CampaignService svc, ClaimsPrincipal user) => Results.Ok(await svc.SetStatusAsync(id, CampaignStatus.Ativa, Actor(user))))
            .RequireAuthorization(Api.SupervisorPolicy);
        api.MapPost("/campaigns/{id:guid}/cancel", async (Guid id, CampaignService svc, ClaimsPrincipal user) => Results.Ok(await svc.SetStatusAsync(id, CampaignStatus.Cancelada, Actor(user))))
            .RequireAuthorization(Api.SupervisorPolicy);

        // ================= Fase 9: relatórios, exportações, LGPD e manutenção =================
        api.MapGet("/reports", async (DateTimeOffset? from, DateTimeOffset? to, CrmDbContext db) =>
            await BuildReportAsync(db, from ?? DateTimeOffset.UtcNow.AddDays(-30), to ?? DateTimeOffset.UtcNow))
            .RequireAuthorization(Api.SupervisorPolicy);

        api.MapGet("/export/leads.csv", async (CrmDbContext db, ClaimsPrincipal user) =>
        {
            var leads = await db.Leads.Include(l => l.AssignedUser).AsNoTracking().OrderBy(l => l.CreatedAt).ToListAsync();
            var sb = new StringBuilder();
            Row(sb, "Nome", "WhatsApp", "Cidade", "UF", "Origem", "Tags", "Etapa", "Modo", "Consentimento", "Consentimento em", "Origem do consentimento",
                "Plano de interesse", "Clientes ativos", "Dor principal", "Prioridade", "Responsável", "Próxima ação", "Última interação", "Criado em");
            foreach (var l in leads)
                Row(sb, l.Name, l.Phone, l.City, l.State, l.Source, l.Tags, KanbanRules.Title(l.Stage), KanbanRules.ModeTitle(l.Mode),
                    l.ConsentStatus.ToString(), D(l.ConsentAt), l.ConsentSource, l.InterestPlanCode, l.ActiveClients?.ToString(), PainClassifier.Title(l.MainPain),
                    l.Priority.ToString(), l.AssignedUser?.DisplayName, l.NextAction, D(l.LastInteractionAt), D(l.CreatedAt));
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "export.leads", EntityType = "Lead", Details = $"{leads.Count} linhas" });
            await db.SaveChangesAsync();
            return Csv(sb, $"leads-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        }).RequireAuthorization(Api.SupervisorPolicy);

        api.MapGet("/export/messages.csv", async (DateTimeOffset? from, DateTimeOffset? to, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var f = from ?? DateTimeOffset.UtcNow.AddDays(-30); var t = to ?? DateTimeOffset.UtcNow;
            var leads = await db.Leads.AsNoTracking().ToDictionaryAsync(l => l.Id, l => (l.Name, l.Phone));
            var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
            var msgs = (await db.Messages.AsNoTracking().ToListAsync()).Where(m => m.Timestamp >= f && m.Timestamp <= t).OrderBy(m => m.Timestamp).ToList();
            var sb = new StringBuilder();
            Row(sb, "Data/hora", "Contato", "WhatsApp", "Direção", "Autor", "Atendente", "Status", "Texto", "Erro");
            foreach (var m in msgs)
            {
                var (name, phone) = leads.TryGetValue(m.LeadId, out var x) ? x : ("", "");
                Row(sb, D(m.Timestamp), name, phone, m.Direction.ToString(), m.Author.ToString(),
                    m.SentByUserId is { } u && users.TryGetValue(u, out var n) ? n : null, m.Status.ToString(), m.Text, m.Error);
            }
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "export.messages", EntityType = "Message", Details = $"{msgs.Count} linhas" });
            await db.SaveChangesAsync();
            return Csv(sb, $"mensagens-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        }).RequireAuthorization(Api.SupervisorPolicy);

        // LGPD: direito de acesso/portabilidade — todos os dados de um titular em JSON
        api.MapGet("/leads/{id:guid}/export", async (Guid id, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var l = await db.Leads.AsNoTracking().Include(x => x.ConsentRecords).Include(x => x.Messages).Include(x => x.Facts).Include(x => x.StageChanges)
                .FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound();
            var optOut = await db.OptOuts.AsNoTracking().Where(o => o.Phone == l.Phone).Select(o => new { o.Reason, o.Source, o.CreatedAt }).FirstOrDefaultAsync();
            var data = new
            {
                geradoEm = DateTimeOffset.UtcNow,
                titular = new { l.Name, WhatsApp = l.Phone, l.City, l.State, l.Source, l.Tags, l.Notes, l.CreatedAt },
                qualificacao = new { l.Experience, l.PanelUsed, l.ActiveClients, l.MainPain, l.SecondaryPains, l.InterestPlanCode, l.InvestmentRange, l.ConversationSummary },
                crm = new { Etapa = KanbanRules.Title(l.Stage), Modo = KanbanRules.ModeTitle(l.Mode), l.LossReason, l.LastInteractionAt },
                consentimento = new { Status = l.ConsentStatus, l.ConsentAt, l.ConsentSource,
                    historico = l.ConsentRecords.OrderBy(c => c.GrantedAt).Select(c => new { c.Status, c.Source, c.Evidence, c.GrantedAt, c.RegisteredBy }) },
                descadastro = optOut,
                fatos = l.Facts.OrderBy(f => f.CreatedAt).Select(f => new { f.Key, f.Value, Origem = f.Source.ToString(), f.CreatedAt }),
                historicoEtapas = l.StageChanges.OrderBy(s => s.CreatedAt).Select(s => new { s.From, s.To, s.ChangedBy, s.Reason, s.CreatedAt }),
                mensagens = l.Messages.OrderBy(m => m.Timestamp).Select(m => new { m.Timestamp, Direcao = m.Direction.ToString(), Autor = m.Author.ToString(), m.Text, Status = m.Status.ToString() }),
            };
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "lgpd.export", EntityType = "Lead", EntityId = id.ToString() });
            await db.SaveChangesAsync();
            var json = JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            return Results.File(json, "application/json", $"dados-titular-{l.Phone}.json");
        }).RequireAuthorization(Api.SupervisorPolicy);

        api.MapPost("/maintenance/purge", async (int? days, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var keep = Math.Max(7, days ?? 90);
            var limit = DateTimeOffset.UtcNow.AddDays(-keep);
            var events = (await db.WebhookEvents.Where(e => e.Processed).Select(e => new { e.Id, e.CreatedAt }).ToListAsync())
                .Where(e => e.CreatedAt < limit).Select(e => e.Id).ToList();
            var terminal = new[] { MessageStatus.EnviadaServidor, MessageStatus.Cancelada, MessageStatus.Falhou };
            var queue = (await db.OutboundQueue.Where(q => terminal.Contains(q.Status)).Select(q => new { q.Id, q.UpdatedAt, q.Kind }).ToListAsync())
                .Where(q => q.UpdatedAt < limit && q.Kind != OutboundKind.AbordagemInicial).Select(q => q.Id).ToList(); // abordagens ficam: base do intervalo e dos relatórios de campanha
            foreach (var chunk in events.Chunk(500)) await db.WebhookEvents.Where(e => chunk.Contains(e.Id)).ExecuteDeleteAsync();
            foreach (var chunk in queue.Chunk(500)) await db.OutboundQueue.Where(q => chunk.Contains(q.Id)).ExecuteDeleteAsync();
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "maintenance.purge", EntityType = "System", Details = $"{events.Count} eventos, {queue.Count} itens de fila (> {keep} dias)" });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, $"Removidos {events.Count} evento(s) de webhook e {queue.Count} item(ns) antigos da fila (mais de {keep} dias)."));
        }).RequireAuthorization(Api.AdminPolicy);

        api.MapPost("/maintenance/backup", async (CrmDbContext db, DataPaths paths, ClaimsPrincipal user) =>
        {
            if (!db.Database.IsSqlite())
                return Results.Ok(new OperationResult(false, "Banco PostgreSQL: faça o backup com pg_dump no servidor do banco (ver README)."));
            Directory.CreateDirectory(paths.Backups);
            var file = Path.Combine(paths.Backups, $"crm-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            await db.Database.ExecuteSqlAsync($"VACUUM INTO {file}");
            // mantém os 20 backups mais recentes
            foreach (var old in new DirectoryInfo(paths.Backups).GetFiles("crm-*.db").OrderByDescending(f => f.Name).Skip(20)) old.Delete();
            db.AuditLogs.Add(new AuditLog { Actor = Actor(user), Action = "maintenance.backup", EntityType = "System", Details = Path.GetFileName(file) });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, "Backup criado", file));
        }).RequireAuthorization(Api.AdminPolicy);

        api.MapGet("/maintenance/backups", (DataPaths paths) =>
            Directory.Exists(paths.Backups)
                ? new DirectoryInfo(paths.Backups).GetFiles("crm-*.db").OrderByDescending(f => f.Name).Select(f => new ReportRow(f.Name, (int)(f.Length / 1024), f.FullName)).ToList()
                : []).RequireAuthorization(Api.AdminPolicy);
    }

    public static async Task<ReportDto> BuildReportAsync(CrmDbContext db, DateTimeOffset from, DateTimeOffset to)
    {
        bool In(DateTimeOffset t) => t >= from && t <= to;
        var leads = await db.Leads.AsNoTracking().ToListAsync();
        var msgs = (await db.Messages.AsNoTracking().Select(m => new { m.LeadId, m.Direction, m.Author, m.Status, m.Timestamp, m.SentByUserId, m.TemplateVersionId }).ToListAsync())
            .Where(m => In(m.Timestamp)).ToList();
        var changes = (await db.StageChanges.AsNoTracking().ToListAsync()).Where(c => In(c.CreatedAt)).ToList();
        var users = await db.Users.AsNoTracking().ToListAsync();
        var versions = await db.MessageTemplateVersions.AsNoTracking().Join(db.MessageTemplates, v => v.TemplateId, t => t.Id, (v, t) => new { v.Id, t.Name, v.Version }).ToListAsync();

        var outbound = msgs.Where(m => m.Direction == MessageDirection.Saida && m.Author != MessageAuthor.Sistema).ToList();
        var newLeads = leads.Where(l => In(l.CreatedAt)).ToList();
        var closedIds = changes.Where(c => c.To == KanbanStage.NegocioFechado).Select(c => c.LeadId).ToHashSet();
        var lostIds = changes.Where(c => c.To == KanbanStage.NaoTemInteresse).Select(c => c.LeadId).ToHashSet();
        var repliedLeads = msgs.Where(m => m.Direction == MessageDirection.Entrada).Select(m => m.LeadId).ToHashSet();
        var byId = leads.ToDictionary(l => l.Id);

        static List<ReportRow> Group<T>(IEnumerable<T> items, Func<T, string> key) =>
            items.GroupBy(key).OrderByDescending(g => g.Count()).Select(g => new ReportRow(g.Key, g.Count())).ToList();

        var templateRows = versions.Select(v =>
        {
            var sentTo = outbound.Where(m => m.TemplateVersionId == v.Id && m.Status != MessageStatus.Falhou).Select(m => m.LeadId).ToHashSet();
            var answered = sentTo.Count(repliedLeads.Contains);
            return new ReportRow($"{v.Name} v{v.Version}", sentTo.Count, sentTo.Count == 0 ? "sem envios" : $"{answered} resposta(s) ({(double)answered / sentTo.Count:P0})");
        }).Where(r => r.Value > 0).OrderByDescending(r => r.Value).ToList();

        var attendantRows = users.Select(u =>
        {
            var sent = outbound.Count(m => m.SentByUserId == u.Id);
            var closed = changes.Count(c => c.To == KanbanStage.NegocioFechado && c.ChangedBy.StartsWith(u.UserName, StringComparison.OrdinalIgnoreCase));
            return new ReportRow(u.DisplayName, sent, $"{closed} fechamento(s), {leads.Count(l => l.AssignedUserId == u.Id)} conversa(s) atribuída(s)");
        }).OrderByDescending(r => r.Value).ToList();
        var aiSent = outbound.Count(m => m.Author == MessageAuthor.Ia);
        attendantRows.Insert(0, new ReportRow("IA (assistente virtual)", aiSent, $"{msgs.Count(m => m.Direction == MessageDirection.Entrada)} mensagem(ns) recebida(s) no período"));

        return new ReportDto(from, to, newLeads.Count,
            outbound.Where(m => m.Status != MessageStatus.Falhou).Select(m => m.LeadId).Distinct().Count(),
            repliedLeads.Count, closedIds.Count, lostIds.Count,
            Enum.GetValues<KanbanStage>().Select(s => new ReportRow(KanbanRules.Title(s), leads.Count(l => l.Stage == s))).ToList(),
            Group(closedIds.Where(byId.ContainsKey).Select(id => byId[id]), l => l.InterestPlanCode ?? "Não informado"),
            Group(lostIds.Where(byId.ContainsKey).Select(id => byId[id]), l => l.LossReason ?? "Não informado"),
            Group(newLeads.Where(l => l.MainPain != PainCategory.Nenhuma), l => PainClassifier.Title(l.MainPain)),
            Group(outbound, m => m.Status.ToString()),
            templateRows, attendantRows,
            Group(newLeads, l => string.IsNullOrWhiteSpace(l.Source) ? "Não informado" : l.Source!));
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? ValidateBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "O texto do modelo está vazio";
        if (body.Length > 1000) return "Modelo muito longo (máximo 1000 caracteres)";
        var unknown = TemplateRenderer.UnknownVariables(body);
        if (unknown.Count > 0) return $"Variáveis desconhecidas: {string.Join(", ", unknown)}. Use {{nome}}, {{, nome}} ou {{assistente}}.";
        if (!body.Contains("{assistente}") && !body.Contains("assistente virtual", StringComparison.OrdinalIgnoreCase))
            return "A mensagem deve identificar a assistente virtual (use {assistente} ou o texto 'assistente virtual')";
        return null;
    }

    private static string D(DateTimeOffset? t) => t?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR")) ?? "";

    /// <summary>Linha CSV com ';' (Excel pt-BR). Neutraliza fórmulas (injeção CSV).</summary>
    private static void Row(StringBuilder sb, params string?[] cols)
    {
        sb.AppendJoin(';', cols.Select(c =>
        {
            var v = c ?? "";
            if (v.Length > 0 && "=+-@\t\r".Contains(v[0])) v = "'" + v;
            return v.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }));
        sb.Append("\r\n");
    }

    private static IResult Csv(StringBuilder sb, string name) =>
        Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv; charset=utf-8", name);
}
