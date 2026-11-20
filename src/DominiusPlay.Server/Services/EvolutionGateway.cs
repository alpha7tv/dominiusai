using DominiusPlay.Infrastructure.Evolution;
using DominiusPlay.Infrastructure.Security;

namespace DominiusPlay.Server.Services;

/// <summary>Resolve a configuração atual da Evolution API a partir do banco (chave descriptografada só no servidor).</summary>
public class EvolutionGateway(SettingsStore settings)
{
    public async Task<EvolutionConfig?> GetConfigAsync(CancellationToken ct = default)
    {
        var url = await settings.GetAsync(SettingKeys.EvolutionUrl, ct);
        var inst = await settings.GetAsync(SettingKeys.EvolutionInstance, ct);
        var key = await settings.GetAsync(SettingKeys.EvolutionApiKey, ct);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(inst) || string.IsNullOrWhiteSpace(key)) return null;
        return new EvolutionConfig(url, inst, key);
    }
}

/// <summary>Último estado conhecido da instância (atualizado pelo monitor e pelos webhooks connection.update).</summary>
public class InstanceStatusCache
{
    public volatile string? State;
    public DateTimeOffset CheckedAt;
    public string? Error;
    public bool IsOpen => string.Equals(State, "open", StringComparison.OrdinalIgnoreCase);
}
