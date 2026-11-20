using System.Net;
using System.Net.Http.Json;
using System.Text;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Tests;

/// <summary>Construtor de automações: validação (ciclos, conflitos), modo de teste e execução real na mensagem recebida.</summary>
public class AutomationTests
{
    private static readonly AutomationInput Input = new("quanto custa o plano?", IntentCategory.SolicitacaoPreco, true, false, KanbanStage.Respondeu, "", 0,
        ConversationMode.Ia, "Revendedor", null, true);

    private static List<AutomationBlock> PriceFlow() =>
    [
        new("1", AutomationBlockType.Inicio, Next: "2"),
        new("2", AutomationBlockType.VerificarConsentimento, Next: "3", NextNo: "9"),
        new("3", AutomationBlockType.AnalisarIntencao, "SolicitacaoPreco", Next: "4", NextNo: "6"),
        new("4", AutomationBlockType.AdicionarTag, "quer-preco", Next: "5"),
        new("5", AutomationBlockType.AtualizarCrm, "EmAtendimento", Next: "6"),
        new("6", AutomationBlockType.ExecutarIa, Next: "9"),
        new("9", AutomationBlockType.Encerrar),
    ];

    [Fact]
    public void Fluxo_valido_roda_sem_efeitos_colaterais()
    {
        Assert.Empty(AutomationEngine.Validate(PriceFlow()));
        var run = AutomationEngine.Run(PriceFlow(), Input);
        Assert.Null(run.Error);
        Assert.Equal([AutomationActionType.AdicionarTag, AutomationActionType.AtualizarEtapa, AutomationActionType.DeixarIaResponder], run.Actions.Select(a => a.Type));
        Assert.False(run.SuppressAi);
        // sem consentimento: vai direto para o fim
        Assert.Empty(AutomationEngine.Run(PriceFlow(), Input with { ConsentValid = false }).Actions);
    }

    [Fact]
    public void Bloqueia_ciclo_infinito_mas_aceita_ciclo_com_espera()
    {
        List<AutomationBlock> loop =
        [
            new("1", AutomationBlockType.Inicio, Next: "2"),
            new("2", AutomationBlockType.AdicionarTag, "x", Next: "3"),
            new("3", AutomationBlockType.VerificarCondicao, "pontuacao >= 60", Next: "4", NextNo: "2"),
            new("4", AutomationBlockType.Encerrar),
        ];
        Assert.Contains(AutomationEngine.Validate(loop), p => p.Contains("circular"));
        var withWait = loop.Select(b => b.Id == "2" ? b with { Next = "w" } : b).Append(new("w", AutomationBlockType.AguardarResposta, Next: "3")).ToList();
        Assert.DoesNotContain(AutomationEngine.Validate(withWait), p => p.Contains("circular"));
    }

    [Theory]
    [InlineData("transfer-then-ai")]
    [InlineData("ai-and-send")]
    [InlineData("closed-deal")]
    [InlineData("bad-condition")]
    [InlineData("orphan")]
    [InlineData("missing-no")]
    public void Recusa_fluxos_conflitantes_ou_incompletos(string kind)
    {
        List<AutomationBlock> f = kind switch
        {
            "transfer-then-ai" => [new("1", AutomationBlockType.Inicio, Next: "2"), new("2", AutomationBlockType.TransferirAtendente, "x", Next: "3"), new("3", AutomationBlockType.ExecutarIa)],
            "ai-and-send" => [new("1", AutomationBlockType.Inicio, Next: "2"), new("2", AutomationBlockType.ExecutarIa, Next: "3"), new("3", AutomationBlockType.EnviarMensagem, "Boas-vindas")],
            "closed-deal" => [new("1", AutomationBlockType.Inicio, Next: "2"), new("2", AutomationBlockType.AtualizarCrm, "NegocioFechado")],
            "bad-condition" => [new("1", AutomationBlockType.Inicio, Next: "2"), new("2", AutomationBlockType.VerificarCondicao, "idade > 3", Next: "1", NextNo: "1")],
            "orphan" => [new("1", AutomationBlockType.Inicio), new("2", AutomationBlockType.Encerrar)],
            _ => [new("1", AutomationBlockType.Inicio, Next: "2"), new("2", AutomationBlockType.AnalisarIntencao, "PedidoHumano", Next: "3"), new("3", AutomationBlockType.Encerrar)],
        };
        Assert.NotEmpty(AutomationEngine.Validate(f));
    }

    [Fact]
    public async Task Api_valida_testa_e_executa_na_mensagem_recebida()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);

        // modo de teste: nada gravado
        var t = (await (await c.PostAsJsonAsync("/api/automations/test", new AutomationTestRequest(PriceFlow(), "qual o valor do plano?"))).Content.ReadFromJsonAsync<AutomationTestResponse>())!;
        Assert.True(t.Valid, string.Join("; ", t.Problems));
        Assert.Contains("Adicionar tag: quer-preco", t.Actions);
        Assert.True(t.AiWouldReply);
        Assert.Equal(0, await f.WithDb(db => db.Leads.CountAsync()));

        // fluxo ativo com modelo não aprovado é recusado
        List<AutomationBlock> send = [new("1", AutomationBlockType.Inicio, Next: "2"), new("2", AutomationBlockType.EnviarMensagem, "Inexistente")];
        var bad = await c.PostAsJsonAsync("/api/automations", new AutomationFlowUpsert("Envio", null, AutomationTrigger.MensagemRecebida, true, send));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        // inativo (rascunho) pode ser salvo
        (await c.PostAsJsonAsync("/api/automations", new AutomationFlowUpsert("Envio", null, AutomationTrigger.MensagemRecebida, false, send))).EnsureSuccessStatusCode();

        var flow = (await (await c.PostAsJsonAsync("/api/automations", new AutomationFlowUpsert("Preço", "marca quem pede preço", AutomationTrigger.MensagemRecebida, true, PriceFlow())))
            .Content.ReadFromJsonAsync<AutomationFlowDto>())!;
        Assert.True(flow.Active);

        // contato com consentimento manda mensagem pedindo preço
        var lead = (await (await c.PostAsJsonAsync("/api/leads", new LeadUpsert("Ana", "11987650099", null, null, "Formulário site", null, "", IptvExperience.NaoInformado,
            null, null, PainCategory.Nenhuma, null, null, Priority.Normal, null))).Content.ReadFromJsonAsync<LeadCard>())!;
        (await c.PostAsJsonAsync($"/api/leads/{lead.Id}/consent", new ConsentRequest("Formulário do site", "print", null))).EnsureSuccessStatusCode();
        var body = $$$"""{"event":"messages.upsert","instance":"dominius","data":{"key":{"remoteJid":"5511987650099@s.whatsapp.net","fromMe":false,"id":"AU1"},"pushName":"Ana","message":{"conversation":"quanto custa o plano?"},"messageTimestamp":{{{f.Clock.GetUtcNow().ToUnixTimeSeconds()}}}}}""";
        var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/evolution") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("x-dominius-token", await f.WebhookTokenAsync());
        (await f.CreateClient().SendAsync(req)).EnsureSuccessStatusCode();

        var l = await f.WithDb(db => db.Leads.SingleAsync());
        Assert.Contains("quer-preco", l.Tags);
        Assert.Equal(KanbanStage.EmAtendimento, l.Stage);
        Assert.Equal(1, await f.WithDb(db => db.AuditLogs.CountAsync(a => a.Action == "automation.run")));
        Assert.Equal(1, await f.WithDb(db => db.StageChanges.CountAsync(s => s.ChangedBy == "Automação: Preço")));
        Assert.Equal(1, (await c.GetFromJsonAsync<List<AutomationFlowDto>>("/api/automations"))!.Single(x => x.Name == "Preço").Runs);
    }
}
