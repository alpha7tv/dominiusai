using System.Text.Json;

namespace DominiusPlay.Desktop.Services;

/// <summary>Preferências locais do atendente (apenas endereço do servidor e último usuário — nenhuma senha ou chave).</summary>
public class LocalSettings
{
    public string ServerUrl { get; set; } = "http://localhost:5080";
    public string? LastUser { get; set; }

    private static string PathFile => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DominiusPlay", "CRM", "desktop.json");

    /// <summary>Versão para navegador: o servidor é sempre o próprio endereço da página.</summary>
    public static string? ForcedServerUrl { get; set; }

    public static LocalSettings Load()
    {
        if (ForcedServerUrl is { } forced) return new() { ServerUrl = forced };
        try { return File.Exists(PathFile) ? JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(PathFile)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save()
    {
        if (ForcedServerUrl is not null) return; // navegador: sem arquivo local
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!); File.WriteAllText(PathFile, JsonSerializer.Serialize(this)); }
        catch { /* preferências não são críticas */ }
    }
}
