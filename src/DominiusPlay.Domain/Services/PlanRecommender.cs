using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Domain.Services;

public enum CustomerProfile { Indefinido, A_Iniciante, B_RevendedorSigma, C_MarcaPropria, D_Indeciso }

public sealed record Recommendation(CustomerProfile Profile, IReadOnlyList<Plan> Plans, string Rationale, string? MissingInfo);

/// <summary>
/// Recomendação de planos (seção 11). Opera apenas sobre planos ATIVOS e APROVADOS vindos do banco —
/// nunca inventa preço. Retorna também qual informação falta para avançar o diagnóstico.
/// </summary>
public static class PlanRecommender
{
    /// <summary>Plano da tabela anterior (arquivado na atualização comercial de nov/2026; mantido para históricos).</summary>
    public const string Mensalista50 = "MENSALISTA_50";
    public const string Mensalista10 = PlanCodes.Mensalista10;
    public const string Mensalista100 = PlanCodes.Mensalista100;
    public const string Mensalista200 = PlanCodes.Mensalista200;
    public const string Mensalista500 = PlanCodes.Mensalista500;
    public const string Sigma600 = PlanCodes.Apontamento600;
    public const string ApontamentoIlimitado = PlanCodes.ApontamentoIlimitado;
    public const string Franquia = PlanCodes.Franquia;

    public static Recommendation Recommend(Lead lead, IEnumerable<Plan> plans, DateTimeOffset now, bool wantsOwnBrand = false)
    {
        var active = plans.Where(p => p.IsActiveAt(now)).ToDictionary(p => p.Code);
        Plan[] Pick(params string[] codes) => codes.Where(active.ContainsKey).Select(c => active[c]).ToArray();

        if (wantsOwnBrand || lead.MainPain == PainCategory.FaltaMarcaPropria)
            return new(CustomerProfile.C_MarcaPropria, Pick(Franquia),
                "Cliente quer marca própria (painel, DNS, app e identidade visual).", null);

        var usesSigma = lead.PanelUsed?.Contains("sigma", StringComparison.OrdinalIgnoreCase) == true;
        if (lead.Experience == IptvExperience.JaTrabalha && usesSigma)
        {
            if (lead.ActiveClients is null)
                return new(CustomerProfile.B_RevendedorSigma, [], "Revendedor com Painel Sigma.", "Quantidade de clientes ativos");
            var sigma = active.GetValueOrDefault(Sigma600);
            var limit = sigma?.MaxActiveLogins ?? 600;
            return lead.ActiveClients <= limit
                ? new(CustomerProfile.B_RevendedorSigma, Pick(Sigma600), $"Usa Sigma com {lead.ActiveClients} ativos (≤ {limit}).", null)
                : new(CustomerProfile.B_RevendedorSigma, Pick(ApontamentoIlimitado), $"Usa Sigma com {lead.ActiveClients} ativos (> {limit}).", null);
        }

        if (lead.Experience == IptvExperience.Iniciante)
        {
            var codes = lead.ActiveClients is > 50 ? new[] { Mensalista100 } : new[] { Mensalista10, Mensalista100 };
            return new(CustomerProfile.A_Iniciante, Pick(codes), "Está começando e busca investimento reduzido.", null);
        }

        if (lead.Experience == IptvExperience.JaTrabalha)
            return new(CustomerProfile.D_Indeciso, [], "Já trabalha com revenda.", lead.PanelUsed is null ? "Painel utilizado" : "Objetivo (manter painel ou estrutura própria)");

        return new(CustomerProfile.D_Indeciso, [], "Diagnóstico ainda incompleto.", "Se já trabalha com revenda ou está começando");
    }
}
