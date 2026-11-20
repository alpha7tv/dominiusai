using System.Security.Claims;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Endpoints;

/// <summary>Construtor visual de automações: CRUD dos fluxos, validação (ciclos e conflitos) e modo de teste sem envio.</summary>
public static class AutomationsApi
{
    private static AutomationFlowDto ToDto(AutomationFlow f) =>
        new(f.Id, f.Name, f.Description, f.Trigger, f.Active, AutomationEngine.Parse(f.BlocksJson), f.Version, f.UpdatedBy, f.UpdatedAt, f.Runs);

    private static async Task<List<string>> ValidateAsync(IReadOnlyList<AutomationBlock> blocks, AutomationService svc)
    {
        var names = blocks.Where(b => b.Type == AutomationBlockType.EnviarMensagem && !string.IsNullOrWhiteSpace(b.Param)).Select(b => b.Param!.Trim()).Distinct().ToList();
        var approved = new HashSet<string>();
        foreach (var n in names) if (await svc.TemplateApprovedAsync(n)) approved.Add(n);
        return AutomationEngine.Validate(blocks, n => approved.Contains(n.Trim()));
    }

    public static void MapAutomationsApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/automations").RequireAuthorization();

        api.MapGet("/", async (CrmDbContext db) => (await db.AutomationFlows.AsNoTracking().OrderBy(f => f.Name).ToListAsync()).Select(ToDto).ToList());

        api.MapPost("/", async (AutomationFlowUpsert r, CrmDbContext db, AutomationService svc, ClaimsPrincipal user) =>
        {
            var f = new AutomationFlow();
            if (await ApplyAsync(f, r, svc, user) is { } err) return Results.BadRequest(new OperationResult(false, err));
            db.AutomationFlows.Add(f);
            db.AuditLogs.Add(new AuditLog { Actor = user.Identity?.Name ?? "", Action = "automation.create", EntityType = "AutomationFlow", EntityId = f.Id.ToString(), Details = f.Name });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(f));
        }).RequireAuthorization(Api.AdminPolicy);

        api.MapPut("/{id:guid}", async (Guid id, AutomationFlowUpsert r, CrmDbContext db, AutomationService svc, ClaimsPrincipal user) =>
        {
            var f = await db.AutomationFlows.FindAsync(id);
            if (f is null) return Results.NotFound();
            if (await ApplyAsync(f, r, svc, user) is { } err) return Results.BadRequest(new OperationResult(false, err));
            f.Version++;
            db.AuditLogs.Add(new AuditLog { Actor = user.Identity?.Name ?? "", Action = "automation.update", EntityType = "AutomationFlow", EntityId = f.Id.ToString(),
                Details = $"{f.Name} v{f.Version} ({(f.Active ? "ativo" : "inativo")})" });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(f));
        }).RequireAuthorization(Api.AdminPolicy);

        api.MapDelete("/{id:guid}", async (Guid id, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var f = await db.AutomationFlows.FindAsync(id);
            if (f is null) return Results.NotFound();
            db.AutomationFlows.Remove(f);
            db.AuditLogs.Add(new AuditLog { Actor = user.Identity?.Name ?? "", Action = "automation.delete", EntityType = "AutomationFlow", EntityId = id.ToString(), Details = f.Name });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, "Fluxo excluído."));
        }).RequireAuthorization(Api.AdminPolicy);

        // modo de teste: roda o fluxo (salvo ou não) sobre um contato fictício. Nada é gravado nem enviado.
        api.MapPost("/test", async (AutomationTestRequest r, AutomationService svc) =>
        {
            var problems = await ValidateAsync(r.Blocks, svc);
            var intent = IntentDetector.Detect(r.Message ?? "", isFirstInbound: false);
            if (problems.Count > 0) return new AutomationTestResponse(false, problems, IntentDetector.Title(intent), [], [], false);
            var run = AutomationEngine.Run(r.Blocks, new AutomationInput(r.Message ?? "", intent, r.ConsentValid, r.OptedOut, r.Stage, r.Tags ?? "", r.Score, r.Mode,
                r.Segment, r.Campaign, r.RepliedToCampaign));
            var aiReplies = !run.SuppressAi && r.Mode == Domain.Enums.ConversationMode.Ia;
            return new AutomationTestResponse(run.Error is null, run.Error is null ? [] : [run.Error], IntentDetector.Title(intent), run.Trace, run.Actions.Select(svc.ActionText).ToList(), aiReplies);
        });
    }

    private static async Task<string?> ApplyAsync(AutomationFlow f, AutomationFlowUpsert r, AutomationService svc, ClaimsPrincipal user)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 80) return "Informe o nome do fluxo (até 80 caracteres)";
        var problems = await ValidateAsync(r.Blocks, svc);
        // rascunho inválido pode ser salvo inativo; ativo precisa estar válido
        if (r.Active && problems.Count > 0) return "Corrija antes de ativar: " + string.Join("; ", problems);
        f.Name = r.Name.Trim(); f.Description = r.Description?.Trim() ?? ""; f.Trigger = r.Trigger; f.Active = r.Active;
        f.BlocksJson = AutomationEngine.Serialize(r.Blocks); f.UpdatedBy = user.Identity?.Name ?? ""; f.UpdatedAt = DateTimeOffset.UtcNow;
        return null;
    }
}
