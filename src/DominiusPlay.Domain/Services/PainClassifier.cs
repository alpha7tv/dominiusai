using System.Globalization;
using System.Text;
using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Domain.Services;

/// <summary>
/// Classificação determinística (palavras-chave) das dores relatadas. Serve como base e como
/// fallback; a IA (Fase 5) poderá refinar, sempre registrando a origem como inferência.
/// </summary>
public static class PainClassifier
{
    private static readonly (PainCategory Cat, string[] Keys)[] Rules =
    [
        (PainCategory.TravamentosBuffering, ["trava", "travando", "buffer", "carregando", "congela", "lento", "lentidao"]),
        (PainCategory.QuedasServidor, ["cai ", "caiu", "queda", "cai muito", "fora do ar", "offline", "derruba", "instavel", "instabilidade"]),
        (PainCategory.SuporteInsuficiente, ["suporte", "nao responde", "demora para responder", "atendimento ruim", "sem resposta"]),
        (PainCategory.PrecosElevados, ["caro", "preco alto", "custo", "credito caro", "muito caro"]),
        (PainCategory.FaltaAutonomia, ["autonomia", "depender", "dependo", "liberdade"]),
        (PainCategory.PainelLimitado, ["painel limitado", "painel ruim", "painel fraco", "sem recursos", "limitado"]),
        (PainCategory.ProblemasAplicativos, ["aplicativo", "app ", "apk", "smart tv", "player"]),
        (PainCategory.DificuldadesClientesFinais, ["cliente reclama", "clientes reclamam", "cancelando", "perdendo cliente", "inadimpl"]),
        (PainCategory.FaltaMarcaPropria, ["marca propria", "minha marca", "meu proprio app", "minha logo", "identidade visual"]),
        (PainCategory.DificuldadesAtivacao, ["ativacao", "ativar", "liberar acesso", "nao ativa"]),
        (PainCategory.ProblemasFaturamento, ["cobranca", "faturamento", "pagamento", "pix", "boleto"]),
        (PainCategory.NecessidadeExpansao, ["crescer", "expandir", "escalar", "mais clientes", "aumentar"]),
        (PainCategory.FaltaConfiancaFornecedor, ["confianca", "confiar", "golpe", "sumiu", "fornecedor some"]),
    ];

    public static IReadOnlyList<PainCategory> Classify(string text)
    {
        var t = " " + Strip(text.ToLowerInvariant()) + " ";
        var found = new List<(PainCategory, int)>();
        foreach (var (cat, keys) in Rules)
        {
            var idx = keys.Select(k => t.IndexOf(k, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
            if (idx >= 0) found.Add((cat, idx));
        }
        return found.OrderBy(f => f.Item2).Select(f => f.Item1).ToList();
    }

    public static string Title(PainCategory p) => p switch
    {
        PainCategory.Nenhuma => "Não identificada",
        PainCategory.TravamentosBuffering => "Travamentos e buffering",
        PainCategory.QuedasServidor => "Quedas de servidores",
        PainCategory.SuporteInsuficiente => "Suporte insuficiente",
        PainCategory.PrecosElevados => "Preços elevados",
        PainCategory.FaltaAutonomia => "Falta de autonomia",
        PainCategory.PainelLimitado => "Painel limitado",
        PainCategory.ProblemasAplicativos => "Problemas com aplicativos",
        PainCategory.DificuldadesClientesFinais => "Dificuldades com clientes finais",
        PainCategory.FaltaMarcaPropria => "Falta de marca própria",
        PainCategory.DificuldadesAtivacao => "Dificuldades de ativação",
        PainCategory.ProblemasFaturamento => "Problemas de faturamento",
        PainCategory.NecessidadeExpansao => "Necessidade de expansão",
        PainCategory.FaltaConfiancaFornecedor => "Falta de confiança no fornecedor",
        _ => "Outros problemas",
    };

    public static string Strip(string s)
    {
        var n = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(n.Length);
        foreach (var c in n) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
