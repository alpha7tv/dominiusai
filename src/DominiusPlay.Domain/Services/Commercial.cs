using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DominiusPlay.Domain.Services;

/// <summary>
/// Configuração comercial editável no painel (Base de conhecimento → Comercial), sem mexer no código.
/// Preços dos planos ficam em Planos (versionados e aprovados); aqui ficam Pix, links, códigos, prazos e regras.
/// </summary>
public sealed record CommercialConfig
{
    public string CompanyName { get; init; } = "Dominius Play";
    public string Site { get; init; } = "https://dominiusplay.top";
    public string TestUrl { get; init; } = "https://dominiusplay.top/teste";
    public string PlayerUrl { get; init; } = "https://player.dominiusplay.top";
    public string PixKey { get; init; } = "supertv.tech@gmail.com";
    public string PaymentMethods { get; init; } = "Pix";
    public string AppCode { get; init; } = "6124702";
    public string AppLink { get; init; } = "https://aftv.news/6124702";
    public string P2pCode { get; init; } = "9604273";
    public string P2pLink { get; init; } = "https://aftv.news/9604273";
    public string SmartTvApps { get; init; } = "SmartUp, Smart STB";
    /// <summary>Aplicativos parceiros ativos (vazio = buscando novas parcerias).</summary>
    public string ActivePartnerApps { get; init; } = "";
    /// <summary>Parceiros antigos que encerraram a locação: a IA nunca os apresenta como ativos.</summary>
    public string FormerPartnerApps { get; init; } = "PlaySim, Assist+, Magic Player";
    public int TestHours { get; init; } = 4;
    public int PanelReleaseMinutes { get; init; } = 5;
    public int FranchiseDeliveryHours { get; init; } = 4;
    public int YearsInMarket { get; init; } = 10;
    public string ContractRequirements { get; init; } = "Primeiro nome e e-mail. Não exigimos CNPJ para a contratação inicial do painel de revenda.";
    public string SupportInfo { get; init; } = "Suporte 24 horas divulgado para a estrutura e parceiros; atendimento humano e prazos conforme o serviço contratado.";
    public string CreditRules { get; init; } = """
1 crédito = 1 ativação de 30 dias para um cliente.
Renovar por mais 30 dias consome outro crédito.
Uma ativação de 12 meses consome 12 créditos.
Todos os planos mensalistas têm créditos acumulativos, desde que as mensalidades estejam em dia.
Créditos são administrados pelo painel; créditos de sub-revendedores saem do saldo do painel principal.
Créditos não são ilimitados.
""".Trim();
    public string SubpanelRules { get; init; } = """
Criar subpainel (sub-revendedor) exige painel com permissão Master.
Os créditos distribuídos para sub-revendedores saem do saldo do painel principal.
O titular continua responsável pelo pagamento integral à Dominius Play e pela distribuição dos créditos.
O amigo/sub-revendedor não passa a ter contrato separado com a empresa; o rateio é combinado entre os participantes.
""".Trim();
    /// <summary>Ofertas promocionais vigentes (texto livre; vazio = nenhuma).</summary>
    public string Promotions { get; init; } = "";
    /// <summary>Oferta "painel dividido entre amigos" habilitada e o plano usado.</summary>
    public bool FriendSplitEnabled { get; init; } = true;
    public string FriendSplitPlanCode { get; init; } = PlanCodes.Mensalista500;
    /// <summary>Modalidade pós-paga por cliente ativo: só é oferecida se habilitada (precisa confirmação de vigência).</summary>
    public bool PostPaidEnabled { get; init; }
    public string PostPaidTiers { get; init; } = "Até 20 ativos: R$ 5,00 por ativo\nDe 21 a 50: R$ 3,00 por ativo\nDe 51 a 100: R$ 2,00 por ativo\nAcima de 100: R$ 1,50 por ativo";
    public decimal PostPaidAdhesionFee { get; init; } = 60m;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static CommercialConfig FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<CommercialConfig>(json, Json) ?? new(); } catch (JsonException) { return new(); }
    }

    /// <summary>Validação para salvar: links https, chave Pix presente, prazos positivos.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var (name, url) in new[] { ("Site", Site), ("Link de teste", TestUrl), ("Player web", PlayerUrl), ("Link do aplicativo", AppLink), ("Link do aplicativo P2P", P2pLink) })
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) errors.Add($"{name}: use um endereço https válido");
        if (string.IsNullOrWhiteSpace(PixKey) || PixKey.Length > 140) errors.Add("Chave Pix obrigatória (até 140 caracteres)");
        if (TestHours is < 1 or > 72) errors.Add("Duração do teste entre 1 e 72 horas");
        if (PanelReleaseMinutes is < 1 or > 1440) errors.Add("Prazo de liberação do painel entre 1 e 1440 minutos");
        if (FranchiseDeliveryHours is < 1 or > 720) errors.Add("Prazo da franquia entre 1 e 720 horas");
        if (!AppCode.All(char.IsAsciiDigit) || !P2pCode.All(char.IsAsciiDigit)) errors.Add("Códigos Downloader só com números");
        return errors;
    }
}

public static class PlanCodes
{
    public const string Mensalista10 = "MENSALISTA_10";
    public const string Mensalista100 = "MENSALISTA_100";
    public const string Mensalista200 = "MENSALISTA_200";
    public const string Mensalista500 = "MENSALISTA_500";
    public const string Apontamento600 = "APONTAMENTO_SIGMA_600";
    public const string ApontamentoIlimitado = "APONTAMENTO_ILIMITADO";
    public const string Franquia = "FRANQUIA_COMPLETA";
}

/// <summary>Regras de créditos e contas comerciais (puras, testáveis). Ilustrativas: margem bruta não é lucro líquido.</summary>
public static class CreditMath
{
    public const int DaysPerCredit = 30;
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Créditos consumidos por uma ativação/renovação de N dias (cada 30 dias, ou fração, consome 1 crédito).</summary>
    public static int CreditsForDays(int days) => days <= 0 ? 0 : (days + DaysPerCredit - 1) / DaysPerCredit;
    public static int CreditsForMonths(int months) => Math.Max(0, months);
    public static decimal CostPerCredit(decimal monthlyPrice, int credits) => credits <= 0 ? 0 : Math.Round(monthlyPrice / credits, 2);

    /// <summary>
    /// Saldo do mês seguinte: com a mensalidade em dia, o que sobrou acumula com os créditos do novo mês.
    /// Com atraso, o acúmulo não é garantido (conta só o novo mês, depois de pago) — as consequências exatas seguem o contrato.
    /// </summary>
    public static int NextBalance(int currentBalance, int used, int monthlyCredits, bool paidOnTime) =>
        paidOnTime ? Math.Max(0, currentBalance - used) + monthlyCredits : monthlyCredits;

    /// <summary>Sub-revendedor recebe créditos do saldo do painel principal (exige Master).</summary>
    public static (bool Ok, int MainBalance, int SubBalance, string? Error) TransferToSubpanel(int mainBalance, int amount, bool isMaster)
    {
        if (!isMaster) return (false, mainBalance, 0, "Criar subpainel exige painel com permissão Master");
        if (amount <= 0 || amount > mainBalance) return (false, mainBalance, 0, "Saldo insuficiente no painel principal");
        return (true, mainBalance - amount, amount, null);
    }

    public sealed record SplitResult(int CreditsEach, decimal AmountEach, decimal CostPerCredit);
    public static SplitResult Split(int credits, decimal monthlyPrice, int participants = 2) =>
        new(credits / participants, Math.Round(monthlyPrice / participants, 2), CostPerCredit(monthlyPrice, credits));

    public sealed record Margin(decimal Revenue, decimal Cost, decimal GrossMargin);
    /// <summary>Cenário ilustrativo: receita e margem BRUTA (antes de impostos e despesas). Não é garantia nem lucro líquido.</summary>
    public static Margin Scenario(int activationsSold, decimal salePrice, decimal packageCost) =>
        new(activationsSold * salePrice, packageCost, activationsSold * salePrice - packageCost);

    public static string Money(decimal v) => "R$ " + v.ToString("N2", PtBr);
}
