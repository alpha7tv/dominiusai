using System.Globalization;
using System.Security.Claims;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Infrastructure.Ai;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Security;
using DominiusPlay.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Endpoints;

public static class AiApi
{
    public static void MapAiApi(this WebApplication app)
    {
        var ai = app.MapGroup("/api/ai").RequireAuthorization();

        ai.MapGet("/settings", async (AiAgent agent, SettingsStore s) =>
        {
            var c = await agent.LoadConfigAsync();
            return new AiSettingsDto(c.Enabled, c.Model, !string.IsNullOrWhiteSpace(c.ApiKey), c.Temperature, c.MaxTokens, c.AssistantName,
                c.Instructions, c.DailyTokenLimit, await agent.TokensUsedTodayAsync(), c.Typing, c.DebounceSeconds);
        });

        ai.MapPut("/settings", async (AiSettingsUpdate r, SettingsStore s, CrmDbContext db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(r.Model)) return Results.BadRequest(new OperationResult(false, "Informe o modelo"));
            if (r.MaxTokens is < 50 or > 4000) return Results.BadRequest(new OperationResult(false, "Tamanho máximo da resposta deve ficar entre 50 e 4000 tokens"));
            await s.SetAsync(SettingKeys.AiEnabled, r.Enabled ? "true" : "false");
            await s.SetAsync(SettingKeys.AiModel, r.Model.Trim());
            if (!string.IsNullOrWhiteSpace(r.ApiKey)) await s.SetAsync(SettingKeys.AiApiKey, r.ApiKey.Trim(), secret: true);
            await s.SetAsync(SettingKeys.AiTemperature, Math.Clamp(r.Temperature, 0, 1.5).ToString(CultureInfo.InvariantCulture));
            await s.SetAsync(SettingKeys.AiMaxTokens, r.MaxTokens.ToString());
            await s.SetAsync(SettingKeys.AssistantName, string.IsNullOrWhiteSpace(r.AssistantName) ? "Assistente Dominius Play" : r.AssistantName.Trim());
            await s.SetAsync(SettingKeys.AiInstructions, r.Instructions ?? "");
            await s.SetAsync(SettingKeys.AiDailyTokenLimit, Math.Max(0, r.DailyTokenLimit).ToString());
            await s.SetAsync(SettingKeys.AiTyping, r.TypingIndicator ? "true" : "false");
            await s.SetAsync(SettingKeys.AiDebounceSeconds, Math.Clamp(r.DebounceSeconds, 0, 120).ToString());
            db.AuditLogs.Add(new AuditLog { Actor = user.Identity?.Name ?? "", Action = "ai.settings", EntityType = "Setting",
                Details = $"{(r.Enabled ? "ativada" : "desativada")}, modelo {r.Model}" + (string.IsNullOrWhiteSpace(r.ApiKey) ? "" : " (chave alterada)") });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, r.Enabled ? "IA ativada. Ela responderá às conversas no modo IA." : "Configuração salva (IA desativada)."));
        }).RequireAuthorization(Api.AdminPolicy);

        ai.MapGet("/models", async (AiAgent agent, IAiProvider provider) =>
        {
            var c = await agent.LoadConfigAsync();
            if (string.IsNullOrWhiteSpace(c.ApiKey)) return new ModelsResponse(false, [], "Salve a chave da OpenAI primeiro");
            var (ok, models, err) = await provider.ListModelsAsync(c.ApiKey);
            return new ModelsResponse(ok, models, err);
        });

        // Simulação: conversa fictícia, nada é salvo nem enviado (testes de conversação sem mensagens reais).
        ai.MapPost("/simulate", async (AiSimulateRequest r, AiAgent agent) =>
        {
            var c = await agent.LoadConfigAsync();
            var lead = new Lead { Name = "", Phone = "5500000000000", Mode = ConversationMode.Ia };
            var t0 = DateTimeOffset.UtcNow.AddMinutes(-r.Turns.Count);
            var history = r.Turns.Select((t, i) => new Message
            {
                LeadId = lead.Id, Text = t.Text, Timestamp = t0.AddMinutes(i),
                Direction = t.FromContact ? MessageDirection.Entrada : MessageDirection.Saida, Author = t.FromContact ? MessageAuthor.Contato : MessageAuthor.Ia,
            }).ToList();
            var res = await agent.RunAsync(lead, history, c);
            return new AiSimulateResponse(res.Error is null, res.Reply, res.Actions, res.Transferred, res.Error, res.Tokens);
        });

        // Sugestão interna para o atendente humano (não é enviada; o atendente revisa e envia se quiser).
        app.MapPost("/api/conversations/{id:guid}/suggest", async (Guid id, AiAgent agent, CrmDbContext db) =>
        {
            var c = await agent.LoadConfigAsync();
            var lead = await db.Leads.AsNoTracking().Include(l => l.Facts).FirstOrDefaultAsync(l => l.Id == id);
            if (lead is null) return Results.NotFound();
            var history = await db.Messages.AsNoTracking().Where(m => m.LeadId == id).ToListAsync();
            var res = await agent.RunAsync(lead, history, c);
            return Results.Ok(new AiSimulateResponse(res.Error is null, res.Reply, res.Actions, res.Transferred, res.Error, res.Tokens));
        }).RequireAuthorization();

        app.MapPost("/api/knowledge/{id:guid}/approve", async (Guid id, bool approve, CrmDbContext db, ClaimsPrincipal user) =>
        {
            var k = await db.KnowledgeArticles.FindAsync(id);
            if (k is null) return Results.NotFound();
            k.Approval = approve ? ApprovalStatus.Aprovado : ApprovalStatus.EmRevisao;
            k.ApprovedBy = approve ? user.Identity?.Name : null;
            k.UpdatedAt = DateTimeOffset.UtcNow;
            db.AuditLogs.Add(new AuditLog { Actor = user.Identity?.Name ?? "", Action = approve ? "knowledge.approve" : "knowledge.unapprove", EntityType = "KnowledgeArticle", EntityId = id.ToString() });
            await db.SaveChangesAsync();
            return Results.Ok(new OperationResult(true, approve ? "Aprovado: a IA passa a usar este conteúdo." : "Retirado da IA (em revisão)."));
        }).RequireAuthorization(Api.AdminPolicy);
    }
}
