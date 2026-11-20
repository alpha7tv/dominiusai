using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Services;

/// <summary>Roda os fluxos de automação ativos quando chega mensagem e aplica as ações (sempre pelas mesmas regras do resto do sistema).</summary>
public class AutomationService(CrmDbContext db, HandoffService handoff, TimeProvider clock, ILogger<AutomationService> log)
{
    public string ActionText(AutomationAction a) => a.Type switch
    {
        AutomationActionType.AdicionarTag => "Adicionar tag: " + a.Value,
        AutomationActionType.AtualizarEtapa => "Mover card para " + (Enum.TryParse<KanbanStage>(a.Value, out var s) ? KanbanRules.Title(s) : a.Value),
        AutomationActionType.Transferir => "Transferir para atendente: " + a.Value,
        AutomationActionType.EnviarModelo => "Enviar modelo aprovado: " + a.Value,
        AutomationActionType.DeixarIaResponder => "Agente de Atendimento IA responde",
        _ => a.Type.ToString(),
    };

    public async Task<bool> TemplateApprovedAsync(string name, CancellationToken ct = default) =>
        await ApprovedBodyAsync(name, ct) is not null;

    private async Task<string?> ApprovedBodyAsync(string name, CancellationToken ct)
    {
        var t = await db.MessageTemplates.AsNoTracking().Include(x => x.Versions).FirstOrDefaultAsync(x => x.Name == name.Trim(), ct);
        return t?.Versions.Where(v => v.Approval == ApprovalStatus.Aprovado).OrderByDescending(v => v.Version).Select(v => v.Body).FirstOrDefault();
    }

    /// <summary>Executa os fluxos para a mensagem recebida. Retorna (ia deve responder?, atendente atribuído se transferiu).</summary>
    public async Task<(bool SuppressAi, string? HandedTo)> OnInboundAsync(Lead lead, Message message, bool repliedToCampaign, CancellationToken ct = default)
    {
        var flows = await db.AutomationFlows.Where(f => f.Active).OrderBy(f => f.CreatedAt).ToListAsync(ct);
        if (flows.Count == 0 || lead.Mode is not (ConversationMode.Ia or ConversationMode.AguardandoContato or ConversationMode.Prospeccao)) return (false, null);
        var optedOut = await db.OptOuts.AnyAsync(o => o.Phone == lead.Phone, ct);
        var campaign = lead.OriginCampaignId is { } cid ? await db.Campaigns.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync(ct) : null;
        var suppress = false; string? handedTo = null;
        foreach (var f in flows)
        {
            if (f.Trigger == AutomationTrigger.RespostaACampanha && !repliedToCampaign) continue;
            var input = new AutomationInput(message.Text, message.Intent, lead.ConsentStatus == ConsentStatus.Valido, optedOut, lead.Stage, lead.Tags,
                lead.QualificationScore ?? 0, lead.Mode, lead.Segment, campaign, repliedToCampaign);
            var run = AutomationEngine.Run(AutomationEngine.Parse(f.BlocksJson), input);
            if (run.Error is not null) { log.LogWarning("Automação {Flow} inválida: {Error}", f.Name, run.Error); continue; }
            if (run.Actions.Count == 0) continue;
            f.Runs++;
            var applied = new List<string>();
            foreach (var a in run.Actions)
            {
                switch (a.Type)
                {
                    case AutomationActionType.AdicionarTag:
                        var tags = lead.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                        if (!tags.Contains(a.Value!, StringComparer.OrdinalIgnoreCase)) { tags.Add(a.Value!); lead.Tags = string.Join(",", tags); }
                        break;
                    case AutomationActionType.AtualizarEtapa when Enum.TryParse<KanbanStage>(a.Value, out var st):
                        var d = KanbanRules.Evaluate(lead, st, MoveActor.Sistema, commercialConfirmation: false);
                        if (d.Applied) KanbanRules.Apply(lead, st, "Automação: " + f.Name, "Regra de automação");
                        break;
                    case AutomationActionType.Transferir when lead.Mode != ConversationMode.AtendimentoHumano:
                        handedTo = await handoff.ApplyAsync(lead, a.Value ?? "Regra de automação", "Automação: " + f.Name, noticeToCustomer: true, ct) ?? "";
                        break;
                    case AutomationActionType.EnviarModelo:
                        if (await ApprovedBodyAsync(a.Value ?? "", ct) is { } body)
                            db.OutboundQueue.Add(new OutboundQueueItem { LeadId = lead.Id, Kind = OutboundKind.Resposta, InstanceName = message.InstanceName ?? "",
                                Text = TemplateRenderer.Render(body, lead.Name, ""), NotBefore = clock.GetUtcNow() });
                        break;
                }
                applied.Add(ActionText(a));
            }
            if (run.SuppressAi) suppress = true;
            db.AuditLogs.Add(new AuditLog { Actor = "Automação: " + f.Name, Action = "automation.run", EntityType = "Lead", EntityId = lead.Id.ToString(), Details = string.Join(" | ", applied) });
            if (lead.Mode == ConversationMode.AtendimentoHumano) break; // transferiu: nenhum outro fluxo fala
        }
        return (suppress, handedTo);
    }
}
