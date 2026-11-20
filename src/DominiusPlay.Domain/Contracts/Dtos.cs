using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Domain.Contracts;

// Contratos compartilhados entre servidor (API) e aplicativo Windows.

public sealed record LoginRequest(string UserName, string Password);
public sealed record LoginResponse(string Token, string DisplayName, UserRole Role, DateTimeOffset ExpiresAt);
public sealed record SetupRequest(string UserName, string DisplayName, string Password);
public sealed record SetupStatus(bool NeedsSetup);

public sealed record LeadCard(
    Guid Id, string Name, string Phone, string PhoneFormatted, KanbanStage Stage, ConversationMode Mode, Priority Priority,
    string? InterestPlanCode, int? ActiveClients, PainCategory MainPain, string? LastMessagePreview, DateTimeOffset? LastInteractionAt,
    string? AssignedTo, string? NextAction, ConsentStatus Consent, string Tags);

public sealed record LeadDetail(
    Guid Id, string Name, string Phone, string PhoneFormatted, string? City, string? State, string? Source, string? Notes, string Tags,
    IptvExperience Experience, string? PanelUsed, int? ActiveClients, PainCategory MainPain, string SecondaryPains,
    string? InterestPlanCode, string? InvestmentRange, KanbanStage Stage, ConversationMode Mode, Priority Priority,
    Guid? AssignedUserId, string? AssignedTo, string? NextAction, DateTimeOffset? LastInteractionAt, string? ConversationSummary,
    ConsentStatus Consent, DateTimeOffset? ConsentAt, string? ConsentSource, DateTimeOffset CreatedAt,
    IReadOnlyList<FactDto> Facts, IReadOnlyList<StageChangeDto> StageHistory, RecommendationDto? Recommendation);

public sealed record FactDto(string Key, string Value, FactSource Source, DateTimeOffset At);
public sealed record StageChangeDto(KanbanStage? From, KanbanStage To, string ChangedBy, string? Reason, DateTimeOffset At);
public sealed record RecommendationDto(string Profile, IReadOnlyList<string> PlanNames, string Rationale, string? MissingInfo);

public sealed record LeadUpsert(
    string Name, string Phone, string? City, string? State, string? Source, string? Notes, string? Tags,
    IptvExperience Experience, string? PanelUsed, int? ActiveClients, PainCategory MainPain, string? InterestPlanCode,
    string? InvestmentRange, Priority Priority, string? NextAction);

public sealed record ConsentRequest(string Source, string? Evidence, DateTimeOffset? GrantedAt);

public sealed record MoveCardRequest(KanbanStage To, string? Reason, bool CommercialConfirmation);
public sealed record MoveCardResponse(bool Applied, bool IsSuggestion, string? Message);

public sealed record KanbanColumn(KanbanStage Stage, string Title, IReadOnlyList<LeadCard> Cards);

public sealed record MessageDto(Guid Id, MessageDirection Direction, MessageAuthor Author, string Text, MessageStatus Status,
    DateTimeOffset Timestamp, string? Error, string? SentBy);

public sealed record SendMessageRequest(string Text);
public sealed record SendMessageResponse(bool Queued, string? Reason, Guid? QueueItemId);

public sealed record ImportResponse(Guid BatchId, int Total, int Created, int Updated, int Duplicates, int Invalid, int Blocked,
    int WithConsent, IReadOnlyList<ImportRowDto> Problems);
public sealed record ImportRowDto(int Line, string? RawPhone, string Outcome, string? Message);
public sealed record PasteImportRequest(string Text, string? Source, string? Tags);
public sealed record OptOutImportRequest(string Text, string Reason);

public sealed record PlanDto(Guid Id, string Code, string Name, int Version, decimal MonthlyPrice, int? Credits, int? MaxActiveLogins,
    string Description, string Benefits, string Limits, string TechnicalRequirements, string TargetProfile,
    DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, ApprovalStatus Approval, string? ApprovedBy, bool IsCampaignPrice, bool IsActiveNow);

public sealed record PlanUpsert(string Code, string Name, decimal MonthlyPrice, int? Credits, int? MaxActiveLogins, string Description,
    string Benefits, string Limits, string TechnicalRequirements, string TargetProfile, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil);

public sealed record EvolutionSettingsDto(string? BaseUrl, string? InstanceName, bool HasApiKey, string? IntegrationMode,
    string? WebhookPublicUrl, string WebhookPath, bool HasWebhookToken);
public sealed record EvolutionSettingsUpdate(string BaseUrl, string InstanceName, string? ApiKey, string? IntegrationMode, string? WebhookPublicUrl);
public sealed record EvolutionStatusDto(bool Configured, bool Reachable, string? State, string? Error, DateTimeOffset CheckedAt);
public sealed record TestMessageRequest(string Phone, string Text);
public sealed record OperationResult(bool Ok, string Message, string? Detail = null);

public sealed record WebhookEventDto(DateTimeOffset At, string Type, string Instance, bool Processed, string? Error);

public sealed record DashboardDto(
    int LeadsTotal, int LeadsWithConsent, int LeadsPendingConsent, int ContactsInitiated, int ConversationsReplied,
    IReadOnlyDictionary<string, int> ByStage, int HumanTakeovers, int MessagesSent, int MessagesDelivered, int MessagesRead,
    int MessagesFailed, int QueuePending, IReadOnlyDictionary<string, int> TopPains, IReadOnlyDictionary<string, int> ClosedByPlan,
    IReadOnlyDictionary<string, int> LossReasons);

public sealed record KnowledgeDto(Guid Id, string Category, string Title, string Content, string? SourceUrl, ApprovalStatus Approval, int Version);
public sealed record TemplateDto(Guid Id, string Name, string Purpose, int Version, string Body, ApprovalStatus Approval, int TimesUsed);

public sealed record AiSettingsDto(bool Enabled, string Model, bool HasApiKey, double Temperature, int MaxTokens, string AssistantName,
    string Instructions, int DailyTokenLimit, int TokensUsedToday, bool TypingIndicator, int DebounceSeconds);
public sealed record AiSettingsUpdate(bool Enabled, string Model, string? ApiKey, double Temperature, int MaxTokens, string AssistantName,
    string Instructions, int DailyTokenLimit, bool TypingIndicator, int DebounceSeconds);
public sealed record SimTurn(bool FromContact, string Text);
public sealed record AiSimulateRequest(IReadOnlyList<SimTurn> Turns);
public sealed record AiSimulateResponse(bool Ok, string? Reply, IReadOnlyList<string> Actions, bool Transferred, string? Error, int Tokens);
public sealed record ModelsResponse(bool Ok, IReadOnlyList<string> Models, string? Error);

public static class HubEvents
{
    public const string LeadChanged = "leadChanged";
    public const string MessageAdded = "messageAdded";
    public const string ConnectionChanged = "connectionChanged";
}
