using DominiusPlay.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Infrastructure.Data;

/// <summary>
/// Banco central (fonte única de verdade). Provedores suportados: PostgreSQL (produção, multi-atendente)
/// e SQLite (instalação de uma máquina / desenvolvimento / testes).
/// </summary>
public class CrmDbContext(DbContextOptions<CrmDbContext> options) : DbContext(options)
{
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();
    public DbSet<OptOut> OptOuts => Set<OptOut>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<OutboundQueueItem> OutboundQueue => Set<OutboundQueueItem>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<LeadFact> LeadFacts => Set<LeadFact>();
    public DbSet<StageChange> StageChanges => Set<StageChange>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<KnowledgeArticle> KnowledgeArticles => Set<KnowledgeArticle>();
    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();
    public DbSet<MessageTemplateVersion> MessageTemplateVersions => Set<MessageTemplateVersion>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<AgentProfile> AgentProfiles => Set<AgentProfile>();
    public DbSet<QualificationQuestion> QualificationQuestions => Set<QualificationQuestion>();
    public DbSet<AutomationFlow> AutomationFlows => Set<AutomationFlow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // IDs são gerados pela aplicação (Guid.NewGuid). Sem isso o EF trata entidades novas adicionadas
        // por navegação (ex.: lead.ConsentRecords.Add) como existentes e tenta UPDATE.
        foreach (var et in typeof(Entity).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Entity)) && !t.IsAbstract))
            b.Entity(et).Property(nameof(Entity.Id)).ValueGeneratedNever();

        b.Entity<Lead>(e =>
        {
            e.HasIndex(x => x.Phone).IsUnique();
            e.HasIndex(x => x.Stage);
            e.HasIndex(x => x.ConsentStatus);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            e.HasMany(x => x.ConsentRecords).WithOne().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Messages).WithOne(x => x.Lead).HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Facts).WithOne().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.StageChanges).WithOne().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.AssignedUser).WithMany().HasForeignKey(x => x.AssignedUserId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<OptOut>().HasIndex(x => x.Phone).IsUnique();
        b.Entity<Message>(e =>
        {
            e.HasIndex(x => new { x.LeadId, x.Timestamp });
            // Deduplicação: um ID real de mensagem do WhatsApp só existe uma vez por instância.
            e.HasIndex(x => new { x.InstanceName, x.ExternalId }).IsUnique();
        });
        b.Entity<OutboundQueueItem>(e =>
        {
            e.HasIndex(x => new { x.Status, x.NotBefore });
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<WebhookEvent>().HasIndex(x => x.EventKey).IsUnique();
        b.Entity<Plan>(e =>
        {
            e.HasIndex(x => new { x.Code, x.Version }).IsUnique();
            e.Property(x => x.MonthlyPrice).HasPrecision(12, 2);
        });
        b.Entity<MessageTemplate>().HasMany(x => x.Versions).WithOne().HasForeignKey(x => x.TemplateId);
        b.Entity<User>().HasIndex(x => x.UserName).IsUnique();
        b.Entity<AppSetting>().HasKey(x => x.Key);
        b.Entity<AuditLog>().HasIndex(x => x.CreatedAt);
        b.Entity<AgentProfile>(e => { e.HasIndex(x => new { x.Kind, x.Status }); e.Property(x => x.Name).HasMaxLength(120); });
        b.Entity<QualificationQuestion>().HasIndex(x => new { x.AgentId, x.Order });

        // SQLite não ordena/compara DateTimeOffset nativamente: armazenar como ticks UTC (long).
        if (Database.IsSqlite())
        {
            foreach (var et in b.Model.GetEntityTypes())
            foreach (var p in et.GetProperties())
            {
                if (p.ClrType == typeof(DateTimeOffset)) p.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
                if (p.ClrType == typeof(DateTimeOffset?)) p.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
                if (p.ClrType == typeof(decimal)) p.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<decimal, double>(v => (double)v, v => (decimal)v));
            }
        }
    }
}
