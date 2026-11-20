using System.Text.RegularExpressions;

namespace DominiusPlay.Domain.Services;

/// <summary>
/// Personalização de modelos de mensagem. Variáveis aceitas:
/// {nome} → primeiro nome do contato (ou vazio); {, nome} → ", Fulano" somente quando há nome; {assistente} → nome da assistente virtual.
/// </summary>
public static partial class TemplateRenderer
{
    public static readonly string[] KnownVariables = ["{nome}", "{, nome}", "{assistente}"];

    public static string FirstName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var first = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (first.Any(char.IsDigit)) return ""; // "5511..." ou nome inválido: não personaliza
        return first.Length <= 1 ? first.ToUpperInvariant() : char.ToUpperInvariant(first[0]) + first[1..].ToLowerInvariant();
    }

    public static string Render(string body, string? leadName, string assistantName)
    {
        var first = FirstName(leadName);
        var text = body.Replace("{, nome}", first.Length > 0 ? ", " + first : "")
            .Replace("{nome}", first)
            .Replace("{assistente}", assistantName);
        return MultiSpace().Replace(text, " ").Replace(" ,", ",").Replace(" !", "!").Replace(" ?", "?").Trim();
    }

    /// <summary>Variáveis entre chaves que o sistema não conhece (erro de digitação no modelo).</summary>
    public static IReadOnlyList<string> UnknownVariables(string body) =>
        Variable().Matches(body).Select(m => m.Value).Where(v => !KnownVariables.Contains(v)).Distinct().ToList();

    /// <summary>Similaridade de Jaccard entre conjuntos de palavras (0 a 1). Usado para alertar modelos quase idênticos.</summary>
    public static double Similarity(string a, string b)
    {
        static HashSet<string> Words(string s) => Word().Matches(s.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 2).ToHashSet();
        var wa = Words(a); var wb = Words(b);
        if (wa.Count == 0 || wb.Count == 0) return 0;
        return (double)wa.Intersect(wb).Count() / wa.Union(wb).Count();
    }

    public const double SimilarityWarning = 0.8;

    [GeneratedRegex(@"[ \t]{2,}")] private static partial Regex MultiSpace();
    [GeneratedRegex(@"\{[^{}]*\}")] private static partial Regex Variable();
    [GeneratedRegex(@"[\p{L}\p{N}]+")] private static partial Regex Word();
}
