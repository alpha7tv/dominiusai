namespace DominiusPlay.Server.Services;

/// <summary>Registro de erros inesperados em arquivo (pasta logs do diretório de dados), para diagnóstico no Windows.</summary>
public static class ErrorLog
{
    private static readonly object Gate = new();

    public static void Write(string dataDir, string where, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(dataDir, "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"erros-{DateTime.Now:yyyyMMdd}.log");
            var text = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {where}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
            lock (Gate) File.AppendAllText(file, text);
            // mantém só os últimos 30 arquivos
            foreach (var old in new DirectoryInfo(dir).GetFiles("erros-*.log").OrderByDescending(f => f.Name).Skip(30)) old.Delete();
        }
        catch { /* o registro nunca pode derrubar a resposta */ }
    }
}
