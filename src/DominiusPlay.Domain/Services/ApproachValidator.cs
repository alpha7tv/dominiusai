using System.Text.RegularExpressions;

namespace DominiusPlay.Domain.Services;

/// <summary>
/// Confere uma abordagem gerada pela IA contra o modelo aprovado antes de enviar. A IA só pode variar a redação:
/// não pode inventar preço, link, número, nem remover a identificação da empresa ou a opção de saída.
/// </summary>
public static partial class ApproachValidator
{
    [GeneratedRegex(@"https?://|www\.|\.com\b|\.br\b", RegexOptions.IgnoreCase)] private static partial Regex Url();
    [GeneratedRegex(@"R\$\s*\d|\d+[.,]\d{2}\b|\d+\s*reais", RegexOptions.IgnoreCase)] private static partial Regex Price();
    [GeneratedRegex(@"\d{4,}")] private static partial Regex LongNumber();

    public static string? Validate(string generated, string approved, string company, string? leadName)
    {
        var g = generated.Trim();
        if (g.Length < 15) return "texto curto demais";
        if (g.Length > Math.Max(600, approved.Length * 2)) return "texto longo demais";
        if (g.Contains('{') || g.Contains('}')) return "variável não substituída";
        if (Url().IsMatch(g) && !Url().IsMatch(approved)) return "link que não está no modelo aprovado";
        if (Price().IsMatch(g) && !Price().IsMatch(approved)) return "preço/valor que não está no modelo aprovado";
        if (LongNumber().IsMatch(g) && !LongNumber().IsMatch(approved)) return "número que não está no modelo aprovado";
        if (!string.IsNullOrWhiteSpace(company) && Contains(approved, company) && !Contains(g, company)) return $"sem a identificação da empresa ({company})";
        foreach (var exit in new[] { "SAIR", "PARAR", "STOP" })
            if (Contains(approved, exit) && !g.Contains(exit, StringComparison.OrdinalIgnoreCase)) return $"sem a opção de saída ({exit})";
        foreach (var fake in new[] { "como combinamos", "conforme conversamos", "lembra de mim", "nossa conversa", "como você pediu" })
            if (Contains(g, fake)) return "finge relação prévia";
        return null;
    }

    private static bool Contains(string text, string part) =>
        PainClassifier.Strip(text.ToLowerInvariant()).Contains(PainClassifier.Strip(part.ToLowerInvariant()));
}
