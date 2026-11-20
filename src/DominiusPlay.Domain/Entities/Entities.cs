using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Domain.Entities;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Lead : Entity
{
    public string Name { get; set; } = "";
    /// <summary>Telefone normalizado E.164 sem "+", ex.: 5511987654321. Único.</summary>
    public string Phone { get; set; } = "";
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Source { get; set; }
    public string? Notes { get; set; }
    public string Tags { get; set; } = ""; // separadas por vírgula
    public DateTimeOffset? ImportedAt { get; set; }
    public Guid? ImportBatchId { get; set; }

    // Qualificação comercial
    public IptvExperience Experience { get; set; }
    public string? PanelUsed { get; set; }
    public int? ActiveClients { get; set; }
    public PainCategory MainPain { get; set; }
    public string SecondaryPains { get; set; } = ""; // lista de PainCategory separada por vírgula
    public string? InterestPlanCode { get; set; }
    public string? InvestmentRange { get; set; }

    // CRM
    public KanbanStage Stage { get; set; } = KanbanStage.PrimeiroContato;
    public ConversationMode Mode { get; set; } = ConversationMode.AguardandoContato;
    public Priority Priority { get; set; } = Priority.Normal;
    public Guid? AssignedUserId { get; set; }
    public User? AssignedUser { get; set; }
    public string? NextAction { get; set; }
    public DateTimeOffset? LastInteractionAt { get; set; }
    public string? LastMessagePreview { get; set; }
    public string? ConversationSummary { get; set; }
    public string? LossReason { get; set; }

    // Consentimento (último estado consolidado; histórico em ConsentRecords)
    public ConsentStatus ConsentStatus { get; set; } = ConsentStatus.Pendente;
    public DateTimeOffset? ConsentAt { get; set; }
    public string? ConsentSource { get; set; }

    public List<ConsentRecord> ConsentRecords { get; set; } = new();
    public List<Message> Messages { get; set; } = new();
    public List<LeadFact> Facts { get; set; } = new();
    public List<StageChange> StageChanges { get; set; } = new();

    /// <summary>Token de concorrência otimista (bloqueio transacional por conversa).</summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

/// <summary>Registro imutável de consentimento / revogação.</summary>
public class ConsentRecord : Entity
{
    public Guid LeadId { get; set; }
    public ConsentStatus Status { get; set; }
    public string Source { get; set; } = "";   // ex.: "Formulário site", "Importação planilha X", "Pedido do contato"
    public string? Evidence { get; set; }       // ex.: link do formulário, print, texto da mensagem
    public DateTimeOffset GrantedAt { get; set; }
    public string? RegisteredBy { get; set; }
}

/// <summary>Lista de bloqueio / descadastro. Telefones aqui nunca recebem envio automático.</summary>
public class OptOut : Entity
{
    public string Phone { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Source { get; set; } = "";
}

public class Message : Entity
{
    public Guid LeadId { get; set; }
    public Lead? Lead { get; set; }
    public MessageDirection Direction { get; set; }
    public MessageAuthor Author { get; set; }
    public string Text { get; set; } = "";
    public MessageStatus Status { get; set; }
    /// <summary>ID real da mensagem no WhatsApp (key.id). Usado para deduplicação e atualização de status.</summary>
    public string? ExternalId { get; set; }
    public string? InstanceName { get; set; }
    public Guid? SentByUserId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? TemplateVersionId { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Fila persistente de envio. Sobrevive a reinicializações; cada item é reavaliado antes do envio.</summary>
public class OutboundQueueItem : Entity
{
    public Guid LeadId { get; set; }
    public Lead? Lead { get; set; }
    public OutboundKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public Guid? CampaignId { get; set; }
    public Guid? TemplateVersionId { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public DateTimeOffset NotBefore { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public MessageStatus Status { get; set; } = MessageStatus.NaFila;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public Guid? MessageId { get; set; }
    /// <summary>Chave de idempotência — impede envios duplicados do mesmo item.</summary>
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
}

/// <summary>Evento de webhook recebido. EventKey é único → deduplicação.</summary>
public class WebhookEvent : Entity
{
    public string EventKey { get; set; } = "";
    public string EventType { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public string Payload { get; set; } = "";
    public bool Processed { get; set; }
    public string? Error { get; set; }
}

public class LeadFact : Entity
{
    public Guid LeadId { get; set; }
    public string Key { get; set; } = "";   // ex.: painel, clientes_ativos, objecao, proposta_enviada, proximo_passo
    public string Value { get; set; } = "";
    public FactSource Source { get; set; }
}

public class StageChange : Entity
{
    public Guid LeadId { get; set; }
    public KanbanStage? From { get; set; }
    public KanbanStage To { get; set; }
    public string ChangedBy { get; set; } = ""; // usuário, "IA (sugestão aprovada)", "Sistema"
    public string? Reason { get; set; }
}

public class Plan : Entity
{
    public string Code { get; set; } = "";       // ex.: MENSALISTA_50
    public string Name { get; set; } = "";
    public int Version { get; set; } = 1;
    public decimal MonthlyPrice { get; set; }
    public int? Credits { get; set; }            // null = não se aplica / ilimitado conforme contrato
    public int? MaxActiveLogins { get; set; }    // null = sem limite contratado
    public string Description { get; set; } = "";
    public string Benefits { get; set; } = "";   // um por linha
    public string Limits { get; set; } = "";
    public string TechnicalRequirements { get; set; } = "";
    public string TargetProfile { get; set; } = "";
    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public ApprovalStatus Approval { get; set; } = ApprovalStatus.Rascunho;
    public string? ApprovedBy { get; set; }
    public bool IsCampaignPrice { get; set; }
    public int SortOrder { get; set; }

    public bool IsActiveAt(DateTimeOffset now) =>
        Approval == ApprovalStatus.Aprovado
        && (ValidFrom is null || ValidFrom <= now)
        && (ValidUntil is null || ValidUntil >= now);
}

public class KnowledgeArticle : Entity
{
    public string Category { get; set; } = "";  // Empresa, Planos, FAQ, Regras comerciais, Suporte...
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string? SourceUrl { get; set; }
    public ApprovalStatus Approval { get; set; } = ApprovalStatus.Rascunho;
    public string? ApprovedBy { get; set; }
    public int Version { get; set; } = 1;
}

public class MessageTemplate : Entity
{
    public string Name { get; set; } = "";
    public string Purpose { get; set; } = "Abordagem inicial";
    public List<MessageTemplateVersion> Versions { get; set; } = new();
}

public class MessageTemplateVersion : Entity
{
    public Guid TemplateId { get; set; }
    public int Version { get; set; }
    public string Body { get; set; } = "";       // aceita {nome}, {assistente}
    public ApprovalStatus Approval { get; set; } = ApprovalStatus.Rascunho;
    public string? ChannelTemplateName { get; set; } // nome do template aprovado no canal oficial (Cloud API), quando aplicável
    public string? ApprovedBy { get; set; }
    public int TimesUsed { get; set; }
}

public class Campaign : Entity
{
    public string Name { get; set; } = "";
    public CampaignStatus Status { get; set; } = CampaignStatus.Rascunho;
    public string InstanceName { get; set; } = "";
    public TimeOnly StartTime { get; set; } = new(9, 0);
    public TimeOnly EndTime { get; set; } = new(18, 0);
    public string AllowedDays { get; set; } = "1,2,3,4,5"; // DayOfWeek numérico
    public int MaxContacts { get; set; } = 100;
    public int MinIntervalSeconds { get; set; } = 300;     // padrão: 5 minutos
    public int DailyLimit { get; set; } = 50;
    public string? Objective { get; set; }
    public Guid? TemplateId { get; set; }
}

public class User : Entity
{
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; }
    public bool Active { get; set; } = true;
}

public class AuditLog : Entity
{
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? Details { get; set; }
}

/// <summary>Configurações chave/valor. Valores sensíveis são armazenados criptografados (IsSecret).</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool IsSecret { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ImportBatch : Entity
{
    public string FileName { get; set; } = "";
    public string Format { get; set; } = "";
    public int TotalRows { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Duplicates { get; set; }
    public int Invalid { get; set; }
    public int Blocked { get; set; }
    public string ImportedBy { get; set; } = "";
}
