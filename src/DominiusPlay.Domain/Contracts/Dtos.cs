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
    string? AssignedTo, string? NextAction, ConsentStatus Consent, string Tags, KanbanStage? SuggestedStage = null, string? SuggestedStageReason = null,
    string? Company = null, string? AgentName = null, string? CampaignName = null, int? Score = null);

public sealed record LeadDetail(
    Guid Id, string Name, string Phone, string PhoneFormatted, string? City, string? State, string? Source, string? Notes, string Tags,
    IptvExperience Experience, string? PanelUsed, int? ActiveClients, PainCategory MainPain, string SecondaryPains,
    string? InterestPlanCode, string? InvestmentRange, KanbanStage Stage, ConversationMode Mode, Priority Priority,
    Guid? AssignedUserId, string? AssignedTo, string? NextAction, DateTimeOffset? LastInteractionAt, string? ConversationSummary,
    ConsentStatus Consent, DateTimeOffset? ConsentAt, string? ConsentSource, DateTimeOffset CreatedAt,
    IReadOnlyList<FactDto> Facts, IReadOnlyList<StageChangeDto> StageHistory, RecommendationDto? Recommendation,
    KanbanStage? SuggestedStage = null, string? SuggestedStageReason = null,
    string? Company = null, string? Segment = null, int? QualificationScore = null, string? QualificationExplanation = null,
    IntentCategory LastIntent = IntentCategory.Nenhuma, DateTimeOffset? HandoffAt = null, string? HandoffSummary = null, Guid? AttendanceAgentId = null)
{
    /// <summary>Nome para exibir: o número formatado quando o lead não tem nome.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? PhoneFormatted : Name;
}

public sealed record FactDto(string Key, string Value, FactSource Source, DateTimeOffset At);
public sealed record StageChangeDto(KanbanStage? From, KanbanStage To, string ChangedBy, string? Reason, DateTimeOffset At);
public sealed record RecommendationDto(string Profile, IReadOnlyList<string> PlanNames, string Rationale, string? MissingInfo);

public sealed record LeadUpsert(
    string Name, string Phone, string? City, string? State, string? Source, string? Notes, string? Tags,
    IptvExperience Experience, string? PanelUsed, int? ActiveClients, PainCategory MainPain, string? InterestPlanCode,
    string? InvestmentRange, Priority Priority, string? NextAction);

public sealed record ConsentRequest(string Source, string? Evidence, DateTimeOffset? GrantedAt);

/// <summary>Ação em lote na lista de Leads.</summary>
public enum BulkLeadAction { Autorizar = 0, RemoverTag = 1, Excluir = 2 }

/// <summary>
/// Ação em lote: aplica-se aos <see cref="Ids"/> informados ou, se <see cref="ByTag"/> vier preenchido,
/// a todos os leads com essa tag. <see cref="ConsentSource"/> é obrigatório para autorizar;
/// <see cref="Tag"/> é a tag a remover em <see cref="BulkLeadAction.RemoverTag"/>.
/// </summary>
public sealed record BulkLeadRequest(BulkLeadAction Action, IReadOnlyList<Guid>? Ids, string? ByTag = null, string? ConsentSource = null, string? Tag = null);
public sealed record BulkLeadResponse(int Affected, int Skipped, string Message);

public sealed record MoveCardRequest(KanbanStage To, string? Reason, bool CommercialConfirmation);
public sealed record MoveCardResponse(bool Applied, bool IsSuggestion, string? Message);

public sealed record KanbanColumn(KanbanStage Stage, string Title, IReadOnlyList<LeadCard> Cards);
/// <summary>Indicadores dos agentes (período, agente, campanha). Entrega só conta o que o provedor confirmou.</summary>
public sealed record AgentsDashboardDto(int Imported, int Eligible, int ActiveCampaigns, int Sent, int Delivered, int Replies, double ResponseRate,
    int Qualified, int Handoffs, int Proposals, int Sales, int OptOuts, int Errors, long AiTokens, IReadOnlyList<CampaignConversionDto> ByCampaign, string Note);
public sealed record CampaignConversionDto(Guid Id, string Name, int Sent, int Replied, int Qualified, int Sales, double ResponseRate, double ConversionRate);

public sealed record AutomationFlowDto(Guid Id, string Name, string Description, DominiusPlay.Domain.Services.AutomationTrigger Trigger, bool Active,
    IReadOnlyList<DominiusPlay.Domain.Services.AutomationBlock> Blocks, int Version, string UpdatedBy, DateTimeOffset UpdatedAt, int Runs);
public sealed record AutomationFlowUpsert(string Name, string? Description, DominiusPlay.Domain.Services.AutomationTrigger Trigger, bool Active,
    IReadOnlyList<DominiusPlay.Domain.Services.AutomationBlock> Blocks);
public sealed record AutomationTestRequest(IReadOnlyList<DominiusPlay.Domain.Services.AutomationBlock> Blocks, string Message, bool ConsentValid = true,
    bool OptedOut = false, KanbanStage Stage = KanbanStage.Respondeu, string? Tags = null, int Score = 0, ConversationMode Mode = ConversationMode.Ia,
    string? Segment = null, string? Campaign = null, bool RepliedToCampaign = true);
public sealed record AutomationTestResponse(bool Valid, IReadOnlyList<string> Problems, string Intent, IReadOnlyList<string> Trace, IReadOnlyList<string> Actions, bool AiWouldReply);

public sealed record KanbanStageDto(KanbanStage Stage, string Title, int Order, bool Visible, string DefaultTitle);

public sealed record MessageDto(Guid Id, MessageDirection Direction, MessageAuthor Author, string Text, MessageStatus Status,
    DateTimeOffset Timestamp, string? Error, string? SentBy);

public sealed record SendMessageRequest(string Text);
public sealed record SendMessageResponse(bool Queued, string? Reason, Guid? QueueItemId);

public sealed record ImportResponse(Guid BatchId, int Total, int Created, int Updated, int Duplicates, int Invalid, int Blocked,
    int WithConsent, IReadOnlyList<ImportRowDto> Problems);
public sealed record ImportRowDto(int Line, string? RawPhone, string Outcome, string? Message);
public sealed record PasteImportRequest(string Text, string? Source, string? Tags, string? ConsentSource = null);
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
public sealed record TemplateDto(Guid Id, string Name, string Purpose, int Version, string Body, ApprovalStatus Approval, int TimesUsed,
    Guid VersionId = default, string? ChannelTemplateName = null, string? SimilarTo = null);
public sealed record TemplateCreate(string Name, string Purpose, string Body);
public sealed record TemplateVersionCreate(string Body);
public sealed record TemplateApprove(string? ChannelTemplateName);
public sealed record TemplatePreviewRequest(string Body, Guid? LeadId);
public sealed record TemplatePreview(string Text, string? SimilarTo);

public sealed record QualificationUpdate(IptvExperience Experience, string? PanelUsed, int? ActiveClients, PainCategory MainPain,
    string? InterestPlanCode, string? InvestmentRange, Priority Priority, string? NextAction, string? Summary);
public sealed record FactCreate(string Key, string Value);

public sealed record UserDto(Guid Id, string UserName, string DisplayName, UserRole Role, bool Active, int OpenConversations);
public sealed record UserCreate(string UserName, string DisplayName, string Password, UserRole Role);
public sealed record UserUpdate(string DisplayName, UserRole Role, bool Active, string? NewPassword);
public sealed record AssignRequest(Guid? UserId);
public sealed record ChangePasswordRequest(string Current, string NewPassword);

public sealed record CampaignDto(Guid Id, string Name, CampaignStatus Status, string InstanceName, string StartTime, string EndTime, string AllowedDays,
    int MaxContacts, int MinIntervalSeconds, int DailyLimit, string? Objective, Guid? TemplateId, string TagFilter, DateTimeOffset? StartedAt,
    int Queued, int Sent, int Delivered, int Replied, int Failed, int Cancelled, int Eligible,
    Guid? DispatchAgentId = null, string Segment = "", string Prompt = "", DateTimeOffset? StartDate = null, bool AiPersonalization = false,
    Guid? FollowUpTemplateId = null, string FollowUpDays = "", int MaxFailurePercent = 20, int MaxOptOutPercent = 10, string? PauseReason = null,
    int OptOuts = 0, int SimilarityAlerts = 0, int FollowUpsSent = 0);
public sealed record CampaignUpsert(string Name, string StartTime, string EndTime, string AllowedDays, int MaxContacts, int MinIntervalSeconds,
    int DailyLimit, string? Objective, Guid? TemplateId, string TagFilter,
    Guid? DispatchAgentId = null, string? Segment = null, string? Prompt = null, DateTimeOffset? StartDate = null, bool AiPersonalization = false,
    Guid? FollowUpTemplateId = null, string? FollowUpDays = null, int MaxFailurePercent = 20, int MaxOptOutPercent = 10);

/// <summary>Resultado da validação da campanha antes de iniciar: problemas impedem o início; avisos não.</summary>
public sealed record CampaignValidation(bool Ok, int Eligible, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings);
/// <summary>Prévia de abordagem personalizada para um contato elegível (nada é salvo nem enviado).</summary>
public sealed record ApproachPreview(string Contact, string Text, string Note);

public sealed record ReportDto(DateTimeOffset From, DateTimeOffset To, int NewLeads, int Contacted, int Replied, int Closed, int Lost,
    IReadOnlyList<ReportRow> Funnel, IReadOnlyList<ReportRow> ClosedByPlan, IReadOnlyList<ReportRow> LossReasons, IReadOnlyList<ReportRow> Pains,
    IReadOnlyList<ReportRow> MessagesByStatus, IReadOnlyList<ReportRow> Templates, IReadOnlyList<ReportRow> ByAttendant, IReadOnlyList<ReportRow> Sources);
public sealed record ReportRow(string Label, int Value, string? Detail = null);

/// <summary>Linha do relatório de mensagens enviadas. Cada mensagem conta só no seu status atual (uma mensagem lida conta em Lido, não em Entregue).</summary>
public sealed record SentMessagesRow(string Key, string Label, int Total, int Sent, int Delivered, int Read, int Failed);
public sealed record FilterOption(string Key, string Label);
/// <summary>Relatório de mensagens enviadas: por período, campanha e atendente/IA, com status de entrega.</summary>
public sealed record SentMessagesReportDto(DateTimeOffset From, DateTimeOffset To, string Grouping, SentMessagesRow Totals, int QueuedNow,
    IReadOnlyList<SentMessagesRow> ByPeriod, IReadOnlyList<SentMessagesRow> ByCampaign, IReadOnlyList<SentMessagesRow> ByAuthor,
    IReadOnlyList<ReportRow> FailureReasons, IReadOnlyList<FilterOption> CampaignOptions, IReadOnlyList<FilterOption> AuthorOptions);

public sealed record AiSettingsDto(bool Enabled, string Model, string AdvancedModel, bool HasApiKey, int MaxTokens, string AssistantName,
    string Instructions, int DailyTokenLimit, int TokensUsedToday, bool TypingIndicator, int DebounceSeconds,
    string SystemPrompt = "", string DefaultSystemPrompt = "", string FixedRules = "");
public sealed record AiSettingsUpdate(bool Enabled, string Model, string AdvancedModel, string? ApiKey, int MaxTokens, string AssistantName,
    string Instructions, int DailyTokenLimit, bool TypingIndicator, int DebounceSeconds,
    string? SystemPrompt = null); // Instructions = script de atendimento; SystemPrompt null = mantém o atual, "" = volta ao padrão
public sealed record SimTurn(bool FromContact, string Text);
public sealed record AiSimulateRequest(IReadOnlyList<SimTurn> Turns);
public sealed record AiSimulateResponse(bool Ok, string? Reply, IReadOnlyList<string> Actions, bool Transferred, string? Error, int Tokens);
public sealed record ModelsResponse(bool Ok, IReadOnlyList<string> Models, string? Error);

// ---------- Agentes de IA (v0.4) ----------

public sealed record AgentDto(Guid Id, AgentKind Kind, string Name, string AssistantName, string Description, string Objective, string Company, string Personality, string Tone,
    string Prompt, string ProductsAndServices, string AllowedAnswers, string NegotiationRestrictions, string HandoffCriteria, string Model, string AdvancedModel,
    AgentStatus Status, bool IsDefault, string StartTime, string EndTime, string AllowedDays, string TimeZoneId, string IntentActions,
    string InstanceName, int DailyLimit, int MinIntervalSeconds, Guid? AttendanceAgentId, int Conversations);

public sealed record AgentUpsert(AgentKind Kind, string Name, string? AssistantName, string? Description, string? Objective, string? Company, string? Personality, string? Tone,
    string? Prompt, string? ProductsAndServices, string? AllowedAnswers, string? NegotiationRestrictions, string? HandoffCriteria, string? Model, string? AdvancedModel,
    AgentStatus Status, bool IsDefault, string StartTime, string EndTime, string AllowedDays, string? TimeZoneId, string? IntentActions,
    string? InstanceName = null, int DailyLimit = 50, int MinIntervalSeconds = 300, Guid? AttendanceAgentId = null);

public sealed record QualificationQuestionDto(Guid Id, Guid? AgentId, int Order, string Key, string Question, int Weight, string ScoringHint, bool Active);
public sealed record QualificationQuestionUpsert(Guid? AgentId, int Order, string Key, string Question, int Weight, string? ScoringHint, bool Active);

public sealed record IntentInfo(IntentCategory Intent, string Title, IntentAction DefaultAction);

/// <summary>Testar Agente: conversa simulada de vários turnos. SessionId mantém a memória (fatos, qualificação) entre os turnos; nada é salvo nem enviado.</summary>
public sealed record AgentTestRequest(Guid? SessionId, string Message, bool Reset = false);
public sealed record AgentTestResponse(Guid SessionId, bool Ok, string? Reply, IReadOnlyList<string> Actions, bool Transferred, bool Closed, string? Error, int Tokens,
    string DetectedIntent, string FinalIntent, int? Score, string? ScoreExplanation, string? HandoffSummary, IReadOnlyList<string> Memory, string Model);

public sealed record HandoffRequest(string? Reason);

public static class HubEvents
{
    public const string LeadChanged = "leadChanged";
    public const string MessageAdded = "messageAdded";
    public const string ConnectionChanged = "connectionChanged";
    /// <summary>Conversa transferida para humano (leadId, nome do atendente atribuído ou null).</summary>
    public const string Handoff = "handoff";
}

// ---------- Base de conhecimento Dominius Play, Comercial e Laboratório (v0.4.1) ----------
public sealed record KbEntryDto(Guid Id, string Base, string Code, string Category, string Question, string Variations, string Answer, string RenderedAnswer,
    string Keywords, string Products, int Priority, DominiusPlay.Domain.Entities.KbStatus Status, string Source, bool NeedsConfirmation, int Version,
    DateTimeOffset UpdatedAt, string UpdatedBy, IReadOnlyList<string> Warnings);
public sealed record KbEntryUpsert(string Code, string Category, string Question, string Variations, string Answer, string Keywords, string Products,
    int Priority, DominiusPlay.Domain.Entities.KbStatus Status, string Source, bool NeedsConfirmation);
public sealed record KbRevisionDto(int Version, string Action, string ChangedBy, DateTimeOffset At, string Question, string Answer, string Status);
public sealed record KbTestRequest(string Question, bool IncludeDrafts = true, IReadOnlyList<string>? Products = null);
public sealed record KbTestMatch(string Code, string Category, string Question, double Score, string RenderedAnswer, DominiusPlay.Domain.Entities.KbStatus Status, bool NeedsConfirmation, IReadOnlyList<string> Warnings);
public sealed record KbTestResponse(IReadOnlyList<KbTestMatch> Matches, string Profile, string Trigger, string? OfferMessage, string Intent);
public sealed record KbImportRequest(IReadOnlyList<KbEntryUpsert> Entries, bool Overwrite);
public sealed record KbImportResult(int Created, int Updated, int Skipped, IReadOnlyList<string> Errors);
public sealed record KbAccessDto(bool Attendance, bool Dispatch);
public sealed record CommercialHistoryDto(DateTimeOffset At, string Actor, string Details);
public sealed record LabRequest(string? Scenario, IReadOnlyList<string>? Messages, bool UseAi = false);
public sealed record LabScenarioDto(string Key, string Title, IReadOnlyList<string> Messages);
public sealed record LabStepDto(int Index, string Message, string Intent, string Profile, string Trigger, string? Category, string? MatchedCode, string? MatchedQuestion,
    double Score, string? Reply, string? Product, IReadOnlyList<string> MemoryUsed, string SuggestedAction, IReadOnlyList<string> Alerts, bool Handoff, bool OptOut,
    string? AiReply, IReadOnlyList<string> AiAlerts, IReadOnlyList<string> AiActions);
public sealed record LabResponse(string Title, IReadOnlyList<LabStepDto> Steps, bool AiUsed, string? AiError);
