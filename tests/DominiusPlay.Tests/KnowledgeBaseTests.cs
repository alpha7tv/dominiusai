using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Security;
using DominiusPlay.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DominiusPlay.Tests;

/// <summary>
/// Parte 13 do Prompt Mestre Dominius Play: os 35 testes obrigatórios (T01–T35), mais testes da integração
/// (edição sem reiniciar, rascunho, importação/exportação, histórico e atualização de banco antigo).
/// </summary>
public class KnowledgeBaseTests
{
    private static readonly IReadOnlyList<KbEntry> Entries = DominiusKnowledge.Entries();
    private static readonly List<Plan> Plans = SeedData.CampaignPlans().ToList();
    private static readonly CommercialConfig Cfg = new();
    private static Plan P(string code) => Plans.Single(p => p.Code == code);
    private static IReadOnlyList<LabStep> Lab(params string[] msgs) => AttendanceLab.Run(msgs, Entries, Cfg, Plans);
    private static string Reply(params string[] msgs) => Lab(msgs)[^1].Reply ?? "";

    // ---------------------------------------------------------------- base
    [Fact]
    public async Task T01_todas_as_75_perguntas_estao_cadastradas_com_todos_os_campos()
    {
        Assert.Equal(75, Entries.Count);
        Assert.Equal(75, Entries.Select(e => e.Code).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 75).Select(DominiusKnowledge.Code), Entries.Select(e => e.Code));
        Assert.All(Entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Category)); Assert.False(string.IsNullOrWhiteSpace(e.Question)); Assert.False(string.IsNullOrWhiteSpace(e.Answer));
            Assert.False(string.IsNullOrWhiteSpace(e.Variations)); Assert.False(string.IsNullOrWhiteSpace(e.Keywords)); Assert.InRange(e.Priority, 1, 5);
            Assert.Equal(KbStatus.Publicado, e.Status); Assert.False(string.IsNullOrWhiteSpace(e.Source));
            Assert.Empty(KbHistory.Validate(e.Code, e.Category, e.Question, e.Answer, e.Priority));
        });
        Assert.Equal(7, Entries.Select(e => e.Category).Distinct().Count());

        // no servidor: carregadas no banco, publicadas, com histórico de criação
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var list = await c.GetFromJsonAsync<List<KbEntryDto>>("/api/kb");
        Assert.Equal(75, list!.Count);
        Assert.All(list, e => { Assert.Equal(KbStatus.Publicado, e.Status); Assert.DoesNotContain("{", e.RenderedAnswer); Assert.Empty(e.Warnings); });
        Assert.Equal(75, await f.WithDb(db => db.KbEntryRevisions.CountAsync()));
    }

    [Fact]
    public void T02_respostas_sao_recuperadas_corretamente()
    {
        foreach (var e in Entries)
            Assert.Equal(e.Code, KnowledgeMatcher.Search(e.Question, Entries).First().Entry.Code);
        // perguntas reescritas pelo cliente, sem copiar o texto da base
        (string Msg, int Expected)[] paraphrases =
        [
            ("quanto eu preciso pra comecar a revender?", 1), ("me explica como funciona essa revenda de iptv", 2), ("o servidor de vcs trava muito?", 3),
            ("os creditos que sobram acumulam pro mes seguinte?", 6), ("aceita cartao ou so pix?", 46), ("vcs emitem nota fiscal?", 70),
            ("da pra criar subpainel pra outro revendedor?", 53), ("tem teste gratis?", 75), ("qual o codigo do downloader?", 73),
            ("quanto custa a franquia completa?", 16), ("preciso ter cnpj?", 24), ("depois do pix em quanto tempo libera o painel?", 74),
            ("vcs trabalham com playsim?", 71), ("posso rachar o painel com um amigo?", 72), ("tem dns exclusiva?", 14),
            ("o que acontece se eu atrasar a mensalidade?", 49), ("quanto tempo vcs tem de mercado?", 63), ("funciona em tv box?", 55),
        ];
        foreach (var (msg, expected) in paraphrases)
            Assert.Equal(DominiusKnowledge.Code(expected), KnowledgeMatcher.Search(msg, Entries).First().Entry.Code);
    }

    // ---------------------------------------------------------------- planos e créditos
    [Fact]
    public async Task T03_painel_inicial_custa_30_reais_por_10_creditos()
    {
        Assert.Equal(30m, P(PlanCodes.Mensalista10).MonthlyPrice); Assert.Equal(10, P(PlanCodes.Mensalista10).Credits);
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var active = (await c.GetFromJsonAsync<List<PlanDto>>("/api/plans"))!.Where(p => p.Approval == ApprovalStatus.Aprovado).ToList();
        Assert.Contains(active, p => p.Code == PlanCodes.Mensalista10 && p.MonthlyPrice == 30m && p.Credits == 10);
        Assert.Contains("R$ 30,00 mensais e receber 10 créditos", Reply("Quanto custa para começar a revender?"));
    }

    [Fact] public void T04_custo_unitario_do_plano_inicial_e_3_reais() =>
        Assert.Equal(3.00m, CreditMath.CostPerCredit(P(PlanCodes.Mensalista10).MonthlyPrice, P(PlanCodes.Mensalista10).Credits!.Value));

    [Fact] public void T05_pacote_de_100_creditos_custa_80() { Assert.Equal(80m, P(PlanCodes.Mensalista100).MonthlyPrice); Assert.Equal(100, P(PlanCodes.Mensalista100).Credits); Assert.Equal(0.80m, CreditMath.CostPerCredit(80m, 100)); }
    [Fact] public void T06_pacote_de_200_creditos_custa_100() { Assert.Equal(100m, P(PlanCodes.Mensalista200).MonthlyPrice); Assert.Equal(200, P(PlanCodes.Mensalista200).Credits); Assert.Equal(0.50m, CreditMath.CostPerCredit(100m, 200)); }
    [Fact] public void T07_pacote_de_500_creditos_custa_200() { Assert.Equal(200m, P(PlanCodes.Mensalista500).MonthlyPrice); Assert.Equal(500, P(PlanCodes.Mensalista500).Credits); Assert.Equal(0.40m, CreditMath.CostPerCredit(200m, 500)); }

    [Fact]
    public void T08_um_credito_equivale_a_30_dias()
    {
        Assert.Equal(1, CreditMath.CreditsForDays(30));
        Assert.Equal(30, CreditMath.DaysPerCredit);
        Assert.Contains("Cada crédito permite uma ativação de 30 dias", Reply("Como funciona o sistema de créditos?"));
    }

    [Fact]
    public void T09_renovacao_de_30_dias_consome_mais_um_credito()
    {
        var activation = CreditMath.CreditsForDays(30);
        var renewal = CreditMath.CreditsForDays(30);
        Assert.Equal(2, activation + renewal);
        Assert.Equal(2, CreditMath.CreditsForDays(60)); Assert.Equal(3, CreditMath.CreditsForDays(90));
        Assert.Contains("Cada período adicional de 30 dias consome 1 crédito", Reply("Como faço para renovar uma assinatura?"));
    }

    [Fact]
    public void T10_ativacao_de_12_meses_consome_12_creditos()
    {
        Assert.Equal(12, CreditMath.CreditsForMonths(12)); Assert.Equal(12, CreditMath.CreditsForDays(360));
        Assert.Contains("por 12 meses, 12 créditos", Reply("Quanto crédito consome um cliente de 12 meses?"));
    }

    [Fact]
    public void T11_creditos_acumulam_com_pagamento_em_dia()
    {
        Assert.Equal(17, CreditMath.NextBalance(currentBalance: 10, used: 3, monthlyCredits: 10, paidOnTime: true));
        Assert.Equal(10, CreditMath.NextBalance(currentBalance: 10, used: 3, monthlyCredits: 10, paidOnTime: false)); // atraso: não acumula
        var r = Reply("Os créditos vencem ou acumulam?");
        Assert.Contains("acumulam", r); Assert.Contains("pagas em dia", r);
    }

    [Fact]
    public void T12_sub_revendedores_usam_os_creditos_do_painel_principal()
    {
        var t = CreditMath.TransferToSubpanel(mainBalance: 500, amount: 250, isMaster: true);
        Assert.True(t.Ok); Assert.Equal(250, t.MainBalance); Assert.Equal(250, t.SubBalance);
        Assert.False(CreditMath.TransferToSubpanel(100, 250, true).Ok); // não cria crédito do nada
        Assert.Contains("descontados do saldo do seu painel principal", Reply("Posso criar sub-revendedores?"));
    }

    [Fact]
    public void T13_criacao_de_subpainel_exige_permissao_Master()
    {
        var t = CreditMath.TransferToSubpanel(500, 250, isMaster: false);
        Assert.False(t.Ok); Assert.Contains("Master", t.Error);
        var sub = Reply("Posso criar sub-revendedores?");
        Assert.Contains("a partir de R$ 100,00 mensais já saem com permissão Master: Mensalista 200 (R$ 100,00) e Mensalista 500 (R$ 200,00)", sub);
        Assert.DoesNotContain("Mensalista 100 (", sub); // R$ 80: sem Master
        Assert.Contains("já sai com permissão Master", Reply("Posso dividir um painel com um amigo para pagar menos?"));
        Assert.True(Cfg.HasMaster(P(PlanCodes.Mensalista200))); Assert.False(Cfg.HasMaster(P(PlanCodes.Mensalista100))); Assert.False(Cfg.HasMaster(P(PlanCodes.Mensalista50)));
    }

    [Fact] public void T14_rateio_de_500_creditos_em_duas_partes_da_250_para_cada() => Assert.Equal(250, CreditMath.Split(500, 200m).CreditsEach);
    [Fact] public void T15_rateio_financeiro_da_100_reais_por_pessoa() => Assert.Equal(100m, CreditMath.Split(500, 200m).AmountEach);

    [Fact]
    public void T16_custo_por_credito_no_pacote_de_500_e_40_centavos()
    {
        Assert.Equal(0.40m, CreditMath.Split(500, 200m).CostPerCredit);
        var offer = CommercialTriggers.OfferMessage(CommercialTrigger.AmigoInteressado, Cfg, Plans)!;
        Assert.Contains("500 créditos por R$ 200,00", offer); Assert.Contains("250 créditos", offer); Assert.Contains("R$ 100,00 por pessoa", offer);
        Assert.Contains("R$ 0,40 por crédito", Reply("Posso dividir um painel com um amigo para pagar menos?"));
    }

    // ---------------------------------------------------------------- comportamento da IA
    [Fact]
    public async Task T17_IA_nao_promete_lucro_liquido_garantido()
    {
        var r = Reply("Quanto posso lucrar revendendo?");
        Assert.Contains("Não existe garantia de lucro", r); Assert.Contains("margem bruta", r);
        Assert.Empty(ReplySafetyGuard.Check(r, Cfg));
        Assert.Contains(ReplySafetyGuard.Check("Com certeza! Lucro garantido de R$ 2.000 por mês.", Cfg), v => v.Contains("lucro"));
        // resposta do modelo com promessa é barrada antes do envio
        var (f, c, id) = await AiConversation("Quanto posso lucrar revendendo?");
        using (f)
        {
            f.Ai.Script.Enqueue(_ => FakeAi.Text("Pode ficar tranquilo: lucro garantido de R$ 1.920 por mês!"));
            await Respond(f, id);
            var sent = await QueuedReply(f);
            Assert.DoesNotContain("garantido", sent); Assert.Contains("Não existe garantia de lucro", sent);
        }
    }

    [Fact]
    public void T18_IA_nao_oferece_teste_do_painel()
    {
        var steps = Lab("Posso testar o painel antes de contratar?", "Libera um painel de teste pra mim?");
        Assert.All(steps, s => Assert.Contains("Não disponibilizamos teste do painel", s.Reply));
        Assert.All(steps, s => Assert.Empty(ReplySafetyGuard.Check(s.Reply, Cfg)));
        Assert.NotEmpty(ReplySafetyGuard.Check("Claro! Vou liberar um painel de teste para você agora.", Cfg));
    }

    [Fact] public void T19_IA_informa_teste_do_servico_por_4_horas() => Assert.Contains("teste do serviço por 4 horas", Reply("Vocês podem me liberar um teste?"));
    [Fact] public void T20_IA_informa_o_link_de_teste() => Assert.Contains("https://dominiusplay.top/teste", Reply("Quero fazer um teste"));

    [Fact]
    public void T21_IA_explica_confirmacao_de_email_e_WhatsApp()
    {
        var r = Reply("Vocês podem me liberar um teste?");
        Assert.Contains("confirme seu e-mail e WhatsApp", r); Assert.Contains("enviados ao seu e-mail", r);
    }

    [Fact]
    public void T22_IA_informa_corretamente_os_codigos_Downloader()
    {
        var r = Reply("Qual aplicativo posso testar?");
        Assert.Contains("código Downloader 6124702", r); Assert.Contains("https://aftv.news/6124702", r);
        Assert.Contains("código Downloader 9604273", r); Assert.Contains("https://aftv.news/9604273", r);
    }

    [Fact] public void T23_IA_informa_corretamente_o_player_web() => Assert.Contains("https://player.dominiusplay.top", Reply("Qual aplicativo posso testar?"));

    [Fact]
    public void T24_IA_nao_apresenta_antigas_parcerias_como_ativas()
    {
        var r = Reply("Vocês trabalham com PlaySim?");
        Assert.Contains("buscando novas parcerias", r); Assert.Contains("encerraram", r);
        Assert.Empty(ReplySafetyGuard.Check(r, Cfg));
        Assert.Contains(ReplySafetyGuard.Check("Sim! Trabalhamos com PlaySim e Magic Player.", Cfg), v => v.Contains("parceiro antigo"));
        // "assistente" não é confundido com o app Assist+
        Assert.Empty(ReplySafetyGuard.Check("Sou a assistente virtual da Dominius Play.", Cfg));
    }

    [Fact]
    public void T25_IA_informa_prazo_de_5_minutos_apos_Pix_com_ressalva()
    {
        var r = Reply("Quanto tempo demora para liberar meu painel depois do pagamento?");
        Assert.Contains("aproximadamente 5 minutos", r); Assert.Contains("Após a confirmação do Pix", r); Assert.Contains("salvo pendências técnicas ou de validação", r);
    }

    [Fact]
    public async Task T26_IA_informa_corretamente_a_chave_Pix_e_usa_a_configuracao_editada()
    {
        var r = Reply("Me passa a chave Pix");
        Assert.Contains("supertv.tech@gmail.com", r); Assert.Contains("confira os dados do recebedor", r);
        Assert.Contains(ReplySafetyGuard.Check("Pode pagar no Pix golpista@exemplo.com", Cfg), v => v.Contains("Chave Pix diferente"));

        // a chave é editável no painel e a IA passa a usar a nova sem reiniciar
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var cfg = await c.GetFromJsonAsync<CommercialConfig>("/api/commercial");
        Assert.Equal("supertv.tech@gmail.com", cfg!.PixKey);
        (await c.PutAsJsonAsync("/api/commercial", cfg with { PixKey = "financeiro@dominiusplay.top" })).EnsureSuccessStatusCode();
        var lab = await (await c.PostAsJsonAsync("/api/lab/run", new LabRequest("pix", null))).Content.ReadFromJsonAsync<LabResponse>();
        Assert.Contains("financeiro@dominiusplay.top", lab!.Steps[0].Reply);
        var hist = await c.GetFromJsonAsync<List<CommercialHistoryDto>>("/api/commercial/history");
        Assert.Contains(hist!, h => h.Details.Contains("supertv.tech@gmail.com → financeiro@dominiusplay.top"));
        // validação: link sem https é recusado
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/commercial", cfg with { TestUrl = "dominiusplay.top/teste" })).StatusCode);
    }

    [Fact]
    public async Task T27_IA_nao_inventa_confirmacao_financeira()
    {
        var step = Lab("Me passa a chave Pix", "Já paguei, vocês receberam?")[^1];
        Assert.True(step.Handoff);
        Assert.Contains("não consigo confirmar pagamentos", step.Reply);
        Assert.Empty(ReplySafetyGuard.Check(step.Reply, Cfg));
        Assert.Contains(ReplySafetyGuard.Check("Pagamento confirmado! Seu painel já está ativo.", Cfg), v => v.Contains("financeira"));

        // o modelo diz que recebeu: a resposta não sai e a conversa vai para um humano
        var (f, c, id) = await AiConversation("Fiz o pix agora, caiu aí?");
        using (f)
        {
            f.Ai.Script.Enqueue(_ => FakeAi.Text("Recebemos seu pagamento, obrigado! Já está tudo certo."));
            await Respond(f, id);
            var sent = await QueuedReply(f);
            Assert.DoesNotContain("Recebemos seu pagamento", sent);
            var lead = await f.WithDb(db => db.Leads.AsNoTracking().SingleAsync());
            Assert.Equal(ConversationMode.AtendimentoHumano, lead.Mode);
            Assert.Contains("bloqueada", lead.NextAction);
        }
    }

    [Fact]
    public void T28_IA_nao_inventa_DNS_STB()
    {
        var r = Reply("Qual aplicativo posso testar?");
        Assert.Contains("DNS STB depende de orientação técnica", r);
        Assert.Empty(ReplySafetyGuard.Check(r, Cfg));
        Assert.NotEmpty(ReplySafetyGuard.Check("Use a DNS 45.12.33.10 no Smart STB.", Cfg));
        Assert.NotEmpty(ReplySafetyGuard.Check("A DNS STB é tv.servidor-qualquer.com, é só colocar.", Cfg));
    }

    [Fact]
    public void T29_IA_nao_repete_perguntas_ja_respondidas()
    {
        // pergunta de diagnóstico feita no máximo uma vez
        var steps = Lab("Quanto custa o painel de 500 créditos?", "Vocês utilizam CDN?", "Existe monitoramento automático dos servidores?", "Vocês emitem nota fiscal?");
        Assert.Equal(1, steps.Count(s => s.Reply!.Contains("Você já trabalha com revenda ou está começando agora?")));
        // o cliente já disse o aparelho: a IA não pergunta de novo
        Assert.DoesNotContain("Qual aparelho", Reply("Vou usar numa TV Box", "Qual aplicativo posso testar?"));
        // o cliente já disse o nome: a IA não pede o nome de novo
        var contract = Reply("Meu nome é Carlos e quero contratar o plano de 100 créditos");
        Assert.DoesNotContain("primeiro nome", contract); Assert.Contains("e-mail", contract);
        // pergunta já respondida: não repete o texto inteiro
        var again = Lab("Posso criar sub-revendedores?", "Os créditos do sub-revendedor saem de onde?")[^1];
        Assert.StartsWith("Como te expliquei", again.Reply);
    }

    [Fact]
    public async Task T30_IA_encaminha_pedidos_de_atendimento_humano()
    {
        var s = Lab("Quanto custa a franquia?", "Quero falar com um atendente")[^1];
        Assert.True(s.Handoff); Assert.Equal(IntentCategory.PedidoHumano, s.Intent);
        // no fluxo real: transfere na hora, sem esperar a IA
        var (f, c, id) = await AiConversation("Oi");
        using (f)
        {
            await Inbound(f, "M2", "quero falar com um atendente");
            Assert.Equal(ConversationMode.AtendimentoHumano, (await f.WithDb(db => db.Leads.AsNoTracking().SingleAsync())).Mode);
        }
    }

    [Fact]
    public async Task T31_IA_respeita_pedidos_de_descadastramento()
    {
        var steps = Lab("Não quero mais receber mensagens, me tire da lista", "Oi?", "Quanto custa?");
        Assert.True(steps[0].OptOut); Assert.Contains("removi seu número", steps[0].Reply);
        Assert.All(steps.Skip(1), s => Assert.Null(s.Reply));
        var (f, c, id) = await AiConversation("Oi");
        using (f)
        {
            await Inbound(f, "M2", "me tire da lista, não quero receber mais mensagens");
            Assert.True(await f.WithDb(db => db.OptOuts.AnyAsync()));
            var lead = await f.WithDb(db => db.Leads.AsNoTracking().SingleAsync());
            Assert.Equal(ConsentStatus.Revogado, lead.ConsentStatus);
        }
    }

    [Fact]
    public void T32_IA_nao_inventa_autorizacoes_de_revenda_de_conteudo()
    {
        foreach (var n in new[] { 19, 68, 69 }) Assert.True(Entries.Single(e => e.Code == DominiusKnowledge.Code(n)).NeedsConfirmation);
        var s = Lab("Os conteúdos possuem autorização para distribuição comercial?")[0];
        Assert.Contains("Nossa equipe responsável deve confirmar", s.Reply);
        Assert.Contains("Resposta depende de confirmação da equipe", s.Alerts);
        Assert.Contains("não concede automaticamente direitos", Reply("Tenho autorização contratual para revender o catálogo?"));
        Assert.NotEmpty(ReplySafetyGuard.Check("Fica tranquilo, todo o conteúdo é 100% legal e conteúdo licenciado.", Cfg));
    }

    [Fact]
    public async Task T33_IA_e_atendentes_nao_alteram_dados_comerciais_sem_permissao()
    {
        // a IA não tem ferramenta que escreva preço, Pix, base ou configuração
        var tools = AiAgent.Tools().Select(t => t.Name).ToList();
        Assert.DoesNotContain(tools, t => t.Contains("comercial") || t.Contains("pix") || t.Contains("preco") || t.Contains("plano") && !t.StartsWith("consultar") || t.Contains("base") && !t.StartsWith("consultar"));

        using var f = new CrmFactory();
        var admin = await f.AdminAsync();
        (await admin.PostAsJsonAsync("/api/users", new UserCreate("maria", "Maria", "Senha@12345", UserRole.Atendente))).EnsureSuccessStatusCode();
        var att = f.CreateClient();
        var token = (await (await att.PostAsJsonAsync("/api/auth/login", new LoginRequest("maria", "Senha@12345"))).Content.ReadFromJsonAsync<LoginResponse>())!.Token;
        att.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var cfg = await att.GetFromJsonAsync<CommercialConfig>("/api/commercial"); // leitura liberada
        Assert.Equal(HttpStatusCode.Forbidden, (await att.PutAsJsonAsync("/api/commercial", cfg! with { PixKey = "outra@chave.com" })).StatusCode);
        var e = (await att.GetFromJsonAsync<List<KbEntryDto>>("/api/kb"))!.First();
        var upsert = new KbEntryUpsert(e.Code, e.Category, e.Question, e.Variations, "Resposta alterada", e.Keywords, e.Products, e.Priority, e.Status, e.Source, e.NeedsConfirmation);
        Assert.Equal(HttpStatusCode.Forbidden, (await att.PutAsJsonAsync($"/api/kb/{e.Id}", upsert)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await att.PostAsync($"/api/kb/{e.Id}/status?status=2", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await att.PutAsJsonAsync("/api/kb/access", new KbAccessDto(false, false))).StatusCode);
        Assert.Equal("supertv.tech@gmail.com", (await admin.GetFromJsonAsync<CommercialConfig>("/api/commercial"))!.PixKey);
    }

    [Fact]
    public async Task T34_IA_mantem_o_historico_e_a_memoria_entre_respostas()
    {
        var (f, c, id) = await AiConversation("Meu amigo também quer trabalhar com revenda");
        using (f)
        {
            f.Ai.Script.Enqueue(_ => FakeAi.Text("Que legal! Dá para dividir um painel Master de 500 créditos."));
            await Respond(f, id);
            await f.RunQueueAsync();
            await Inbound(f, "M2", "E quanto custa?");
            f.Ai.Script.Enqueue(_ => FakeAi.Text("O painel de 500 créditos custa R$ 200,00 por mês."));
            await Respond(f, id);
            var req = f.Ai.Requests[^1];
            // histórico: as mensagens anteriores vão para o modelo
            var texts = string.Join("\n", req.Turns.Select(t => t.ToString()));
            Assert.Contains("Meu amigo também quer trabalhar", texts);
            Assert.Contains("Dá para dividir um painel Master", texts);
            // memória comercial persistida: perfil e produto em discussão (perguntas genéricas se referem a ele)
            Assert.Contains("PERFIL COMERCIAL: Quer começar com um amigo", req.System);
            Assert.Contains("PRODUTO EM DISCUSSÃO: MENSALISTA_500", req.System);
            var facts = await f.WithDb(db => db.LeadFacts.AsNoTracking().Where(x => x.LeadId == id).ToListAsync());
            Assert.Contains(facts, x => x.Key == KnowledgeBaseService.FactProfile && x.Value == nameof(SalesProfile.Amigo));
            Assert.Single(facts, x => x.Key == KnowledgeBaseService.FactOffer && x.Value == nameof(CommercialTrigger.AmigoInteressado));
        }
    }

    [Fact]
    public void T35_agente_de_disparo_e_de_atendimento_nao_respondem_ao_mesmo_tempo()
    {
        var now = DateTimeOffset.UtcNow;
        SendContext Ctx(Lead l, OutboundKind k) => new(l, k, now, false, true, now.AddMinutes(-1), null, false, 0, 50, TimeSpan.FromMinutes(5), null, true, 0, TimeZoneInfo.Utc);
        var lead = new Lead { ConsentStatus = ConsentStatus.Valido, Mode = ConversationMode.Prospeccao };
        // prospecção: só o Disparo fala
        Assert.False(SendGuard.Evaluate(Ctx(lead, OutboundKind.Resposta)).Allowed);
        // o contato respondeu: a conversa passa para o Atendimento e o Disparo fica mudo
        ConversationStateMachine.Apply(lead, ConversationTrigger.ContatoRespondeu);
        Assert.Equal(ConversationMode.Ia, lead.Mode);
        Assert.False(SendGuard.Evaluate(Ctx(lead, OutboundKind.AbordagemInicial)).Allowed);
        Assert.False(SendGuard.Evaluate(Ctx(lead, OutboundKind.Acompanhamento)).Allowed);
        Assert.True(SendGuard.Evaluate(Ctx(lead, OutboundKind.Resposta)).Allowed);
        // humano assumiu: nenhum agente automático fala
        ConversationStateMachine.Apply(lead, ConversationTrigger.TransferirHumano);
        Assert.False(SendGuard.Evaluate(Ctx(lead, OutboundKind.Resposta)).Allowed);
        Assert.False(SendGuard.Evaluate(Ctx(lead, OutboundKind.Acompanhamento)).Allowed);
    }

    // ---------------------------------------------------------------- integração
    [Fact]
    public async Task Prompt_do_agente_recebe_a_base_sem_reescrever_o_prompt_e_edicao_vale_na_hora()
    {
        var (f, c, id) = await AiConversation("Achei caro, tenho pouco dinheiro para começar");
        using (f)
        {
            f.Ai.Script.Enqueue(_ => FakeAi.Text("Temos o plano inicial de R$ 30."));
            await Respond(f, id);
            var sys = f.Ai.Requests[^1].System;
            Assert.Contains("BASE DE CONHECIMENTO DOMINIUS PLAY", sys);
            Assert.Contains("Chave Pix: supertv.tech@gmail.com", sys);
            Assert.Contains("NÃO existe teste do painel", sys);
            Assert.Contains("GATILHO COMERCIAL NA ÚLTIMA MENSAGEM: Orçamento baixo", sys);
            Assert.Contains("Temos um plano inicial de apenas R$ 30,00 mensais, com 10 créditos", sys);
            Assert.Contains(AiAgent.Tools(), t => t.Name == "consultar_base_conhecimento");
            // o prompt principal do agente não foi alterado (a base é um bloco separado)
            var settings = await c.GetFromJsonAsync<AiSettingsDto>("/api/ai/settings");
            Assert.True(string.IsNullOrEmpty(settings!.SystemPrompt));

            // editar uma resposta no painel muda a próxima resposta, sem reiniciar
            var e = (await c.GetFromJsonAsync<List<KbEntryDto>>("/api/kb?q=Quanto custa para começar"))!.First(x => x.Code == "DP-001");
            var upd = new KbEntryUpsert(e.Code, e.Category, e.Question, e.Variations, "Comece com {preco:MENSALISTA_10} por mês (texto revisado).", e.Keywords, e.Products, e.Priority, e.Status, e.Source, e.NeedsConfirmation);
            var saved = await (await c.PutAsJsonAsync($"/api/kb/{e.Id}", upd)).Content.ReadFromJsonAsync<KbEntryDto>();
            Assert.Equal(2, saved!.Version); Assert.Equal("Comece com R$ 30,00 por mês (texto revisado).", saved.RenderedAnswer);
            await f.RunQueueAsync();
            await Inbound(f, "M2", "Quanto custa para começar a revender?");
            f.Ai.Script.Enqueue(_ => FakeAi.Text("ok"));
            await Respond(f, id);
            Assert.Contains("(texto revisado)", f.Ai.Requests[^1].System);
            var history = await c.GetFromJsonAsync<List<KbRevisionDto>>($"/api/kb/{e.Id}/history");
            Assert.Equal(["editada", "criada"], history!.Select(h => h.Action));
        }
    }

    [Fact]
    public async Task Ferramenta_consultar_base_devolve_a_resposta_oficial()
    {
        var (f, c, id) = await AiConversation("Vocês emitem NF?");
        using (f)
        {
            f.Ai.Script.Enqueue(_ => FakeAi.Tools(("consultar_base_conhecimento", """{"pergunta":"vocês emitem nota fiscal?"}""")));
            f.Ai.Script.Enqueue(r => FakeAi.Text("Sobre nota fiscal, a equipe confirma."));
            await Respond(f, id);
            var toolResult = f.Ai.Requests[^1].Turns.OfType<DominiusPlay.Infrastructure.Ai.AiToolResults>().Single().Results.Single().Content;
            Assert.Contains("DP-070", toolResult); Assert.Contains("precisa_confirmacao\":true", toolResult);
        }
    }

    [Fact]
    public async Task Rascunho_nao_vai_para_a_IA_mas_pode_ser_testado_antes_de_publicar()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var created = await (await c.PostAsJsonAsync("/api/kb", new KbEntryUpsert("DP-076", DominiusKnowledge.CatNovidades, "Vocês têm promoção de Black Friday?",
            "Tem Black Friday?\nTem promoção?", "Neste momento não há promoção ativa. Ofertas vigentes: consulte a equipe.", "black friday, promoção", "", 2, KbStatus.Rascunho, "Painel", true))).Content.ReadFromJsonAsync<KbEntryDto>();
        var test = await (await c.PostAsJsonAsync("/api/kb/test", new KbTestRequest("tem black friday?"))).Content.ReadFromJsonAsync<KbTestResponse>();
        Assert.Equal("DP-076", test!.Matches[0].Code);
        using (var scope = f.Services.CreateScope())
        {
            var kb = await scope.ServiceProvider.GetRequiredService<KnowledgeBaseService>().LoadAsync();
            Assert.DoesNotContain(kb.Entries, e => e.Code == "DP-076");
        }
        (await c.PostAsync($"/api/kb/{created!.Id}/status?status={(int)KbStatus.Publicado}", null)).EnsureSuccessStatusCode();
        using (var scope = f.Services.CreateScope())
            Assert.Contains((await scope.ServiceProvider.GetRequiredService<KnowledgeBaseService>().LoadAsync()).Entries, e => e.Code == "DP-076");
        // marcador desconhecido é recusado
        var bad = await c.PostAsJsonAsync("/api/kb", new KbEntryUpsert("DP-077", "X", "Pergunta?", "", "Valor {preco_errado}", "", "", 3, KbStatus.Rascunho, "", false));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Exportar_e_importar_a_base()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var export = await c.GetFromJsonAsync<List<KbEntryUpsert>>("/api/kb/export");
        Assert.Equal(75, export!.Count);
        var changed = export.Select(e => e.Code == "DP-063" ? e with { Answer = "A {empresa} informa cerca de {anos_mercado} anos (importado)." } : e).ToList();
        changed.Add(new KbEntryUpsert("DP-100", "G — Novidades e ofertas", "Tem app para iPhone?", "", "A equipe confirma a compatibilidade com iPhone.", "iphone", "", 3, KbStatus.Publicado, "Importação", true));
        var noOverwrite = await (await c.PostAsJsonAsync("/api/kb/import", new KbImportRequest(changed, false))).Content.ReadFromJsonAsync<KbImportResult>();
        Assert.Equal((1, 0, 75), (noOverwrite!.Created, noOverwrite.Updated, noOverwrite.Skipped));
        var overwrite = await (await c.PostAsJsonAsync("/api/kb/import", new KbImportRequest(changed, true))).Content.ReadFromJsonAsync<KbImportResult>();
        Assert.Equal(76, overwrite!.Updated);
        var all = await c.GetFromJsonAsync<List<KbEntryDto>>("/api/kb");
        Assert.Equal(76, all!.Count);
        Assert.Equal("A Dominius Play informa cerca de 10 anos (importado).", all.Single(e => e.Code == "DP-063").RenderedAnswer);
    }

    [Fact]
    public async Task Laboratorio_simula_os_11_perfis_sem_enviar_nada()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var scenarios = await c.GetFromJsonAsync<List<LabScenarioDto>>("/api/lab/scenarios");
        Assert.Equal(11, scenarios!.Count);
        foreach (var sc in scenarios)
        {
            var r = await (await c.PostAsJsonAsync("/api/lab/run", new LabRequest(sc.Key, null))).Content.ReadFromJsonAsync<LabResponse>();
            Assert.Equal(sc.Messages.Count, r!.Steps.Count);
            Assert.All(r.Steps, s => { Assert.False(string.IsNullOrWhiteSpace(s.Intent)); Assert.False(string.IsNullOrWhiteSpace(s.SuggestedAction)); });
            Assert.All(r.Steps.Where(s => s.Reply is not null), s => Assert.DoesNotContain(s.Alerts, a => a.StartsWith("Confirmação financeira") || a.StartsWith("Ofereceu teste") || a.StartsWith("Promessa")));
        }
        Assert.Empty(f.Evolution.Sent);
        Assert.Equal(0, await f.WithDb(db => db.Leads.CountAsync()));
        Assert.Equal(0, await f.WithDb(db => db.Messages.CountAsync()));
    }

    [Fact]
    public async Task Laboratorio_com_Claude_mostra_a_resposta_e_bloqueia_o_que_for_proibido()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await EnableAi(f, c);
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Nossa chave Pix é supertv.tech@gmail.com. Confira o recebedor antes de pagar."));
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Pix confirmado! Seu painel já está ativo."));
        var r = await (await c.PostAsJsonAsync("/api/lab/run", new LabRequest("pix", null, UseAi: true))).Content.ReadFromJsonAsync<LabResponse>();
        Assert.True(r!.AiUsed);
        Assert.Contains("supertv.tech@gmail.com", r.Steps[0].AiReply);
        Assert.NotEmpty(r.Steps[1].AiAlerts);
        Assert.DoesNotContain("Pix confirmado", r.Steps[1].AiReply);
        Assert.Empty(f.Evolution.Sent);
    }

    [Fact]
    public void Gatilhos_comerciais_e_perfis()
    {
        Assert.Equal(CommercialTrigger.OrcamentoBaixo, CommercialTriggers.Detect("Tenho pouco dinheiro para começar."));
        Assert.Equal(CommercialTrigger.AmigoInteressado, CommercialTriggers.Detect("Meu amigo também quer trabalhar."));
        Assert.Equal(CommercialTrigger.JaPossuiPainel, CommercialTriggers.Detect("Já trabalho com painel próprio."));
        Assert.Equal(CommercialTrigger.MarcaPropria, CommercialTriggers.Detect("Quero trabalhar com meu próprio aplicativo."));
        Assert.Equal(CommercialTrigger.QuerTestar, CommercialTriggers.Detect("Quero testar antes de comprar"));
        Assert.Equal(CommercialTrigger.QuerContratar, CommercialTriggers.Detect("Quero contratar"));
        Assert.Equal(CommercialTrigger.AchouCaro, CommercialTriggers.Detect("achei caro"));
        Assert.Contains("R$ 250,00 mensais para até 600 logins ativos", CommercialTriggers.OfferMessage(CommercialTrigger.JaPossuiPainel, Cfg, Plans));
        Assert.Contains("franquia completa por R$ 650,00", CommercialTriggers.OfferMessage(CommercialTrigger.MarcaPropria, Cfg, Plans));
        // a divisão com amigo não é oferecida de novo nem quando desligada
        Assert.Null(CommercialTriggers.OfferMessage(CommercialTrigger.AmigoInteressado, Cfg, Plans, friendOfferAlreadyMade: true));
        Assert.Null(CommercialTriggers.OfferMessage(CommercialTrigger.AmigoInteressado, Cfg with { FriendSplitEnabled = false }, Plans));

        Assert.Equal(SalesProfile.Iniciante, SalesProfiles.Detect("nunca trabalhei com isso, quero começar"));
        Assert.Equal(SalesProfile.RevendedorExperiente, SalesProfiles.Detect("já trabalho com sigma e tenho clientes"));
        Assert.Equal(SalesProfile.Franquia, SalesProfiles.Detect("quero minha marca própria"));
        Assert.Equal(SalesProfile.Amigo, SalesProfiles.Detect("vou começar com um amigo"));
        Assert.Equal(SalesProfile.Provedor, SalesProfiles.Detect("sou provedor de internet com fibra"));
        // "qual o mais barato?" depende do produto em discussão
        Assert.Contains("R$ 250,00", Reply("Já trabalho com painel próprio", "Qual o mais barato?"));
        Assert.Contains("R$ 30,00", Reply("Nunca trabalhei com isso", "Qual o mais barato?"));
    }

    [Fact]
    public async Task Banco_antigo_recebe_a_tabela_oficial_sem_perder_edicoes()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        // simula banco da v0.4.0: tabela antiga (Mensalista 50, Mensalista 100 a R$ 90) e sem a marca da atualização
        await f.WithDb(async db =>
        {
            db.Settings.Remove(await db.Settings.SingleAsync(s => s.Key == SettingKeys.CommercialTable202611));
            db.Plans.RemoveRange(db.Plans.Where(p => p.Code == PlanCodes.Mensalista10));
            (await db.Plans.SingleAsync(p => p.Code == PlanCodes.Mensalista100)).MonthlyPrice = 90m;
            // o Mensalista 50 (R$ 50, 50 créditos) da v0.4.0 já está no banco de teste e continua igual
            var e = await db.KbEntries.SingleAsync(x => x.Code == "DP-010");
            e.Answer = "Resposta editada pelo administrador."; // edição do admin
            return await db.SaveChangesAsync();
        });
        var changes = await f.WithDb(db => SeedData.EnsureCommercialAsync(db));
        Assert.Contains(changes, x => x.Contains("Mensalista 10"));
        Assert.Contains(changes, x => x.Contains("Mensalista 100 v2"));
        Assert.DoesNotContain(changes, x => x.Contains("Mensalista 50"));
        var plans = await f.WithDb(db => db.Plans.AsNoTracking().ToListAsync());
        Assert.Contains(plans, p => p.Code == PlanCodes.Mensalista50 && p.Approval == ApprovalStatus.Aprovado && p.MonthlyPrice == 50m); // continua à venda
        Assert.Equal(80m, plans.Single(p => p.Code == PlanCodes.Mensalista100 && p.Approval == ApprovalStatus.Aprovado).MonthlyPrice);
        Assert.Contains(plans, p => p.Code == PlanCodes.Mensalista100 && p.Approval == ApprovalStatus.Arquivado && p.MonthlyPrice == 90m); // histórico preservado
        // rodar de novo não muda nada nem duplica a base
        Assert.Empty(await f.WithDb(db => SeedData.EnsureCommercialAsync(db)));
        Assert.Equal(75, await f.WithDb(db => db.KbEntries.CountAsync()));
        Assert.Equal("Resposta editada pelo administrador.", await f.WithDb(db => db.KbEntries.Where(x => x.Code == "DP-010").Select(x => x.Answer).SingleAsync()));
    }

    [Fact]
    public async Task Confirmacoes_de_Carlos_atualizam_quem_instalou_a_primeira_041()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        // simula a primeira 0.4.1: Mensalista 50 arquivado, pós-pago desligado com faixas antigas, só o atendimento consulta a base, respostas antigas
        await f.WithDb(async db =>
        {
            db.Settings.Remove(await db.Settings.SingleAsync(s => s.Key == SettingKeys.CommercialAnswers20261121));
            foreach (var p in db.Plans.Where(p => p.Code == PlanCodes.Mensalista50)) p.Approval = ApprovalStatus.Arquivado;
            var cfg = await db.Settings.SingleAsync(s => s.Key == SettingKeys.CommercialConfig);
            cfg.Value = (new CommercialConfig() with { PostPaidEnabled = false, PostPaidTiers = CommercialConfig.LegacyPostPaidTiers, PixKey = "editada@exemplo.com" }).ToJson();
            (await db.Settings.SingleAsync(s => s.Key == SettingKeys.KbAccess)).Value = "Atendimento";
            (await db.KbEntries.SingleAsync(x => x.Code == "DP-053")).Answer = "Sim, desde que seu painel seja Master e possua as permissões correspondentes.";
            var edited = await db.KbEntries.SingleAsync(x => x.Code == "DP-044");
            edited.Answer = "Texto do administrador."; edited.UpdatedBy = "admin"; edited.Version = 2;
            return await db.SaveChangesAsync();
        });
        var changes = await f.WithDb(db => SeedData.EnsureCommercialAsync(db));
        Assert.Contains(changes, x => x.StartsWith("Mensalista 50 ativo"));
        Assert.Contains(changes, x => x.StartsWith("pós-pago ligado"));
        Assert.Contains("agente de disparo consulta a base", changes);
        Assert.Contains("resposta DP-053", changes);
        Assert.DoesNotContain("resposta DP-044", changes); // editada pelo administrador: não mexe

        var plans = await f.WithDb(db => db.Plans.AsNoTracking().Where(p => p.Code == PlanCodes.Mensalista50).ToListAsync());
        Assert.Single(plans, p => p.Approval == ApprovalStatus.Aprovado);
        Assert.Contains(plans, p => p.Approval == ApprovalStatus.Arquivado); // histórico preservado
        var cfgNow = CommercialConfig.FromJson(await f.WithDb(db => db.Settings.Where(s => s.Key == SettingKeys.CommercialConfig).Select(s => s.Value).SingleAsync()));
        Assert.True(cfgNow.PostPaidEnabled);
        Assert.Equal("R$ 5,00 por login ativo", cfgNow.PostPaidTiers);
        Assert.Equal(60m, cfgNow.PostPaidAdhesionFee);
        Assert.Equal("editada@exemplo.com", cfgNow.PixKey); // edição do administrador preservada
        Assert.Contains("R$ 100,00", cfgNow.SubpanelRules);
        Assert.Equal("Texto do administrador.", await f.WithDb(db => db.KbEntries.Where(x => x.Code == "DP-044").Select(x => x.Answer).SingleAsync()));
        Assert.Contains("{planos_master}", await f.WithDb(db => db.KbEntries.Where(x => x.Code == "DP-053").Select(x => x.Answer).SingleAsync()));

        var access = await c.GetFromJsonAsync<KbAccessDto>("/api/kb/access");
        Assert.True(access!.Attendance); Assert.True(access.Dispatch);
        Assert.Empty(await f.WithDb(db => SeedData.EnsureCommercialAsync(db))); // roda uma vez só
    }

    [Fact]
    public void Pos_pago_e_Mensalista_50_aparecem_nas_respostas()
    {
        Assert.Contains("50 créditos por R$ 50,00", Reply("Quais pacotes de créditos vocês têm?"));
        var post = Reply("É pós-pago?");
        Assert.Contains("R$ 60,00", post); Assert.Contains("R$ 5,00 por login ativo", post);
        Assert.False(Entries.Single(e => e.Code == "DP-044").NeedsConfirmation);
    }

    [Fact]
    public async Task Apontamento_ate_600_ativos_e_Franquia_sem_limite()
    {
        Assert.Equal(600, P(PlanCodes.Apontamento600).MaxActiveLogins);
        Assert.Null(P(PlanCodes.Franquia).MaxActiveLogins);
        Assert.Contains("sem limite de ativos", Reply("Quanto custa a franquia?"));
        var diff = Reply("Qual a diferença entre mensalista, apontamento e franquia?");
        Assert.Contains("R$ 250,00 comporta até 600 ativos", diff); Assert.Contains("sem limite de ativos", diff);

        // quem já tinha a base antiga recebe o texto novo
        using var f = new CrmFactory();
        await f.WithDb(async db =>
        {
            db.Settings.Remove(await db.Settings.SingleAsync(s => s.Key == SettingKeys.ActiveLimits20261121));
            (await db.Plans.SingleAsync(p => p.Code == PlanCodes.Franquia)).Benefits = SeedData.LegacyFranchiseBenefits;
            (await db.KbEntries.SingleAsync(x => x.Code == "DP-016")).Answer = "Nossa franquia completa custa {preco:FRANQUIA_COMPLETA} mensais.";
            return await db.SaveChangesAsync();
        });
        var changes = await f.WithDb(db => SeedData.EnsureCommercialAsync(db));
        Assert.Contains("Franquia: sem limite de ativos", changes); Assert.Contains("resposta DP-016", changes);
        Assert.Contains("Sem limite de ativos", await f.WithDb(db => db.Plans.Where(p => p.Code == PlanCodes.Franquia).Select(p => p.Benefits).SingleAsync()));
        Assert.Empty(await f.WithDb(db => SeedData.EnsureCommercialAsync(db)));
    }

    [Fact]
    public void Todas_as_respostas_oficiais_passam_na_conferencia()
    {
        foreach (var e in Entries)
        {
            var r = KnowledgeRenderer.Render(e.Answer, Cfg, Plans);
            Assert.Empty(r.Warnings);
            Assert.DoesNotContain("{", r.Text);
            Assert.Empty(ReplySafetyGuard.Check(r.Text, Cfg));
        }
        foreach (var t in Enum.GetValues<CommercialTrigger>())
            if (CommercialTriggers.OfferMessage(t, Cfg, Plans) is { } offer) Assert.Empty(ReplySafetyGuard.Check(offer, Cfg));
        // preço que sai da tabela: plano não aprovado vira "a confirmar", nunca um valor inventado
        var r2 = KnowledgeRenderer.Render("{preco:MENSALISTA_10}", Cfg, Plans.Where(p => p.Code != PlanCodes.Mensalista10).ToList());
        Assert.Equal(KnowledgeRenderer.Missing, r2.Text); Assert.NotEmpty(r2.Warnings);
    }

    // ---------------------------------------------------------------- utilitários
    private static async Task EnableAi(CrmFactory f, HttpClient c)
    {
        await f.ConfigureEvolutionAsync(c);
        (await c.PutAsJsonAsync("/api/ai/settings", new AiSettingsUpdate(true, "claude-haiku-5-5", "claude-sonnet-5-5", "sk-ant-teste-123", 4000, "Lia", "", 100000, false, 0))).EnsureSuccessStatusCode();
    }

    private static async Task<Guid> Inbound(CrmFactory f, string id, string text)
    {
        var body = $$$"""{"event":"messages.upsert","instance":"dominius","data":{"key":{"remoteJid":"5514988159045@s.whatsapp.net","fromMe":false,"id":"{{{id}}}"},"pushName":"João","message":{"conversation":"{{{text}}}"},"messageTimestamp":{{{f.Clock.GetUtcNow().ToUnixTimeSeconds()}}}}}""";
        var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/evolution") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("x-dominius-token", await f.WebhookTokenAsync());
        (await f.CreateClient().SendAsync(req)).EnsureSuccessStatusCode();
        return await f.WithDb(db => db.Leads.Select(l => l.Id).SingleAsync());
    }

    private static async Task<(CrmFactory, HttpClient, Guid)> AiConversation(string first)
    {
        var f = new CrmFactory();
        var c = await f.AdminAsync();
        await EnableAi(f, c);
        var id = await Inbound(f, "M1", first);
        return (f, c, id);
    }

    private static async Task Respond(CrmFactory f, Guid id)
    {
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AiResponder>().RespondAsync(id);
    }

    private static Task<string> QueuedReply(CrmFactory f) =>
        f.WithDb(db => db.OutboundQueue.AsNoTracking().Where(q => q.Kind == OutboundKind.Resposta || q.Kind == OutboundKind.Transicao).OrderBy(q => q.CreatedAt).Select(q => q.Text).ToListAsync())
            .ContinueWith(t => t.Result.LastOrDefault() ?? "");
}
