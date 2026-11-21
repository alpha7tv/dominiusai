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
    public readonly Queue<Func<AiRequest, AiCompletion>> Script = new();
    public readonly List<AiRequest> Requests = [];
    public List<string> Models => Requests.Select(r => r.Model).ToList();

    public static AiCompletion Text(string t) => new(true, t, [], null, 100, 20, null, 200, "end_turn");
    public static AiCompletion Tools(params (string Name, string Args)[] calls) =>
        new(true, null, calls.Select((c, i) => new AiToolCall("toolu_" + i, c.Name, c.Args)).ToList(), null, 120, 30, null, 200, "tool_use");

    public Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        Requests.Add(request with { Turns = request.Turns.ToList() });
        return Task.FromResult(Script.Count > 0 ? Script.Dequeue()(request) : Text("ok"));
    }

    public Task<(bool Ok, IReadOnlyList<string> Models, string? Error)> ListModelsAsync(string apiKey, CancellationToken ct = default) =>
        Task.FromResult<(bool, IReadOnlyList<string>, string?)>((true, ["claude-haiku-5-5", "claude-sonnet-5-5"], null));
}

public class AiTests
{
    private static async Task EnableAi(CrmFactory f, HttpClient c, string model = "claude-haiku-5-5")
    {
        await f.ConfigureEvolutionAsync(c);
        (await c.PutAsJsonAsync("/api/ai/settings", new AiSettingsUpdate(true, model, "claude-sonnet-5-5", "sk-ant-teste-123", 4000, "Lia", "", 100000, false, 0))).EnsureSuccessStatusCode();
    }

    private static async Task<Guid> Inbound(CrmFactory f, string id, string text, string pushName = "João")
    {
        var body = $$$"""{"event":"messages.upsert","instance":"dominius","data":{"key":{"remoteJid":"5514988159045@s.whatsapp.net","fromMe":false,"id":"{{{id}}}"},"pushName":"{{{pushName}}}","message":{"conversation":"{{{text}}}"},"messageTimestamp":{{{f.Clock.GetUtcNow().ToUnixTimeSeconds()}}}}}""";
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
    public async Task Chave_da_Anthropic_criptografada_e_nunca_retornada()
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
    public async Task Script_de_atendimento_e_prompt_de_sistema_vao_no_prompt_sem_limite_de_tamanho()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await EnableAi(f, c);

        // padrão: prompt de sistema do sistema, regras fixas e nenhum script
        var s0 = await c.GetFromJsonAsync<AiSettingsDto>("/api/ai/settings");
        Assert.Equal("", s0!.SystemPrompt);
        Assert.Contains("{nome_assistente}", s0.DefaultSystemPrompt);
        Assert.Contains("REGRAS FIXAS DO SISTEMA", s0.FixedRules);

        // script grande (sem limite de caracteres) e prompt de sistema personalizado
        var script = "TOM: descontraído, sempre chame de parceiro.\nOFERTA: comece pelo Mensalista 50.\n" + string.Concat(Enumerable.Repeat("Objeção de preço: explique o custo por cliente ativo. ", 3000));
        Assert.True(script.Length > 150_000);
        var custom = "Você é {nome_assistente}, consultora da Dominius Play. Fale sempre em frases curtas.";
        (await c.PutAsJsonAsync("/api/ai/settings", new AiSettingsUpdate(true, "claude-haiku-5-5", "claude-sonnet-5-5", null, 4000, "Lia", script, 10_000_000, false, 0, custom))).EnsureSuccessStatusCode();
        var s1 = await c.GetFromJsonAsync<AiSettingsDto>("/api/ai/settings");
        Assert.Equal(script, s1!.Instructions); // salvo inteiro, sem corte
        Assert.Equal(custom, s1.SystemPrompt);

        var id = await Inbound(f, "S1", "oi");
        Assert.Equal(AiReplyOutcome.Replied, await Respond(f, id));
        var system = f.Ai.Requests[0].System;
        Assert.StartsWith("Você é Lia, consultora da Dominius Play.", system); // placeholder trocado pelo nome
        Assert.DoesNotContain("ROTEIRO DE DIAGNÓSTICO", system); // prompt padrão substituído
        Assert.Contains("REGRAS FIXAS DO SISTEMA", system); // regras fixas continuam
        Assert.Contains("transferir_para_humano", system);
        Assert.Contains("SCRIPT DE ATENDIMENTO DA EMPRESA", system);
        Assert.Contains(script.Trim(), system);
        Assert.True(system.IndexOf("SCRIPT DE ATENDIMENTO", StringComparison.Ordinal) < system.IndexOf("PLANOS APROVADOS E ATIVOS", StringComparison.Ordinal));

        // sem enviar SystemPrompt (app antigo) mantém o atual; texto vazio volta ao padrão
        (await c.PutAsJsonAsync("/api/ai/settings", new AiSettingsUpdate(true, "claude-haiku-5-5", "claude-sonnet-5-5", null, 4000, "Lia", "Script curto.", 10_000_000, false, 0))).EnsureSuccessStatusCode();
        Assert.Equal(custom, (await c.GetFromJsonAsync<AiSettingsDto>("/api/ai/settings"))!.SystemPrompt);
        (await c.PutAsJsonAsync("/api/ai/settings", new AiSettingsUpdate(true, "claude-haiku-5-5", "claude-sonnet-5-5", null, 4000, "Lia", "Script curto.", 10_000_000, false, 0, ""))).EnsureSuccessStatusCode();
        Assert.Equal("", (await c.GetFromJsonAsync<AiSettingsDto>("/api/ai/settings"))!.SystemPrompt);
        f.Ai.Requests.Clear();
        // a simulação da tela de IA usa a mesma configuração salva
        (await c.PostAsJsonAsync("/api/ai/simulate", new AiSimulateRequest([new SimTurn(true, "tudo bem?")]))).EnsureSuccessStatusCode();
        system = f.Ai.Requests[0].System;
        Assert.StartsWith("Você é \"Lia\", ASSISTENTE VIRTUAL", system);
        Assert.Contains("ROTEIRO DE DIAGNÓSTICO", system);
        Assert.Contains("Script curto.", system);
    }

    [Fact]
    public async Task IA_conversa_com_lead_importado_so_com_numero()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await EnableAi(f, c);
        var imp = await (await c.PostAsJsonAsync("/api/import/paste", new PasteImportRequest("14 98815-9045", null, null, "Pediram contato no Instagram"))).Content.ReadFromJsonAsync<ImportResponse>();
        Assert.Equal((1, 1), (imp!.Created, imp.WithConsent));
        var id = await Inbound(f, "N1", "oi, quem é?", pushName: "");
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Olá! Tudo bem? Sou a Lia, assistente virtual da Dominius Play. Você já trabalha com revenda de IPTV ou está pensando em começar?"));
        Assert.Equal(AiReplyOutcome.Replied, await Respond(f, id));

        var system = f.Ai.Requests[0].System;
        Assert.Contains("- Nome: não informado", system);
        Assert.Contains("CONTATO SEM NOME", system);
        Assert.Contains("NÃO use nome nenhum", system);
        var card = (await c.GetFromJsonAsync<List<LeadCard>>("/api/leads"))!.Single();
        Assert.Equal("+55 (14) 98815-9045", card.Name); // sem nome, o card mostra o número
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
        var system = f.Ai.Requests[0].System;
        Assert.Contains("ASSISTENTE VIRTUAL", system);
        Assert.Contains("R$ 250,00", system);
        Assert.Equal("claude-haiku-5-5", f.Ai.Models[0]); // atendimento usa o modelo rápido
        Assert.All(f.Ai.Requests, r => Assert.Equal(10, r.Tools!.Count)); // inclui consultar_base_conhecimento (v0.4.1)
        Assert.True(f.Ai.Requests[0].AllowTools);
        // segunda chamada reenviou a resposta com ferramentas e os resultados, nessa ordem
        Assert.IsType<AiAssistantToolCalls>(f.Ai.Requests[1].Turns[^2]);
        Assert.Equal(4, Assert.IsType<AiToolResults>(f.Ai.Requests[1].Turns[^1]).Results.Count);

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
        var id = await Inbound(f, "M1", "Tenho uma dúvida sobre o pagamento do plano");
        f.Ai.Script.Enqueue(_ => FakeAi.Tools(("transferir_para_humano", """{"motivo":"pagamento"}""")));
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
        f.Ai.Script.Enqueue(_ => new AiCompletion(false, null, [], null, 0, 0, "Anthropic HTTP 429: rate limit", 429));
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
        (await c.PutAsJsonAsync("/api/ai/settings", new AiSettingsUpdate(true, "claude-haiku-5-5", "claude-sonnet-5-5", "sk-ant-x", 4000, "Lia", "", 100, false, 0))).EnsureSuccessStatusCode();
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
    public async Task Simulacao_nao_salva_nada()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await EnableAi(f, c);
        f.Ai.Script.Enqueue(_ => FakeAi.Tools(("atualizar_qualificacao", """{"experiencia":"Iniciante"}""")));
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Que bom! Você já conhece como funciona um painel de revenda?"));
        var r = await (await c.PostAsJsonAsync("/api/ai/simulate", new AiSimulateRequest([new SimTurn(true, "Quero começar a revender")]))).Content.ReadFromJsonAsync<AiSimulateResponse>();
        Assert.True(r!.Ok); Assert.Contains("painel", r.Reply);
        Assert.Equal(0, await f.WithDb(db => db.Leads.CountAsync()));
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
        Assert.DoesNotContain("98815-9045", f.Ai.Requests.Last().System);
        (await c.PostAsync($"/api/knowledge/{art.Id}/approve?approve=true", null)).EnsureSuccessStatusCode();
        await c.PostAsJsonAsync("/api/ai/simulate", new AiSimulateRequest([new SimTurn(true, "Oi")]));
        Assert.Contains("98815-9045", f.Ai.Requests.Last().System);
    }

    [Fact]
    public async Task Negociacao_usa_modelo_avancado_e_ultima_rodada_sem_ferramentas()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await EnableAi(f, c);
        var id = await Inbound(f, "M1", "Quero fechar o Sigma 600");
        await f.WithDb(async db => { var l = await db.Leads.SingleAsync(); l.Stage = KanbanStage.EmNegociacao; return await db.SaveChangesAsync(); });
        for (var i = 0; i < AiAgent.MaxToolRounds; i++) f.Ai.Script.Enqueue(_ => FakeAi.Tools(("consultar_planos", "{}")));
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Perfeito, vou te passar para a equipe."));
        Assert.Equal(AiReplyOutcome.Replied, await Respond(f, id));
        Assert.All(f.Ai.Models, m => Assert.Equal("claude-sonnet-5-5", m));
        Assert.Equal(AiAgent.MaxToolRounds + 1, f.Ai.Requests.Count);
        Assert.False(f.Ai.Requests[^1].AllowTools); // última rodada obriga resposta em texto
    }

    [Fact]
    public async Task Configuracao_aceita_so_modelos_claude_e_chave_anthropic()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var bad1 = await c.PutAsJsonAsync("/api/ai/settings", new AiSettingsUpdate(true, "gpt-4o", "claude-sonnet-5-5", null, 4000, "Lia", "", 1000, false, 0));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad1.StatusCode);
        var bad2 = await c.PutAsJsonAsync("/api/ai/settings", new AiSettingsUpdate(true, "claude-haiku-5-5", "claude-sonnet-5-5", "sk-proj-abc", 4000, "Lia", "", 1000, false, 0));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad2.StatusCode);
        var s0 = await c.GetFromJsonAsync<AiSettingsDto>("/api/ai/settings");
        Assert.False(s0!.HasApiKey);
        Assert.Equal(("claude-haiku-5-5", "claude-sonnet-5-5"), (s0.Model, s0.AdvancedModel)); // padrões
        (await c.PutAsJsonAsync("/api/ai/settings", new AiSettingsUpdate(true, "claude-haiku-5-5", "claude-opus-5-5", "sk-ant-segredo-xyz", 4000, "Lia", "", 1000, false, 0))).EnsureSuccessStatusCode();
        var s1 = await c.GetFromJsonAsync<AiSettingsDto>("/api/ai/settings");
        Assert.True(s1!.HasApiKey); Assert.Equal("claude-opus-5-5", s1.AdvancedModel);
        // a chave fica criptografada no banco e nunca volta para o app
        var raw = await f.WithDb(db => db.Settings.AsNoTracking().SingleAsync(x => x.Key == SettingKeys.AiApiKey));
        Assert.DoesNotContain("segredo", raw.Value);
        Assert.DoesNotContain("segredo", await c.GetStringAsync("/api/ai/settings"));
    }

    [Fact]
    public async Task AnthropicClient_monta_requisicao_e_preserva_raciocinio()
    {
        var handler = new CaptureHandler("""
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5","stop_reason":"tool_use","stop_sequence":null,
         "content":[{"type":"thinking","thinking":"pensando","signature":"sig123"},{"type":"tool_use","id":"toolu_1","name":"consultar_planos","input":{}}],
         "usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":3,"cache_creation_input_tokens":0}}
        """);
        var client = new AnthropicAiClient(new HttpClient(handler));
        var req = new AiRequest("sk-ant-abc", "claude-haiku-5-5", "SISTEMA", [new AiUserText("oi"), new AiAssistantText("Olá!"), new AiUserText("preço?")], AiAgent.Tools(), 4000);
        var r = await client.CompleteAsync(req);
        Assert.True(r.Ok, r.Error); Assert.Equal("consultar_planos", r.ToolCalls.Single().Name); Assert.Equal(18, r.InputTokens + r.OutputTokens);
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Url);
        Assert.True(handler.ApiKey == "sk-ant-abc" || handler.Auth == "Bearer sk-ant-abc", $"{handler.ApiKey}|{handler.Auth?[..Math.Min(20, handler.Auth.Length)]}");
        var body = JsonNode.Parse(handler.Body!)!;
        Assert.Equal("claude-haiku-5-5", body["model"]!.GetValue<string>());
        Assert.Equal("SISTEMA", body["system"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("ephemeral", body["system"]![0]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal("low", body["output_config"]!["effort"]!.GetValue<string>());
        Assert.Null(body["temperature"]); Assert.Null(body["tool_choice"]);
        Assert.Equal(10, body["tools"]!.AsArray().Count);
        Assert.Equal("object", body["tools"]![1]!["input_schema"]!["type"]!.GetValue<string>());
        Assert.Equal(["user", "assistant", "user"], body["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()));

        // segunda rodada: reenvia raciocínio assinado + tool_use sem alterar, resultados no turno do cliente; ferramentas bloqueadas
        handler.Response = """
        {"id":"msg_2","type":"message","role":"assistant","model":"claude-sonnet-5-5","stop_reason":"end_turn","stop_sequence":null,
         "content":[{"type":"text","text":"O Mensalista 50 custa R$ 50."}],"usage":{"input_tokens":20,"output_tokens":8}}
        """;
        var r2 = await client.CompleteAsync(req with
        {
            Model = "claude-sonnet-5-5", AllowTools = false,
            Turns = [.. req.Turns, r.AsTurn(), new AiToolResults([new AiToolResult("toolu_1", "[]")])],
        });
        Assert.True(r2.Ok); Assert.Equal("O Mensalista 50 custa R$ 50.", r2.Content);
        body = JsonNode.Parse(handler.Body!)!;
        Assert.Equal("medium", body["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("none", body["tool_choice"]!["type"]!.GetValue<string>());
        var msgs = body["messages"]!.AsArray();
        Assert.Equal(5, msgs.Count);
        Assert.Equal("sig123", msgs[3]!["content"]![0]!["signature"]!.GetValue<string>());
        Assert.Equal("tool_use", msgs[3]!["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", msgs[4]!["content"]![0]!["tool_use_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnthropicClient_erros_e_recusa_viram_falha_controlada()
    {
        var handler = new CaptureHandler("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", System.Net.HttpStatusCode.Unauthorized);
        var client = new AnthropicAiClient(new HttpClient(handler));
        var req = new AiRequest("sk-ant-errada", "claude-haiku-5-5", "S", [new AiUserText("oi")], null, 1000);
        var r = await client.CompleteAsync(req);
        Assert.False(r.Ok); Assert.Equal(401, r.HttpStatus); Assert.Contains("Anthropic HTTP 401", r.Error);

        handler.Status = System.Net.HttpStatusCode.OK;
        handler.Response = """{"id":"m","type":"message","role":"assistant","model":"claude-haiku-5-5","stop_reason":"refusal","stop_sequence":null,"content":[],"usage":{"input_tokens":5,"output_tokens":0}}""";
        r = await client.CompleteAsync(req);
        Assert.False(r.Ok); Assert.Equal("refusal", r.StopReason);
    }

    private sealed class CaptureHandler(string response, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) : HttpMessageHandler
    {
        public string Response = response;
        public System.Net.HttpStatusCode Status = status;
        public string? Url, ApiKey, Auth, Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Url = r.RequestUri!.GetLeftPart(UriPartial.Path);
            ApiKey = r.Headers.TryGetValues("x-api-key", out var v) ? v.Single() : null;
            Auth = r.Headers.Authorization?.ToString();
            Body = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
        }
    }
}
