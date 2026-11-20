using System.Text.Json;

namespace DominiusPlay.Desktop.Services;

/// <summary>Preferências locais do atendente (apenas endereço do servidor e último usuário — nenhuma senha ou chave).</summary>
public class LocalSettings
{
    public string ServerUrl { get; set; } = "http://localhost:5080";
    public string? LastUser { get; set; }

    private static string PathFile => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DominiusPlay", "CRM", "desktop.json");

    public static LocalSettings Load()
    {
        try { return File.Exists(PathFile) ? JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(PathFile)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!); File.WriteAllText(PathFile, JsonSerializer.Serialize(this)); }
        catch { /* preferências não são críticas */ }
    }
}
