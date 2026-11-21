using System.Globalization;
using System.Text.RegularExpressions;
using DominiusPlay.Domain.Entities;

namespace DominiusPlay.Domain.Services;

public sealed record KbMatch(KbEntry Entry, double Score, string MatchedText);

/// <summary>
/// Busca semântica leve e determinística (sem banco vetorial): normaliza o texto (sem acentos, radicais simples),
/// compara com a pergunta e as variações de cada entrada e soma palavras-chave e o produto em discussão.
/// Roda no servidor a cada mensagem, de graça, e alimenta a IA com as respostas oficiais mais relevantes.
/// </summary>
public static class KnowledgeMatcher
{
    public const double MinScore = 0.34;

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a","o","as","os","um","uma","uns","umas","de","da","do","das","dos","e","em","no","na","nos","nas","por","para","pra","pro","com","sem",
        "que","se","eu","voce","voces","me","meu","minha","meus","minhas","seu","sua","ao","aos","ou","ja","tem","ter","ha","isso","esse","essa",
        "e","eh","ser","sao","fica","ficar","qual","quais","como","quando","onde","quanto","vou","vai","posso","pode","consigo","da","dar","mais",
        "muito","tambem","so","ai","oi","ola","la","aqui","ne","entao","sobre","ate","depois","antes","ainda","bem","tudo","nao","sim","faz","fazer",
    };

    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var chars = PainClassifier.Strip(s.ToLowerInvariant()).Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Radical simples: palavras longas viram os 6 primeiros caracteres (créditos/crédito → credit); curtas perdem o "s" final.</summary>
    public static string Stem(string w) => w.Length > 6 ? w[..6] : w.Length > 3 && w.EndsWith('s') ? w[..^1] : w;

    public static HashSet<string> Tokens(string? s) =>
        Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Stop.Contains(w) && (w.Length > 1 || char.IsDigit(w[0]))).Select(Stem).ToHashSet();

    private static double Dice(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Count(b.Contains);
        return 2.0 * inter / (a.Count + b.Count);
    }

    /// <summary>
    /// Entradas mais relevantes para a mensagem. <paramref name="productContext"/> são códigos de produtos já em discussão
    /// (ex.: APONTAMENTO_SIGMA_600) e dão preferência às respostas do mesmo produto.
    /// </summary>
    public static IReadOnlyList<KbMatch> Search(string? message, IEnumerable<KbEntry> entries, IReadOnlyCollection<string>? productContext = null, int take = 3, double minScore = MinScore)
    {
        var norm = " " + Normalize(message) + " ";
        var q = Tokens(message);
        if (q.Count == 0) return [];
        var results = new List<KbMatch>();
        foreach (var e in entries)
        {
            var best = 0.0; var bestText = e.Question;
            foreach (var cand in new[] { e.Question }.Concat(e.Variations.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                var s = Dice(q, Tokens(cand));
                var nc = Normalize(cand);
                if (nc.Length > 0 && norm.Contains(" " + nc + " ")) s = Math.Max(s, 0.9); // frase inteira contida na mensagem
                if (s > best) { best = s; bestText = cand; }
            }
            var kw = 0.0;
            foreach (var k in e.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var nk = Normalize(k);
                if (nk.Length > 1 && norm.Contains(" " + nk + " ")) kw += nk.Contains(' ') ? 0.2 : 0.12;
            }
            var score = best + Math.Min(kw, 0.4);
            if (productContext is { Count: > 0 } && e.Products.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(productContext.Contains))
                score += 0.08;
            score += (5 - Math.Clamp(e.Priority, 1, 5)) * 0.01;
            if (score >= minScore) results.Add(new KbMatch(e, Math.Round(score, 3), bestText));
        }
        return results.OrderByDescending(r => r.Score).ThenBy(r => r.Entry.Priority).ThenBy(r => r.Entry.Code).Take(take).ToList();
    }
}

/// <summary>Preenche os marcadores das respostas com a Configuração comercial e os Planos aprovados.</summary>
public static class KnowledgeRenderer
{
    private static readonly Regex Placeholder = new(@"\{(?<k>[a-z0-9_]+)(?::(?<p>[A-Z0-9_]+))?\}", RegexOptions.Compiled);
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    public const string Missing = "[valor a confirmar com a equipe]";

    public static readonly (string Key, string Description)[] Placeholders =
    [
        ("{empresa}", "Nome da empresa"), ("{site}", "Site"), ("{link_teste}", "Link para solicitar teste"), ("{link_player}", "Player web"),
        ("{pix}", "Chave Pix"), ("{forma_pagamento}", "Formas de pagamento"), ("{app_codigo}", "Código Downloader do app oficial"), ("{app_link}", "Link do app oficial"),
        ("{p2p_codigo}", "Código Downloader do app P2P"), ("{p2p_link}", "Link do app P2P"), ("{smarttv_apps}", "Apps para Smart TV antigas"),
        ("{parceiros_antigos}", "Parceiros que encerraram a locação"), ("{teste_horas}", "Duração do teste (h)"), ("{liberacao_minutos}", "Liberação do painel após Pix (min)"),
        ("{franquia_horas}", "Prazo de entrega da franquia (h)"), ("{anos_mercado}", "Anos de mercado"), ("{adesao_pospago}", "Taxa de adesão pós-pago"),
        ("{preco:CODIGO}", "Preço mensal do plano aprovado"), ("{creditos:CODIGO}", "Créditos do plano"), ("{custo_credito:CODIGO}", "Custo por crédito do plano"),
        ("{logins:CODIGO}", "Limite de logins ativos do plano"),
    ];

    public sealed record Rendered(string Text, IReadOnlyList<string> Warnings);

    public static Rendered Render(string template, CommercialConfig cfg, IReadOnlyList<Plan> activePlans)
    {
        var warnings = new List<string>();
        var text = Placeholder.Replace(template, m =>
        {
            var k = m.Groups["k"].Value; var code = m.Groups["p"].Value;
            if (code.Length > 0)
            {
                var plan = activePlans.FirstOrDefault(p => p.Code == code);
                if (plan is null) { warnings.Add($"Plano {code} não está aprovado/ativo"); return Missing; }
                switch (k)
                {
                    case "preco": return CreditMath.Money(plan.MonthlyPrice);
                    case "creditos": if (plan.Credits is { } c) return c.ToString(PtBr); break;
                    case "custo_credito": if (plan.Credits is { } c2 && c2 > 0) return CreditMath.Money(CreditMath.CostPerCredit(plan.MonthlyPrice, c2)); break;
                    case "logins": if (plan.MaxActiveLogins is { } l) return l.ToString(PtBr); break;
                }
                warnings.Add($"Marcador {m.Value} sem valor no plano {code}"); return Missing;
            }
            string? v = k switch
            {
                "empresa" => cfg.CompanyName, "site" => cfg.Site, "link_teste" => cfg.TestUrl, "link_player" => cfg.PlayerUrl, "pix" => cfg.PixKey,
                "forma_pagamento" => cfg.PaymentMethods, "app_codigo" => cfg.AppCode, "app_link" => cfg.AppLink, "p2p_codigo" => cfg.P2pCode, "p2p_link" => cfg.P2pLink,
                "smarttv_apps" => cfg.SmartTvApps, "parceiros_antigos" => cfg.FormerPartnerApps, "teste_horas" => cfg.TestHours.ToString(PtBr),
                "liberacao_minutos" => cfg.PanelReleaseMinutes.ToString(PtBr), "franquia_horas" => cfg.FranchiseDeliveryHours.ToString(PtBr),
                "anos_mercado" => cfg.YearsInMarket.ToString(PtBr), "adesao_pospago" => CreditMath.Money(cfg.PostPaidAdhesionFee),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(v)) { warnings.Add($"Marcador {m.Value} desconhecido ou vazio"); return Missing; }
            return v;
        });
        return new Rendered(text, warnings);
    }

    /// <summary>Marcadores usados no texto que não existem (para validar antes de salvar).</summary>
    public static IReadOnlyList<string> UnknownPlaceholders(string template)
    {
        var known = new HashSet<string> { "empresa","site","link_teste","link_player","pix","forma_pagamento","app_codigo","app_link","p2p_codigo","p2p_link",
            "smarttv_apps","parceiros_antigos","teste_horas","liberacao_minutos","franquia_horas","anos_mercado","adesao_pospago" };
        var planKeys = new HashSet<string> { "preco", "creditos", "custo_credito", "logins" };
        return Placeholder.Matches(template).Where(m => m.Groups["p"].Success && m.Groups["p"].Value.Length > 0 ? !planKeys.Contains(m.Groups["k"].Value) : !known.Contains(m.Groups["k"].Value))
            .Select(m => m.Value).Distinct().ToList();
    }
}

/// <summary>Histórico e cópia de entradas da base.</summary>
public static class KbHistory
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { WriteIndented = false };

    public sealed record Snapshot(string Base, string Code, string Category, string Question, string Variations, string Answer, string Keywords,
        string Products, int Priority, KbStatus Status, string Source, bool NeedsConfirmation, int Version);

    public static Snapshot Of(KbEntry e) => new(e.Base, e.Code, e.Category, e.Question, e.Variations, e.Answer, e.Keywords, e.Products, e.Priority, e.Status, e.Source, e.NeedsConfirmation, e.Version);

    public static KbEntryRevision Revision(KbEntry e, string action, string by) => new()
    {
        EntryId = e.Id, Code = e.Code, Version = e.Version, Action = action, ChangedBy = by,
        Snapshot = System.Text.Json.JsonSerializer.Serialize(Of(e), Json),
    };

    /// <summary>Validação de uma entrada antes de salvar.</summary>
    public static IReadOnlyList<string> Validate(string code, string category, string question, string answer, int priority)
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(code) || code.Length > 40 || !code.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')) e.Add("Código obrigatório (letras, números, - ou _; até 40)");
        if (string.IsNullOrWhiteSpace(category) || category.Length > 120) e.Add("Categoria obrigatória (até 120 caracteres)");
        if (string.IsNullOrWhiteSpace(question) || question.Length > 500) e.Add("Pergunta obrigatória (até 500 caracteres)");
        if (string.IsNullOrWhiteSpace(answer) || answer.Length > 4000) e.Add("Resposta obrigatória (até 4000 caracteres)");
        if (priority is < 1 or > 5) e.Add("Prioridade de 1 (mais alta) a 5");
        foreach (var u in KnowledgeRenderer.UnknownPlaceholders(answer)) e.Add($"Marcador desconhecido: {u}");
        return e;
    }
}
