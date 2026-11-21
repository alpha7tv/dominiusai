using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Services;

/// <summary>
/// LGPD: anonimização (alternativa à exclusão que preserva os números agregados de campanha e funil) e retenção configurável.
/// O número entra na lista de descadastro antes de ser apagado do contato, para nunca mais receber prospecção.
/// </summary>
public class LgpdService(CrmDbContext db, TimeProvider clock)
{
    public const string RetentionDaysKey = "lgpd.retention.days";
    public const string Removed = "[removido por anonimização]";

    public async Task<bool> AnonymizeAsync(Guid leadId, string actor, string reason, CancellationToken ct = default)
    {
        var l = await db.Leads.Include(x => x.Facts).Include(x => x.ConsentRecords).FirstOrDefaultAsync(x => x.Id == leadId, ct);
        if (l is null || l.Phone.StartsWith("anon-")) return false;
        if (!await db.OptOuts.AnyAsync(o => o.Phone == l.Phone, ct))
            db.OptOuts.Add(new OptOut { Phone = l.Phone, Reason = "Anonimização (LGPD)", Source = "Sistema" });
        await db.OutboundQueue.Where(q => q.LeadId == leadId && q.Status == MessageStatus.NaFila)
            .ExecuteUpdateAsync(u => u.SetProperty(q => q.Status, MessageStatus.Cancelada).SetProperty(q => q.LastError, "Contato anonimizado").SetProperty(q => q.Text, Removed), ct);
        await db.Messages.Where(m => m.LeadId == leadId).ExecuteUpdateAsync(u => u.SetProperty(m => m.Text, Removed), ct);
        l.Name = "Contato anonimizado";
        l.Phone = "anon-" + Guid.NewGuid().ToString("N")[..12];
        l.City = l.State = l.Notes = l.Company = l.PanelUsed = l.LastMessagePreview = l.ConversationSummary = l.HandoffSummary = l.QualificationExplanation = l.NextAction = null;
        l.Tags = ""; l.LossReason = null; l.InvestmentRange = null;
        db.LeadFacts.RemoveRange(l.Facts);
        foreach (var c in l.ConsentRecords) c.Evidence = null;
        ConversationStateMachine.Apply(l, ConversationTrigger.Bloquear);
        l.ConsentStatus = ConsentStatus.Revogado;
        l.ConcurrencyStamp = Guid.NewGuid();
        l.UpdatedAt = clock.GetUtcNow();
        db.AuditLogs.Add(new AuditLog { Actor = actor, Action = "lead.anonymize", EntityType = "Lead", EntityId = leadId.ToString(), Details = reason });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Anonimiza contatos sem interação há mais de N dias (exceto clientes com venda concluída). N = 0 desliga.</summary>
    public async Task<int> ApplyRetentionAsync(int days, string actor, CancellationToken ct = default)
    {
        if (days <= 0) return 0;
        var limit = clock.GetUtcNow().AddDays(-days);
        var ids = (await db.Leads.AsNoTracking().Where(l => l.Stage != KanbanStage.NegocioFechado)
                .Select(l => new { l.Id, l.Phone, Last = l.LastInteractionAt, l.CreatedAt }).ToListAsync(ct))
            .Where(l => !l.Phone.StartsWith("anon-") && (l.Last ?? l.CreatedAt) < limit).Select(l => l.Id).ToList();
        var n = 0;
        foreach (var id in ids) if (await AnonymizeAsync(id, actor, $"Retenção: sem interação há mais de {days} dias", ct)) n++;
        return n;
    }
}

/// <summary>Aplica a retenção configurada uma vez por dia (desligada por padrão).</summary>
public class RetentionWorker(IServiceScopeFactory scopes, ILogger<RetentionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
                if (int.TryParse(await settings.GetAsync(LgpdService.RetentionDaysKey, stoppingToken), out var days) && days > 0)
                {
                    var n = await scope.ServiceProvider.GetRequiredService<LgpdService>().ApplyRetentionAsync(days, "Sistema (retenção)", stoppingToken);
                    if (n > 0) log.LogInformation("Retenção LGPD: {N} contato(s) anonimizado(s)", n);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Falha na retenção LGPD"); }
            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
