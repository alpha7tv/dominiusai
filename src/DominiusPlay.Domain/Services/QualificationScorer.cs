using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Domain.Services;

public sealed record ScoreLine(int Points, string Reason);
public sealed record QualificationScore(int Score, IReadOnlyList<ScoreLine> Lines, IReadOnlyList<string> Pending)
{
    public string Explanation => string.Join("\n", Lines.Select(l => $"{(l.Points >= 0 ? "+" : "")}{l.Points} {l.Reason}"))
        + (Pending.Count > 0 ? "\nPendentes: " + string.Join("; ", Pending) : "");
}

/// <summary>
/// Pontuação de qualificação explicável (0–100), baseada apenas em critérios comerciais.
/// Com perguntas cadastradas: soma o peso das respostas que atendem ao critério / soma dos pesos.
/// Sem perguntas: usa os campos de qualificação do lead (experiência, carteira, dor, plano, investimento, próximo passo).
/// Respostas ficam na memória do lead como fatos "qualificacao:{chave}" com valor "[sim] ..." ou "[nao] ...".
/// </summary>
public static class QualificationScorer
{
    public const string FactPrefix = "qualificacao:";

    public static string FactValue(bool meetsCriterion, string answer) => (meetsCriterion ? "[sim] " : "[nao] ") + answer.Trim();

    public static QualificationScore Compute(Lead lead, IReadOnlyList<QualificationQuestion> questions)
    {
        var active = questions.Where(q => q.Active && q.Weight > 0).OrderBy(q => q.Order).ToList();
        return active.Count > 0 ? FromQuestions(lead, active) : FromLeadFields(lead);
    }

    private static QualificationScore FromQuestions(Lead lead, List<QualificationQuestion> qs)
    {
        var answers = lead.Facts.Where(f => f.Key.StartsWith(FactPrefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.Key[FactPrefix.Length..].ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.CreatedAt).Last());
        var total = qs.Sum(q => q.Weight);
        var lines = new List<ScoreLine>();
        var pending = new List<string>();
        var got = 0;
        foreach (var q in qs)
        {
            if (!answers.TryGetValue(q.Key.ToLowerInvariant(), out var f)) { pending.Add(q.Question); continue; }
            var meets = f.Value.StartsWith("[sim]", StringComparison.OrdinalIgnoreCase);
            var answer = f.Value.Length > 5 && f.Value.StartsWith('[') ? f.Value[(f.Value.IndexOf(']') + 1)..].Trim() : f.Value;
            var pts = meets ? q.Weight : 0;
            got += pts;
            lines.Add(new(Scale(pts, total), $"{q.Question} → \"{answer}\"" + (meets ? "" : " (não atende ao critério)")));
        }
        return new(Math.Clamp(Scale(got, total), 0, 100), lines, pending);
    }

    private static int Scale(int pts, int total) => total <= 0 ? 0 : (int)Math.Round(pts * 100.0 / total);

    private static QualificationScore FromLeadFields(Lead l)
    {
        var lines = new List<ScoreLine>();
        var pending = new List<string>();
        void Add(bool cond, int pts, string reason, string missing) { if (cond) lines.Add(new(pts, reason)); else pending.Add(missing); }
        Add(l.Experience != IptvExperience.NaoInformado, 15, l.Experience == IptvExperience.JaTrabalha ? "Já trabalha com revenda" : "Está começando (perfil identificado)", "Já trabalha ou está começando?");
        Add(l.ActiveClients is > 0, 20, $"Tem carteira ativa ({l.ActiveClients} clientes)", "Quantos clientes ativos?");
        Add(l.MainPain != PainCategory.Nenhuma, 15, "Dificuldade principal identificada: " + PainClassifier.Title(l.MainPain), "Qual a principal dificuldade?");
        Add(!string.IsNullOrWhiteSpace(l.InterestPlanCode), 25, "Plano de interesse definido: " + l.InterestPlanCode, "Qual solução procura?");
        Add(!string.IsNullOrWhiteSpace(l.InvestmentRange), 10, "Faixa de investimento informada: " + l.InvestmentRange, "Previsão de investimento/contratação?");
        var proposal = l.Facts.Any(f => f.Key is "proposta_enviada" or "pediu_proposta" or "compromisso") || l.Stage == KanbanStage.EmNegociacao;
        Add(proposal, 15, "Pediu proposta ou está em negociação", "Deseja receber uma proposta?");
        return new(Math.Clamp(lines.Sum(x => x.Points), 0, 100), lines, pending);
    }

    /// <summary>Perguntas padrão sugeridas (o administrador edita na tela de agentes).</summary>
    public static IReadOnlyList<QualificationQuestion> Defaults() =>
    [
        // Roteiro de revenda IPTV (uma pergunta por vez, nesta ordem)
        new() { Order = 1, Key = "ja_revende", Question = "Já trabalha com revenda ou está começando?", Weight = 15, ScoringHint = "já revende (iniciante com plano de começar também pontua metade: registre atende_criterio=false)" },
        new() { Order = 2, Key = "clientes_ativos", Question = "Quantos clientes ativos tem hoje, mais ou menos?", Weight = 20, ScoringHint = "tem carteira ativa (mais de 50 clientes)" },
        new() { Order = 3, Key = "painel_dor", Question = "Usa qual painel hoje e o que mais incomoda nele?", Weight = 15, ScoringHint = "citou uma dificuldade concreta que nossos planos resolvem (travamento, suporte, preço do crédito, marca própria)" },
        new() { Order = 4, Key = "marca_propria", Question = "Quer marca própria (app/logo) ou só créditos?", Weight = 15, ScoringHint = "definiu o tipo de solução que procura" },
        new() { Order = 5, Key = "volume_ativacoes", Question = "Quantas ativações por mês você imagina?", Weight = 15, ScoringHint = "informou um volume compatível com um plano aprovado" },
        new() { Order = 6, Key = "proposta", Question = "Quer que eu te passe para o time fechar ou te mande a proposta?", Weight = 20, ScoringHint = "sim, quer proposta ou falar com o time" },
    ];
}
