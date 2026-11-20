using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;

namespace DominiusPlay.Tests;

public class PhoneNormalizerTests
{
    [Theory]
    [InlineData("(14) 98815-9045", "5514988159045")]
    [InlineData("+55 14 98815-9045", "5514988159045")]
    [InlineData("5514988159045", "5514988159045")]
    [InlineData("014988159045", "5514988159045")]
    [InlineData("0 21 14 98815-9045", "5514988159045")]   // código de operadora
    [InlineData("11 8765-4321", "5511987654321")]         // celular antigo sem 9º dígito
    [InlineData("11 3456-7890", "551134567890")]          // fixo mantém 8 dígitos
    [InlineData("5514988159045@s.whatsapp.net", "5514988159045")]
    [InlineData("+351 912 345 678", "351912345678")]       // internacional
    public void Normaliza(string input, string expected)
    {
        var r = PhoneNormalizer.Normalize(input);
        Assert.True(r.IsValid, r.Error);
        Assert.Equal(expected, r.Normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12345")]
    [InlineData("(20) 98888-7777")]   // DDD inexistente
    [InlineData("(11) 88888-7777")]   // 9 dígitos sem começar com 9
    public void Rejeita(string input) => Assert.False(PhoneNormalizer.Normalize(input).IsValid);

    [Fact]
    public void Formata() => Assert.Equal("+55 (14) 98815-9045", PhoneNormalizer.Format("5514988159045"));
}

public class SendGuardTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 14, 0, 0, TimeSpan.Zero); // quarta-feira

    private static SendContext Ctx(Lead lead, OutboundKind kind = OutboundKind.AbordagemInicial, bool optedOut = false, bool connected = true,
        DateTimeOffset? lastInbound = null, DateTimeOffset? lastInitial = null, bool already = false, int sentToday = 0,
        Campaign? campaign = null, bool template = true, int failures = 0) =>
        new(lead, kind, Now, optedOut, connected, lastInbound, lastInitial, already, sentToday, 50, TimeSpan.FromMinutes(5), campaign, template, failures, Tz);

    private static Lead Consented() => new() { Phone = "5514988159045", ConsentStatus = ConsentStatus.Valido };

    [Fact] public void Permite_abordagem_com_consentimento() => Assert.True(SendGuard.Evaluate(Ctx(Consented())).Allowed);

    [Fact]
    public void Bloqueia_sem_consentimento()
    {
        var r = SendGuard.Evaluate(Ctx(new Lead { ConsentStatus = ConsentStatus.Pendente }));
        Assert.False(r.Allowed); Assert.Null(r.RetryAt); Assert.Contains("consentimento", r.Reason);
    }

    [Fact] public void Bloqueia_descadastrado() => Assert.False(SendGuard.Evaluate(Ctx(Consented(), optedOut: true)).Allowed);

    [Fact]
    public void Bloqueia_IA_durante_atendimento_humano()
    {
        var l = Consented(); l.Mode = ConversationMode.AtendimentoHumano;
        Assert.False(SendGuard.Evaluate(Ctx(l, OutboundKind.Resposta, lastInbound: Now.AddMinutes(-1))).Allowed);
        Assert.True(SendGuard.Evaluate(Ctx(l, OutboundKind.Humana, lastInbound: Now.AddMinutes(-1))).Allowed);
    }

    [Fact]
    public void Adia_pelo_intervalo_de_5_minutos()
    {
        var r = SendGuard.Evaluate(Ctx(Consented(), lastInitial: Now.AddMinutes(-2)));
        Assert.False(r.Allowed);
        Assert.Equal(Now.AddMinutes(3), r.RetryAt);
    }

    [Fact]
    public void Resposta_ignora_intervalo_mas_exige_janela_24h()
    {
        var l = Consented();
        Assert.True(SendGuard.Evaluate(Ctx(l, OutboundKind.Resposta, lastInbound: Now.AddMinutes(-1), lastInitial: Now.AddSeconds(-10))).Allowed);
        Assert.False(SendGuard.Evaluate(Ctx(l, OutboundKind.Resposta, lastInbound: Now.AddHours(-25))).Allowed);
    }

    [Fact] public void Bloqueia_contato_repetido_na_campanha() => Assert.False(SendGuard.Evaluate(Ctx(Consented(), already: true)).Allowed);

    [Fact] public void Fora_da_janela_exige_modelo_aprovado() => Assert.False(SendGuard.Evaluate(Ctx(Consented(), template: false)).Allowed);

    [Fact] public void Adia_quando_desconectado() => Assert.NotNull(SendGuard.Evaluate(Ctx(Consented(), connected: false)).RetryAt);

    [Fact] public void Adia_limite_diario() => Assert.NotNull(SendGuard.Evaluate(Ctx(Consented(), sentToday: 50)).RetryAt);

    [Fact] public void Bloqueia_apos_falhas_consecutivas() => Assert.False(SendGuard.Evaluate(Ctx(Consented(), failures: 3)).Allowed);

    [Fact]
    public void Respeita_horario_e_dias_da_campanha()
    {
        var camp = new Campaign { Status = CampaignStatus.Ativa, StartTime = new(15, 0), EndTime = new(18, 0), AllowedDays = "3" };
        var r = SendGuard.Evaluate(Ctx(Consented(), campaign: camp));
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero), r.RetryAt);
        camp.AllowedDays = "4"; // quinta
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 15, 0, 0, TimeSpan.Zero), SendGuard.Evaluate(Ctx(Consented(), campaign: camp)).RetryAt);
        camp.Status = CampaignStatus.Pausada;
        Assert.Null(SendGuard.Evaluate(Ctx(Consented(), campaign: camp)).RetryAt);
    }
}

public class KanbanAndPlanTests
{
    [Fact]
    public void IA_nao_marca_negocio_fechado()
    {
        var l = new Lead { Stage = KanbanStage.EmNegociacao };
        var d = KanbanRules.Evaluate(l, KanbanStage.NegocioFechado, MoveActor.Ia, commercialConfirmation: true);
        Assert.False(d.Applied); Assert.True(d.IsSuggestion);
    }

    [Fact]
    public void Atendente_precisa_confirmacao_para_fechar()
    {
        var l = new Lead { Stage = KanbanStage.EmNegociacao };
        Assert.False(KanbanRules.Evaluate(l, KanbanStage.NegocioFechado, MoveActor.Atendente, false).Applied);
        Assert.True(KanbanRules.Evaluate(l, KanbanStage.NegocioFechado, MoveActor.Atendente, true).Applied);
    }

    [Fact]
    public void Apply_registra_historico()
    {
        var l = new Lead();
        var c = KanbanRules.Apply(l, KanbanStage.EmDuvidas, "carlos", "pediu detalhes");
        Assert.Equal(KanbanStage.PrimeiroContato, c.From); Assert.Equal(KanbanStage.EmDuvidas, l.Stage); Assert.Single(l.StageChanges);
    }

    private static List<Plan> Plans() => DominiusPlay.Infrastructure.Data.SeedData.CampaignPlans().ToList();

    [Fact]
    public void Sigma_350_recomenda_apontamento_600()
    {
        var l = new Lead { Experience = IptvExperience.JaTrabalha, PanelUsed = "Painel Sigma", ActiveClients = 350 };
        var r = PlanRecommender.Recommend(l, Plans(), DateTimeOffset.UtcNow);
        Assert.Equal(CustomerProfile.B_RevendedorSigma, r.Profile);
        Assert.Equal(250m, Assert.Single(r.Plans).MonthlyPrice);
    }

    [Fact]
    public void Sigma_acima_de_600_recomenda_ilimitado()
    {
        var l = new Lead { Experience = IptvExperience.JaTrabalha, PanelUsed = "sigma", ActiveClients = 900 };
        Assert.Equal(PlanRecommender.ApontamentoIlimitado, Assert.Single(PlanRecommender.Recommend(l, Plans(), DateTimeOffset.UtcNow).Plans).Code);
    }

    [Fact]
    public void Sigma_sem_quantidade_pede_informacao()
    {
        var r = PlanRecommender.Recommend(new Lead { Experience = IptvExperience.JaTrabalha, PanelUsed = "Sigma" }, Plans(), DateTimeOffset.UtcNow);
        Assert.Empty(r.Plans); Assert.Equal("Quantidade de clientes ativos", r.MissingInfo);
    }

    [Fact]
    public void Iniciante_recebe_mensalistas_e_marca_propria_franquia()
    {
        Assert.Equal(2, PlanRecommender.Recommend(new Lead { Experience = IptvExperience.Iniciante }, Plans(), DateTimeOffset.UtcNow).Plans.Count);
        Assert.Equal(650m, PlanRecommender.Recommend(new Lead(), Plans(), DateTimeOffset.UtcNow, wantsOwnBrand: true).Plans.Single().MonthlyPrice);
    }

    [Fact]
    public void Plano_nao_aprovado_ou_vencido_nao_e_oferecido()
    {
        var plans = Plans();
        plans.First(p => p.Code == PlanRecommender.Sigma600).ValidUntil = DateTimeOffset.UtcNow.AddDays(-1);
        var r = PlanRecommender.Recommend(new Lead { Experience = IptvExperience.JaTrabalha, PanelUsed = "sigma", ActiveClients = 100 }, plans, DateTimeOffset.UtcNow);
        Assert.Empty(r.Plans);
    }

    [Fact]
    public void Indeciso_continua_diagnostico() =>
        Assert.Equal(CustomerProfile.D_Indeciso, PlanRecommender.Recommend(new Lead(), Plans(), DateTimeOffset.UtcNow).Profile);

    [Fact]
    public void Classifica_dores_da_conversa_exemplo()
    {
        var pains = PainClassifier.Classify("Meu servidor cai muito e tenho 350 clientes, trava nos jogos");
        Assert.Equal(PainCategory.QuedasServidor, pains[0]);
        Assert.Contains(PainCategory.TravamentosBuffering, pains);
    }

    [Theory]
    [InlineData("Quero sair da lista", true)]
    [InlineData("PARE", true)]
    [InlineData("Não quero receber mais mensagens", true)]
    [InlineData("Quanto custa o plano?", false)]
    public void Detecta_descadastro(string text, bool expected) => Assert.Equal(expected, OptOutDetector.IsOptOut(text));
}
