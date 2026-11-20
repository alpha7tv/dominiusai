using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Infrastructure.Ai;
using DominiusPlay.Infrastructure.Security;
using DominiusPlay.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DominiusPlay.Tests;

/// <summary>Provedor de IA roteirizado: cada chamada devolve o próximo passo da fila.</summary>
public class FakeAi : IAiProvider
{
    public readonly Queue<Func<JsonArray, AiCompletion>> Script = new();
    public readonly List<JsonArray> Requests = [];
    public readonly List<string> Models = [];

    public static AiCompletion Text(string t) => new(true, t, [], new JsonObject { ["role"] = "assistant", ["content"] = t }, 100, 20, null, 200);
    public static AiCompletion Tools(params (string Name, string Args)[] calls)
    {
        var tc = new JsonArray(calls.Select((c, i) => (JsonNode)new JsonObject
        { ["id"] = "call_" + i, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args } }).ToArray());
        return new(true, null, calls.Select((c, i) => new AiToolCall("call_" + i, c.Name, c.Args)).ToList(),
            new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = tc }, 120, 30, null, 200);
    }

    public Task<AiCompletion> CompleteAsync(string apiKey, string model, JsonArray messages, JsonArray? tools, double? temperature, int maxTokens, CancellationToken ct = default)
    {
        Requests.Add((JsonArray)messages.DeepClone()); Models.Add(model);
        return Task.FromResult(Script.Count > 0 ? Script.Dequeue()(messages) : Text("ok"));
    }

    public Task<(bool Ok, IReadOnlyList<string> Models, string? Error)> ListModelsAsync(string apiKey, CancellationToken ct = default) =>
        Task.FromResult<(bool, IReadOnlyList<string>, string?)>((true, ["gpt-4o", "gpt-5"], null));
}

public class AiTests
{
    private static async Task EnableAi(CrmFactory f, HttpClient c, string model = "gpt-4o")
    {
        await f.ConfigureEvolutionAsync(c);
        (await c.PutAsJsonAsync("/api/ai/settings", new AiSettingsUpdate(true, model, "sk-teste-123", 0.4, 600, "Lia", "", 100000, false, 0))).EnsureSuccessStatusCode();
    }

    private static async Task<Guid> Inbound(CrmFactory f, string id, string text)
    {
        var body = $$$"""{"event":"messages.upsert","instance":"dominius","data":{"key":{"remoteJid":"5514988159045@s.whatsapp.net","fromMe":false,"id":"{{{id}}}"},"pushName":"João","message":{"conversation":"{{{text}}}"},"messageTimestamp":{{{f.Clock.GetUtcNow().ToUnixTimeSeconds()}}}}}""";
        var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/evolution") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("x-dominius-token", await f.WebhookTokenAsync());
        (await f.CreateClient().SendAsync(req)).EnsureSuccessStatusCode();
        return await f.WithDb(db => db.Leads.Select(l => l.Id).SingleAsync());
    }

    private static async Task<AiReplyOutcome> Respond(CrmFactory f, Guid id)
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AiResponder>().RespondAsync(id);
    }

    [Fact]
    public async Task Chave_da_OpenAI_criptografada_e_nunca_retornada()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await EnableAi(f, c);
        var raw = await c.GetStringAsync("/api/ai/settings");
        Assert.DoesNotContain("sk-teste-123", raw);
        Assert.Contains("\"hasApiKey\":true", raw);
        var stored = await f.WithDb(db => db.Settings.SingleAsync(s => s.Key == SettingKeys.AiApiKey));
        Assert.True(stored.IsSecret); Assert.DoesNotContain("sk-teste", stored.Value);
    }

    [Fact]
    public async Task IA_qualifica_registra_memoria_e_responde_pela_fila()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await EnableAi(f, c);
        var id = await Inbound(f, "M1", "Já trabalho, tenho Painel Sigma com 350 clientes e meu servidor cai muito");
        f.Ai.Script.Enqueue(_ => FakeAi.Tools(
            ("atualizar_qualificacao", """{"experiencia":"JaTrabalha","painel":"Sigma","clientes_ativos":350,"dor_principal":"QuedasServidor","plano_interesse":"APONTAMENTO_SIGMA_600"}"""),
            ("registrar_fato", """{"chave":"horario_quedas","valor":"principalmente nos jogos","origem":"cliente"}"""),
            ("sugerir_etapa", """{"etapa":"EmNegociacao","motivo":"perfil Sigma qualificado"}"""),
            ("sugerir_etapa", """{"etapa":"NegocioFechado","motivo":"tentativa indevida"}""")));
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Entendi! Podemos avaliar o Apontamento Sigma 600. Quer conhecer os requisitos?"));

        Assert.Equal(AiReplyOutcome.Replied, await Respond(f, id));
        var lead = await f.WithDb(db => db.Leads.Include(l => l.Facts).AsNoTracking().SingleAsync());
        Assert.Equal((IptvExperience.JaTrabalha, "Sigma", 350, PainCategory.QuedasServidor), (lead.Experience, lead.PanelUsed, lead.ActiveClients, lead.MainPain));
        Assert.Equal(KanbanStage.EmNegociacao, lead.Stage); // nunca NegocioFechado pela IA
        Assert.Contains(lead.Facts, x => x.Key == "horario_quedas" && x.Source == FactSource.DeclaradoPeloCliente);
        Assert.Contains(lead.Facts, x => x.Key == "sugestao_etapa" && x.Source == FactSource.InferidoPelaIa);

        // o prompt recebeu os planos aprovados e a identificação como assistente virtual
        var system = f.Ai.Requests[0][0]!["content"]!.GetValue<string>();
        Assert.Contains("ASSISTENTE VIRTUAL", system);
        Assert.Contains("R$ 250,00", system);
        Assert.Equal("gpt-4o", f.Ai.Models[0]);

        Assert.Equal(1, await f.RunQueueAsync());
        Assert.Equal("Entendi! Podemos avaliar o Apontamento Sigma 600. Quer conhecer os requisitos?", f.Evolution.Sent.Single().Text);
        var msgs = await c.GetFromJsonAsync<List<MessageDto>>($"/api/conversations/{id}/messages");
        Assert.Contains(msgs!, m => m.Author == MessageAuthor.Ia);
        Assert.Equal(1, await f.WithDb(db => db.StageChanges.CountAsync(x => x.LeadId == id)));
    }

    [Fact]
    public async Task Humano_assume_durante_geracao_resposta_descartada()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await EnableAi(f, c);
        var id = await Inbound(f, "M1", "Oi");
        f.Ai.Script.Enqueue(_ =>
        {
            // simula atendente clicando "Assumir" enquanto o modelo gera a resposta
            c.PostAsync($"/api/conversations/{id}/takeover", null).GetAwaiter().GetResult();
            return FakeAi.Text("Olá!");
        });
        Assert.Equal(AiReplyOutcome.Superseded, await Respond(f, id));
        Assert.Equal(0, await f.WithDb(db => db.OutboundQueue.CountAsync()));
    }

    [Fact]
    public async Task Transferencia_avisa_cliente_e_pausa_IA()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await EnableAi(f, c);
        var id = await Inbound(f, "M1", "Quero falar com um atendente");
        f.Ai.Script.Enqueue(_ => FakeAi.Tools(("transferir_para_humano", """{"motivo":"cliente pediu"}""")));
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Claro! Um atendente da equipe vai continuar seu atendimento."));
        Assert.Equal(AiReplyOutcome.Replied, await Respond(f, id));
        var lead = await f.WithDb(db => db.Leads.AsNoTracking().SingleAsync());
        Assert.Equal(ConversationMode.AtendimentoHumano, lead.Mode);
        Assert.Equal(1, await f.RunQueueAsync()); // aviso de transição sai mesmo com a IA pausada
        // nova mensagem do cliente não gera resposta da IA
        await Inbound(f, "M2", "Alô?");
        Assert.Equal(AiReplyOutcome.Skipped, await Respond(f, id));
    }

    [Fact]
    public async Task IA_desativada_ou_erro_do_provedor()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        var id = await Inbound(f, "M1", "Oi");
        Assert.Equal(AiReplyOutcome.Disabled, await Respond(f, id));

        await EnableAi(f, c);
        f.Ai.Script.Enqueue(_ => new AiCompletion(false, null, [], null, 0, 0, "OpenAI HTTP 429: quota", 429));
        Assert.Equal(AiReplyOutcome.Failed, await Respond(f, id));
        var lead = await f.WithDb(db => db.Leads.AsNoTracking().SingleAsync());
        Assert.Equal(ConversationMode.AtendimentoHumano, lead.Mode); // não deixa o cliente sem retorno: vai para humano
        Assert.Contains("IA indisponível", lead.NextAction);
    }

    [Fact]
    public async Task Limite_diario_de_consumo()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        (await c.PutAsJsonAsync("/api/ai/settings", new AiSettingsUpdate(true, "gpt-4o", "sk-x", 0.4, 600, "Lia", "", 100, false, 0))).EnsureSuccessStatusCode();
        var id = await Inbound(f, "M1", "Oi");
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Olá!")); // consome 120 tokens > limite 100
        Assert.Equal(AiReplyOutcome.Replied, await Respond(f, id));
        await f.RunQueueAsync();
        await f.WithDb(async db => { var l = await db.Leads.SingleAsync(); l.Mode = ConversationMode.Ia; return await db.SaveChangesAsync(); });
        await Inbound(f, "M2", "E o preço?");
        Assert.Equal(AiReplyOutcome.Failed, await Respond(f, id));
        Assert.Contains("Limite diário", (await f.WithDb(db => db.Leads.AsNoTracking().SingleAsync())).NextAction);
    }

    [Fact]
    public async Task Simulacao_nao_salva_nada_e_modelo_gpt5_sem_temperatura()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await EnableAi(f, c, "gpt-5");
        f.Ai.Script.Enqueue(_ => FakeAi.Tools(("atualizar_qualificacao", """{"experiencia":"Iniciante"}""")));
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Que bom! Você já conhece como funciona um painel de revenda?"));
        var r = await (await c.PostAsJsonAsync("/api/ai/simulate", new AiSimulateRequest([new SimTurn(true, "Quero começar a revender")]))).Content.ReadFromJsonAsync<AiSimulateResponse>();
        Assert.True(r!.Ok); Assert.Contains("painel", r.Reply);
        Assert.Equal(0, await f.WithDb(db => db.Leads.CountAsync()));
        Assert.False(OpenAiClient.SupportsTemperature("gpt-5"));
        Assert.True(OpenAiClient.SupportsTemperature("gpt-4o"));
    }

    [Fact]
    public async Task Base_de_conhecimento_so_entra_no_prompt_apos_aprovacao()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await EnableAi(f, c);
        var kb = await c.GetFromJsonAsync<List<KnowledgeDto>>("/api/knowledge");
        var art = kb!.First(k => k.Title == "Canais e horário");
        await c.PostAsJsonAsync("/api/ai/simulate", new AiSimulateRequest([new SimTurn(true, "Oi")]));
        Assert.DoesNotContain("98815-9045", f.Ai.Requests.Last()[0]!["content"]!.GetValue<string>());
        (await c.PostAsync($"/api/knowledge/{art.Id}/approve?approve=true", null)).EnsureSuccessStatusCode();
        await c.PostAsJsonAsync("/api/ai/simulate", new AiSimulateRequest([new SimTurn(true, "Oi")]));
        Assert.Contains("98815-9045", f.Ai.Requests.Last()[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenAiClient_monta_requisicao_e_le_ferramentas()
    {
        var handler = new CaptureHandler("""{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"consultar_planos","arguments":"{}"}}]}}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""");
        var r = await new OpenAiClient(new HttpClient(handler)).CompleteAsync("sk-abc", "gpt-5", [new JsonObject { ["role"] = "user", ["content"] = "oi" }], AiAgent.Tools(), 0.7, 300);
        Assert.True(r.Ok); Assert.Equal("consultar_planos", r.ToolCalls.Single().Name); Assert.Equal(15, r.PromptTokens + r.CompletionTokens);
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.Url);
        Assert.Equal("Bearer sk-abc", handler.Auth);
        var body = JsonNode.Parse(handler.Body!)!;
        Assert.Null(body["temperature"]); // gpt-5 não aceita temperatura
        Assert.Equal(300, body["max_completion_tokens"]!.GetValue<int>());
        Assert.Equal(7, body["tools"]!.AsArray().Count);
    }

    private sealed class CaptureHandler(string response) : HttpMessageHandler
    {
        public string? Url, Auth, Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Url = r.RequestUri!.AbsoluteUri; Auth = r.Headers.Authorization?.ToString(); Body = await r.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
