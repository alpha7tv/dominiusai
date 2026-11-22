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

    public const string PasswordMask = "****";

    /// <summary>Esconde a senha de "https://usuario:senha@host" antes de devolver a URL para a tela.</summary>
    public static string? MaskUrl(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.UserInfo.Contains(':')) return url;
        var user = u.UserInfo[..u.UserInfo.IndexOf(':')];
        return url.Replace(u.UserInfo + "@", user + ":" + PasswordMask + "@");
    }

    /// <summary>Se a tela devolveu a senha mascarada, reaproveita a senha que já estava salva.</summary>
    public static string UnmaskUrl(string incoming, string? stored)
    {
        if (!Uri.TryCreate(incoming, UriKind.Absolute, out var inc) || !inc.UserInfo.EndsWith(":" + PasswordMask)) return incoming;
        if (stored is null || !Uri.TryCreate(stored, UriKind.Absolute, out var old) || !old.UserInfo.Contains(':')) return incoming;
        if (old.UserInfo.Split(':')[0] != inc.UserInfo.Split(':')[0] || old.Authority != inc.Authority) return incoming;
        return incoming.Replace(inc.UserInfo + "@", old.UserInfo + "@");
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
