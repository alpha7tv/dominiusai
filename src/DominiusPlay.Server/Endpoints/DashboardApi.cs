using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Security;
using DominiusPlay.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Endpoints;

/// <summary>Painel de indicadores dos agentes com filtros por período, agente (disparo ou atendimento) e campanha.</summary>
public static class DashboardApi
{
    private static readonly KanbanStage[] ProposalOrLater = [KanbanStage.PropostaEnviada, KanbanStage.EmNegociacao, KanbanStage.NegocioFechado];

    public static void MapDashboardApi(this WebApplication app)
    {
        app.MapGet("/api/dashboard/agents", async (DateTimeOffset? from, DateTimeOffset? to, Guid? agentId, Guid? campaignId, CrmDbContext db, SettingsStore settings, TimeProvider clock) =>
        {
            var start = from ?? DateTimeOffset.MinValue;
            var end = to ?? DateTimeOffset.MaxValue;
            var agent = agentId is { } aid ? await db.AgentProfiles.AsNoTracking().FirstOrDefaultAsync(a => a.Id == aid) : null;

            var campaigns = await db.Campaigns.AsNoTracking().ToListAsync();
            if (campaignId is { } cid) campaigns = campaigns.Where(c => c.Id == cid).ToList();
            if (agent?.Kind == AgentKind.Disparo) campaigns = campaigns.Where(c => c.DispatchAgentId == agent.Id).ToList();
            var campIds = campaigns.Select(c => c.Id).ToHashSet();
            var byCampaign = campaignId is not null || agent?.Kind == AgentKind.Disparo;

            var leads = await db.Leads.AsNoTracking().Select(l => new { l.Id, l.CreatedAt, l.Stage, l.Mode, l.ConsentStatus, l.Phone, l.OriginCampaignId, l.AttendanceAgentId, l.QualificationScore }).ToListAsync();
            if (byCampaign) leads = leads.Where(l => l.OriginCampaignId is { } o && campIds.Contains(o)).ToList();
            if (agent?.Kind == AgentKind.Atendimento) leads = leads.Where(l => l.AttendanceAgentId == agent.Id).ToList();
            if (agentId is not null && agent is null) { leads.Clear(); campaigns.Clear(); campIds.Clear(); }
            var leadIds = leads.Select(l => l.Id).ToHashSet();

            var msgs = (await db.Messages.AsNoTracking().Where(m => m.Timestamp >= start && m.Timestamp <= end)
                .Select(m => new { m.LeadId, m.Direction, m.Status, m.Author, m.CampaignId }).ToListAsync()).Where(m => leadIds.Contains(m.LeadId)).ToList();
            var outbound = msgs.Where(m => m.Direction == MessageDirection.Saida && m.Author != MessageAuthor.Sistema).ToList();
            if (byCampaign) outbound = outbound.Where(m => m.CampaignId is { } c && campIds.Contains(c)).ToList();
            var sentLeads = outbound.Where(m => m.Status != MessageStatus.Falhou).Select(m => m.LeadId).ToHashSet();
            var repliedLeads = msgs.Where(m => m.Direction == MessageDirection.Entrada).Select(m => m.LeadId).ToHashSet();

            var changes = (await db.StageChanges.AsNoTracking().Where(s => s.CreatedAt >= start && s.CreatedAt <= end).Select(s => new { s.LeadId, s.To }).ToListAsync())
                .Where(s => leadIds.Contains(s.LeadId)).ToList();
            bool Reached(Guid lead, params KanbanStage[] st) => changes.Any(c => c.LeadId == lead && st.Contains(c.To));
            var qualified = leads.Where(l => (l.QualificationScore ?? 0) >= KanbanRules.QualifiedScore || Reached(l.Id, KanbanStage.Qualificado)).Select(l => l.Id).ToHashSet();
            var proposals = leads.Where(l => Reached(l.Id, ProposalOrLater)).Select(l => l.Id).ToHashSet();
            var sales = leads.Where(l => Reached(l.Id, KanbanStage.NegocioFechado)).Select(l => l.Id).ToHashSet();

            var handoffs = (await db.AuditLogs.AsNoTracking().Where(a => a.Action == "conversation.handoff" && a.CreatedAt >= start && a.CreatedAt <= end).Select(a => a.EntityId).ToListAsync())
                .Count(id => Guid.TryParse(id, out var g) && leadIds.Contains(g));
            var phones = leads.Select(l => l.Phone).ToHashSet();
            var optOuts = (await db.OptOuts.AsNoTracking().Where(o => o.CreatedAt >= start && o.CreatedAt <= end).Select(o => o.Phone).ToListAsync()).Count(phones.Contains);
            var queueErrors = (await db.OutboundQueue.AsNoTracking().Where(q => q.Status == MessageStatus.Falhou && q.UpdatedAt >= start && q.UpdatedAt <= end).Select(q => new { q.LeadId, q.CampaignId }).ToListAsync())
                .Count(q => leadIds.Contains(q.LeadId) && (!byCampaign || q.CampaignId is { } c && campIds.Contains(c)));
            var eligibleOptOut = await db.OptOuts.AsNoTracking().Select(o => o.Phone).ToListAsync();
            var eligible = leads.Count(l => l.ConsentStatus == ConsentStatus.Valido && l.Stage == KanbanStage.PrimeiroContato && l.Mode == ConversationMode.AguardandoContato && !eligibleOptOut.Contains(l.Phone));

            // tokens de IA: o consumo é registrado por dia, sem separar por agente/campanha
            long tokens = 0;
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var firstDay = from is { } f ? DateOnly.FromDateTime(f.UtcDateTime) : today.AddDays(-90);
            var lastDay = to is { } t ? DateOnly.FromDateTime(t.UtcDateTime) : today;
            for (var d = firstDay; d <= lastDay && d <= today; d = d.AddDays(1))
                if (long.TryParse(await settings.GetAsync(SettingKeys.AiUsage(d)), out var n)) tokens += n;

            var rows = campaigns.Select(c =>
            {
                var cl = leads.Where(l => l.OriginCampaignId == c.Id).Select(l => l.Id).ToHashSet();
                var sent = outbound.Where(m => m.CampaignId == c.Id && m.Status != MessageStatus.Falhou).Select(m => m.LeadId).Distinct().Count();
                var rep = cl.Count(repliedLeads.Contains);
                var sal = cl.Count(sales.Contains);
                return new CampaignConversionDto(c.Id, c.Name, sent, rep, cl.Count(qualified.Contains), sal, Rate(rep, sent), Rate(sal, sent));
            }).Where(r => r.Sent > 0 || campaignId is not null).OrderByDescending(r => r.Sent).ToList();

            return new AgentsDashboardDto(
                leads.Count(l => l.CreatedAt >= start && l.CreatedAt <= end), eligible,
                campaigns.Count(c => c.Status == CampaignStatus.Ativa),
                outbound.Count(m => m.Status != MessageStatus.Falhou),
                outbound.Count(m => m.Status is MessageStatus.Entregue or MessageStatus.Lida),
                repliedLeads.Count, Rate(sentLeads.Count(repliedLeads.Contains), sentLeads.Count),
                qualified.Count, handoffs, proposals.Count, sales.Count, optOuts,
                queueErrors + msgs.Count(m => m.Status == MessageStatus.Falhou && m.Author == MessageAuthor.Sistema && !byCampaign),
                tokens, rows,
                "Entregas só contam quando o WhatsApp confirma (no modo Baileys nem sempre há confirmação). Tokens de IA são do sistema todo no período.");
        }).RequireAuthorization();
    }

    private static double Rate(int part, int total) => total == 0 ? 0 : Math.Round(100.0 * part / total, 1);
}
