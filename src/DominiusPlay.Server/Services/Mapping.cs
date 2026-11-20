using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Services;

namespace DominiusPlay.Server.Services;

public static class Mapping
{
    public static LeadCard ToCard(this Lead l) => new(l.Id, string.IsNullOrWhiteSpace(l.Name) ? PhoneNormalizer.Format(l.Phone) : l.Name,
        l.Phone, PhoneNormalizer.Format(l.Phone), l.Stage, l.Mode, l.Priority, l.InterestPlanCode, l.ActiveClients, l.MainPain,
        l.LastMessagePreview, l.LastInteractionAt, l.AssignedUser?.DisplayName, l.NextAction, l.ConsentStatus, l.Tags);

    public static MessageDto ToDto(this Message m, string? sentBy = null) =>
        new(m.Id, m.Direction, m.Author, m.Text, m.Status, m.Timestamp, m.Error, sentBy);

    public static PlanDto ToDto(this Plan p) => new(p.Id, p.Code, p.Name, p.Version, p.MonthlyPrice, p.Credits, p.MaxActiveLogins,
        p.Description, p.Benefits, p.Limits, p.TechnicalRequirements, p.TargetProfile, p.ValidFrom, p.ValidUntil, p.Approval,
        p.ApprovedBy, p.IsCampaignPrice, p.IsActiveAt(DateTimeOffset.UtcNow));
}
