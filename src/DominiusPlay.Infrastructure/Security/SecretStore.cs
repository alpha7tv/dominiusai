using DominiusPlay.Domain.Entities;
using DominiusPlay.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Infrastructure.Security;

/// <summary>
/// Configurações persistidas no banco. Valores marcados como segredo (chave da Evolution API, chave do
/// provedor de IA) são criptografados com ASP.NET Data Protection e nunca retornados à aplicação cliente.
/// </summary>
public class SettingsStore(CrmDbContext db, IDataProtectionProvider dp)
{
    private readonly IDataProtector _protector = dp.CreateProtector("DominiusPlay.Settings.v1");

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        if (s is null) return null;
        return s.IsSecret ? _protector.Unprotect(s.Value) : s.Value;
    }

    public async Task<bool> HasAsync(string key, CancellationToken ct = default) =>
        await db.Settings.AnyAsync(x => x.Key == key && x.Value != "", ct);

    public async Task SetAsync(string key, string value, bool secret = false, CancellationToken ct = default)
    {
        var s = await db.Settings.FirstOrDefaultAsync(x => x.Key == key, ct);
        var stored = secret ? _protector.Protect(value) : value;
        if (s is null) db.Settings.Add(new AppSetting { Key = key, Value = stored, IsSecret = secret });
        else { s.Value = stored; s.IsSecret = secret; s.UpdatedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(ct);
    }
}

public static class SettingKeys
{
    public const string EvolutionUrl = "evolution.url";
    public const string EvolutionInstance = "evolution.instance";
    public const string EvolutionApiKey = "evolution.apikey";
    public const string EvolutionIntegration = "evolution.integration"; // WHATSAPP-BAILEYS | WHATSAPP-BUSINESS
    public const string WebhookToken = "webhook.token";
    public const string WebhookPublicUrl = "webhook.publicUrl";
    public const string AssistantName = "ai.assistantName";
    public const string MinInitialIntervalSeconds = "send.minInitialIntervalSeconds";
    public const string DailyLimit = "send.dailyLimit";
    public const string TimeZone = "app.timezone";
    public const string AiEnabled = "ai.enabled";
    public const string AiModel = "ai.model";
    public const string AiApiKey = "ai.apikey";
    public const string AiModelAdvanced = "ai.modelAdvanced";
    public const string AiMaxTokens = "ai.maxTokens";
    public const string AiInstructions = "ai.instructions"; // script de atendimento escrito pelo administrador
    public const string AiSystemPrompt = "ai.systemPrompt"; // vazio = prompt de sistema padrão
    public const string AiDailyTokenLimit = "ai.dailyTokenLimit";
    public const string AiTyping = "ai.typingIndicator";
    public const string AiDebounceSeconds = "ai.debounceSeconds";
    public const string CommercialConfig = "commercial.config"; // JSON editável (Pix, links, prazos, regras)
    public const string KbAccess = "kb.access";                 // agentes que consultam a base Dominius Play: "Atendimento" / "Atendimento,Disparo"
    public const string CommercialTable202611 = "commercial.table.2026-11"; // tabela oficial nov/2026 aplicada
    public static string AiUsage(DateOnly d) => $"ai.usage.{d:yyyyMMdd}";
}

public static class PasswordHasher
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
        var iter = int.Parse(parts[1]);
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, System.Security.Cryptography.HashAlgorithmName.SHA256, expected.Length);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
