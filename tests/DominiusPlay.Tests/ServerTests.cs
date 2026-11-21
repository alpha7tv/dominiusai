using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Evolution;
using DominiusPlay.Infrastructure.Security;
using DominiusPlay.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DominiusPlay.Tests;

public class FakeEvolution : IEvolutionApi
{
    public List<(string Number, string Text)> Sent = [];
    public Func<int, EvolutionSendResult>? Behavior;
    public string State = "open";
    public Task<EvolutionConnectionState> GetConnectionStateAsync(EvolutionConfig c, CancellationToken ct = default) => Task.FromResult(new EvolutionConnectionState(true, State, null));
    public Task<EvolutionSendResult> SendTextAsync(EvolutionConfig c, string n, string t, CancellationToken ct = default)
    {
        lock (Sent)
        {
            var r = Behavior?.Invoke(Sent.Count) ?? new EvolutionSendResult(true, "WA" + Guid.NewGuid().ToString("N")[..10], "PENDING", null, 201);
            if (r.Success) Sent.Add((n, t));
            return Task.FromResult(r);
        }
    }
    public Task<bool> SendPresenceAsync(EvolutionConfig c, string n, string p, int d, CancellationToken ct = default) => Task.FromResult(true);
    public Task<(bool Ok, string Body)> SetWebhookAsync(EvolutionConfig c, string u, IDictionary<string, string> h, IEnumerable<string> e, CancellationToken ct = default) => Task.FromResult((true, "{}"));
    public Task<(bool Ok, string Body)> FindWebhookAsync(EvolutionConfig c, CancellationToken ct = default) => Task.FromResult((true, "{}"));
    public Task<(bool Ok, string Body)> FetchInstancesAsync(EvolutionConfig c, CancellationToken ct = default) => Task.FromResult((true, "[]"));
    public Task<IReadOnlyList<(string Number, bool Exists, string? Jid)>> CheckWhatsAppNumbersAsync(EvolutionConfig c, IEnumerable<string> n, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string, bool, string?)>>([]);
}

public sealed class CrmFactory(bool realAi = false, bool shippedConnection = false) : WebApplicationFactory<Program>
{
    public readonly string DataDir = Path.Combine(Path.GetTempPath(), "dpcrm-tests-" + Guid.NewGuid().ToString("N"));
    public readonly FakeEvolution Evolution = new();
    public readonly FakeAi Ai = new();
    public readonly FakeTimeProvider Clock = new(DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        Directory.CreateDirectory(DataDir);
        b.UseSetting("DataDirectory", DataDir);
        b.UseSetting("Workers:Enabled", "false");
        // shippedConnection: igual ao appsettings.json instalado (conexão vazia → crm.db no diretório de dados)
        b.UseSetting("ConnectionStrings:Crm", shippedConnection ? "" : $"Data Source={Path.Combine(DataDir, "crm.db")}");
        b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IEvolutionApi>(Evolution);
            if (!realAi) s.AddSingleton<DominiusPlay.Infrastructure.Ai.IAiProvider>(Ai);
            s.AddSingleton<TimeProvider>(Clock);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDir, true); } catch { }
    }

    public async Task<HttpClient> AdminAsync()
    {
        var c = CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/setup", new SetupRequest("carlos", "Carlos", "SenhaForte123"));
        if (res.StatusCode == HttpStatusCode.Conflict) res = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest("carlos", "SenhaForte123"));
        var token = (await res.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public async Task ConfigureEvolutionAsync(HttpClient c)
    {
        var r = await c.PutAsJsonAsync("/api/integrations/evolution", new EvolutionSettingsUpdate("https://evo.exemplo.com", "dominius", "CHAVE", "WHATSAPP-BAILEYS", "https://crm.exemplo.com"));
        r.EnsureSuccessStatusCode();
    }

    public async Task<string> WebhookTokenAsync()
    {
        using var scope = Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<SettingsStore>().GetAsync(SettingKeys.WebhookToken))!;
    }

    public async Task<T> WithDb<T>(Func<CrmDbContext, Task<T>> f)
    {
        using var scope = Services.CreateScope();
        return await f(scope.ServiceProvider.GetRequiredService<CrmDbContext>());
    }

    public async Task<int> RunQueueAsync()
    {
        // relógio de teste é congelado; itens criados "agora" (relógio real) precisam ficar vencidos
        if (Clock.GetUtcNow() < DateTimeOffset.UtcNow) Clock.SetUtcNow(DateTimeOffset.UtcNow);
        Clock.Advance(TimeSpan.FromSeconds(1));
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OutboundQueueProcessor>().ProcessDueAsync(50);
    }
}

public class ServerTests
{
    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    private static string Upsert(string id, string phone, string text, bool fromMe = false, long ts = 1763474400) => $$$"""
        {"event":"messages.upsert","instance":"dominius","data":{"key":{"remoteJid":"{{{phone}}}@s.whatsapp.net","fromMe":{{{(fromMe ? "true" : "false")}}},"id":"{{{id}}}"},
        "pushName":"João","message":{"conversation":"{{{text}}}"},"messageType":"conversation","messageTimestamp":{{{ts}}}}}
        """;

    private static async Task<HttpResponseMessage> PostWebhook(CrmFactory f, string body, string? token = null)
    {
        var c = f.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/evolution") { Content = Json(body) };
        req.Headers.Add("x-dominius-token", token ?? await f.WebhookTokenAsync());
        return await c.SendAsync(req);
    }

    [Fact]
    public async Task Setup_login_e_protecao_da_api()
    {
        using var f = new CrmFactory();
        var anon = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/leads")).StatusCode);
        Assert.True((await anon.GetFromJsonAsync<SetupStatus>("/api/auth/setup"))!.NeedsSetup);
        var c = await f.AdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/leads")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await anon.PostAsJsonAsync("/api/auth/setup", new SetupRequest("x", "x", "12345678"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/auth/login", new LoginRequest("carlos", "errada"))).StatusCode);
    }

    [Fact]
    public async Task Planos_da_campanha_carregados_e_versionados()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var plans = await c.GetFromJsonAsync<List<PlanDto>>("/api/plans");
        Assert.Equal(7, plans!.Count);
        Assert.Equal(250m, plans.Single(p => p.Code == "APONTAMENTO_SIGMA_600").MonthlyPrice);
        // nova versão entra como rascunho; só vira ativa após aprovação, que arquiva a anterior
        var v2 = await (await c.PostAsJsonAsync("/api/plans", new PlanUpsert("APONTAMENTO_SIGMA_600", "Apontamento Sigma 600", 270m, null, 600, "d", "b", "", "", "", null, null))).Content.ReadFromJsonAsync<PlanDto>();
        Assert.Equal(2, v2!.Version); Assert.False(v2.IsActiveNow);
        await c.PostAsync($"/api/plans/{v2.Id}/approve", null);
        plans = await c.GetFromJsonAsync<List<PlanDto>>("/api/plans");
        var sigma = plans!.Where(p => p.Code == "APONTAMENTO_SIGMA_600").ToList();
        Assert.Equal(270m, sigma.Single(p => p.IsActiveNow).MonthlyPrice);
        Assert.Equal(ApprovalStatus.Arquivado, sigma.Single(p => p.Version == 1).Approval);
    }

    [Fact]
    public async Task Importacao_por_arquivo_e_colar_via_api()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("nome;telefone\nAna;(14) 98815-9045\nAna2;14988159045\nX;999\n")), "file", "lista.csv" } };
        var r = await (await c.PostAsync("/api/import/file", form)).Content.ReadFromJsonAsync<ImportResponse>();
        Assert.Equal((1, 1, 1), (r!.Created, r.Duplicates, r.Invalid));
        var p = await (await c.PostAsJsonAsync("/api/import/paste", new PasteImportRequest("Bia - 21 99999-8888\nAna;14 98815-9045", "Indicação", null))).Content.ReadFromJsonAsync<ImportResponse>();
        Assert.Equal((1, 1), (p!.Created, p.Updated));
        var pend = await c.GetFromJsonAsync<List<LeadCard>>("/api/leads?consent=Pendente");
        Assert.Equal(2, pend!.Count);
    }

    [Fact]
    public async Task Importacao_so_de_numeros_sem_nome_e_cadastro_manual_sem_nome()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        // arquivo só com números, sem cabeçalho
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("14988159045\n(21) 99999-8888\n11 98765-4321\n")), "file", "numeros.txt" } };
        form.Add(new StringContent("Lista do Instagram"), "consentSource");
        var r = await (await c.PostAsync("/api/import/file", form)).Content.ReadFromJsonAsync<ImportResponse>();
        Assert.Equal((3, 3, 0), (r!.Created, r.WithConsent, r.Invalid));
        // cadastro manual sem nome
        var lead = await (await c.PostAsJsonAsync("/api/leads", new LeadUpsert("", "31 99888-7766", null, null, null, null, null, IptvExperience.NaoInformado, null, null, PainCategory.Nenhuma, null, null, Priority.Normal, null))).Content.ReadFromJsonAsync<LeadCard>();
        Assert.Equal("+55 (31) 99888-7766", lead!.Name);
        var cards = await c.GetFromJsonAsync<List<LeadCard>>("/api/leads");
        Assert.Equal(4, cards!.Count);
        Assert.All(cards, x => Assert.StartsWith("+55 (", x.Name)); // o número aparece no lugar do nome
        Assert.Equal(3, (await c.GetFromJsonAsync<List<LeadCard>>("/api/leads?consent=Valido"))!.Count);
        var kanban = await c.GetStringAsync("/api/kanban");
        Assert.Contains("(21) 99999-8888", kanban);
    }

    [Fact]
    public async Task Acoes_em_lote_autorizar_por_selecao_e_por_tag_remover_tag_e_excluir()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var imp = await (await c.PostAsJsonAsync("/api/import/paste", new PasteImportRequest("11 98888-0001\n11 98888-0002\n11 98888-0003\n11 98888-0004", "Grupos", "iptv"))).Content.ReadFromJsonAsync<ImportResponse>();
        Assert.Equal(4, imp!.Created);
        await c.PostAsJsonAsync("/api/import/paste", new PasteImportRequest("21 97777-0001", "Outra", "iptvplus"));
        await c.PostAsJsonAsync("/api/import/optouts", new OptOutImportRequest("11 98888-0004", "pediu para sair"));
        var leads = (await c.GetFromJsonAsync<List<LeadCard>>("/api/leads"))!;
        var iptv = leads.Where(l => l.Tags == "iptv").OrderBy(l => l.Phone).ToList();

        // sem origem do consentimento não autoriza
        var semOrigem = await c.PostAsJsonAsync("/api/leads/bulk", new BulkLeadRequest(BulkLeadAction.Autorizar, [iptv[0].Id]));
        Assert.Equal(HttpStatusCode.BadRequest, semOrigem.StatusCode);

        // seleção: 1 lead
        var r1 = await (await c.PostAsJsonAsync("/api/leads/bulk", new BulkLeadRequest(BulkLeadAction.Autorizar, [iptv[0].Id], ConsentSource: "Pediram informações nos grupos"))).Content.ReadFromJsonAsync<BulkLeadResponse>();
        Assert.Equal(1, r1!.Affected);

        // por tag: os 2 restantes; o já autorizado e o descadastrado são ignorados; "iptvplus" não entra
        var r2 = await (await c.PostAsJsonAsync("/api/leads/bulk", new BulkLeadRequest(BulkLeadAction.Autorizar, null, ByTag: "IPTV", ConsentSource: "Pediram informações nos grupos"))).Content.ReadFromJsonAsync<BulkLeadResponse>();
        Assert.Equal((2, 2), (r2!.Affected, r2.Skipped));
        var validos = (await c.GetFromJsonAsync<List<LeadCard>>("/api/leads?consent=Valido"))!;
        Assert.Equal(3, validos.Count);
        Assert.All(validos, l => Assert.Equal("iptv", l.Tags));
        Assert.Equal(3, await f.WithDb(db => db.ConsentRecords.CountAsync(x => x.Source == "Pediram informações nos grupos")));

        // remover tag dos selecionados
        var r3 = await (await c.PostAsJsonAsync("/api/leads/bulk", new BulkLeadRequest(BulkLeadAction.RemoverTag, [iptv[0].Id, iptv[1].Id], Tag: "iptv"))).Content.ReadFromJsonAsync<BulkLeadResponse>();
        Assert.Equal(2, r3!.Affected);
        Assert.Equal("", (await c.GetFromJsonAsync<LeadDetail>($"/api/leads/{iptv[0].Id}"))!.Tags);

        // excluir selecionados
        var r4 = await (await c.PostAsJsonAsync("/api/leads/bulk-delete", new BulkLeadRequest(BulkLeadAction.Excluir, [iptv[0].Id, iptv[1].Id]))).Content.ReadFromJsonAsync<BulkLeadResponse>();
        Assert.Equal(2, r4!.Affected);
        Assert.Equal(3, (await c.GetFromJsonAsync<List<LeadCard>>("/api/leads"))!.Count);
    }

    [Fact]
    public async Task Kanban_move_e_negocio_fechado_exige_confirmacao()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var lead = await (await c.PostAsJsonAsync("/api/leads", new LeadUpsert("João", "14988159045", null, null, null, null, null, IptvExperience.JaTrabalha, "Sigma", 350, PainCategory.QuedasServidor, null, null, Priority.Normal, null))).Content.ReadFromJsonAsync<LeadCard>();
        var r1 = await (await c.PostAsJsonAsync($"/api/kanban/{lead!.Id}/move", new MoveCardRequest(KanbanStage.NegocioFechado, null, false))).Content.ReadFromJsonAsync<MoveCardResponse>();
        Assert.False(r1!.Applied);
        var r2 = await (await c.PostAsJsonAsync($"/api/kanban/{lead.Id}/move", new MoveCardRequest(KanbanStage.EmNegociacao, "pediu proposta", false))).Content.ReadFromJsonAsync<MoveCardResponse>();
        Assert.True(r2!.Applied);
        var board = await c.GetFromJsonAsync<List<KanbanColumn>>("/api/kanban");
        Assert.Equal(10, board!.Count); // 9 etapas do funil + "Em fase de teste"; "Em dúvidas" oculta sem cards
        Assert.Equal(lead.Id, board.Single(b => b.Stage == KanbanStage.EmNegociacao).Cards.Single().Id);
        var detail = await c.GetFromJsonAsync<LeadDetail>($"/api/leads/{lead.Id}");
        Assert.Single(detail!.StageHistory);
        Assert.Contains("Apontamento Sigma 600", detail.Recommendation!.PlanNames.Single());
    }

    [Fact]
    public async Task Webhook_exige_token_e_deduplica()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhook(f, Upsert("A1", "5514988159045", "oi"), "errado")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(f, Upsert("A1", "5514988159045", "Já trabalho, meu servidor cai muito"))).StatusCode);
        await PostWebhook(f, Upsert("A1", "5514988159045", "Já trabalho, meu servidor cai muito")); // repetido
        var leads = await c.GetFromJsonAsync<List<LeadCard>>("/api/conversations");
        var lead = Assert.Single(leads!);
        Assert.Equal(ConversationMode.Ia, lead.Mode);
        Assert.Equal(PainCategory.QuedasServidor, lead.MainPain);
        Assert.Equal(ConsentStatus.Pendente, lead.Consent); // contato iniciar conversa ≠ consentimento de prospecção
        var msgs = await c.GetFromJsonAsync<List<MessageDto>>($"/api/conversations/{lead.Id}/messages");
        Assert.Single(msgs!);
    }

    [Fact]
    public async Task Mensagem_manual_no_aparelho_pausa_IA_e_cancela_fila()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        await PostWebhook(f, Upsert("B1", "5514988159045", "oi"));
        var leadId = await f.WithDb(db => db.Leads.Select(l => l.Id).SingleAsync());
        await f.WithDb(async db => { db.OutboundQueue.Add(new OutboundQueueItem { LeadId = leadId, Kind = OutboundKind.Resposta, Text = "resposta IA", InstanceName = "dominius" }); return await db.SaveChangesAsync(); });
        await PostWebhook(f, Upsert("B2", "5514988159045", "Oi João, aqui é o Carlos", fromMe: true));
        var lead = await f.WithDb(db => db.Leads.SingleAsync());
        Assert.Equal(ConversationMode.AtendimentoHumano, lead.Mode);
        Assert.Equal(MessageStatus.Cancelada, await f.WithDb(db => db.OutboundQueue.Select(q => q.Status).SingleAsync()));
        Assert.Equal(0, await f.RunQueueAsync());
        Assert.Empty(f.Evolution.Sent);
    }

    [Fact]
    public async Task Assumir_atendimento_bloqueia_IA_e_humano_responde()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        await PostWebhook(f, Upsert("C1", "5514988159045", "Quero saber dos planos", ts: f.Clock.GetUtcNow().AddMinutes(-1).ToUnixTimeSeconds()));
        var leadId = await f.WithDb(db => db.Leads.Select(l => l.Id).SingleAsync());
        await f.WithDb(async db => { db.OutboundQueue.Add(new OutboundQueueItem { LeadId = leadId, Kind = OutboundKind.Resposta, Text = "IA", InstanceName = "dominius" }); return await db.SaveChangesAsync(); });

        // humano sem assumir não consegue enviar
        var s0 = await (await c.PostAsJsonAsync($"/api/conversations/{leadId}/send", new SendMessageRequest("Olá!"))).Content.ReadFromJsonAsync<SendMessageResponse>();
        Assert.False(s0!.Queued);

        var t = await (await c.PostAsync($"/api/conversations/{leadId}/takeover", null)).Content.ReadFromJsonAsync<OperationResult>();
        Assert.True(t!.Ok); Assert.Contains("1 envio", t.Message);
        var s1 = await (await c.PostAsJsonAsync($"/api/conversations/{leadId}/send", new SendMessageRequest("Olá, sou o Carlos!"))).Content.ReadFromJsonAsync<SendMessageResponse>();
        Assert.True(s1!.Queued);
        Assert.Equal(1, await f.RunQueueAsync());
        Assert.Equal(("5514988159045", "Olá, sou o Carlos!"), f.Evolution.Sent.Single());
        var msgs = await c.GetFromJsonAsync<List<MessageDto>>($"/api/conversations/{leadId}/messages");
        Assert.Contains(msgs!, m => m.Author == MessageAuthor.Atendente && m.SentBy == "Carlos");

        // eco do webhook da mensagem enviada pelo sistema não duplica nem é tratado como manual
        var extId = await f.WithDb(db => db.Messages.Where(m => m.Author == MessageAuthor.Atendente).Select(m => m.ExternalId!).SingleAsync());
        await PostWebhook(f, Upsert(extId, "5514988159045", "Olá, sou o Carlos!", fromMe: true));
        Assert.Equal(1, await f.WithDb(db => db.Messages.CountAsync(m => m.Text == "Olá, sou o Carlos!")));

        var back = await (await c.PostAsync($"/api/conversations/{leadId}/return-to-ai", null)).Content.ReadFromJsonAsync<OperationResult>();
        Assert.True(back!.Ok);
    }

    [Fact]
    public async Task Fila_respeita_consentimento_intervalo_e_retentativas()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        var ids = await f.WithDb(async db =>
        {
            var tpl = new MessageTemplate { Name = "t" };
            var v = new MessageTemplateVersion { TemplateId = tpl.Id, Version = 1, Body = "b", Approval = ApprovalStatus.Aprovado };
            tpl.Versions.Add(v);
            db.MessageTemplates.Add(tpl);
            var a = new Lead { Phone = "5514988159045", ConsentStatus = ConsentStatus.Valido };
            var b = new Lead { Phone = "5521999998888", ConsentStatus = ConsentStatus.Valido };
            var semConsent = new Lead { Phone = "5511987654321" };
            db.Leads.AddRange(a, b, semConsent);
            foreach (var l in new[] { a, b, semConsent })
                db.OutboundQueue.Add(new OutboundQueueItem { LeadId = l.Id, Kind = OutboundKind.AbordagemInicial, Text = "Olá", InstanceName = "dominius", TemplateVersionId = v.Id, NotBefore = f.Clock.GetUtcNow().AddSeconds(-1) });
            await db.SaveChangesAsync();
            return (a.Id, b.Id, semConsent.Id);
        });

        Assert.Equal(1, await f.RunQueueAsync()); // só 1 abordagem; a outra aguarda 5 min; sem consentimento é cancelada
        var queue = await f.WithDb(db => db.OutboundQueue.AsNoTracking().ToListAsync());
        Assert.Equal(MessageStatus.Cancelada, queue.Single(q => q.LeadId == ids.Item3).Status);
        var waiting = queue.Single(q => q.Status == MessageStatus.NaFila);
        Assert.Equal("Intervalo mínimo entre abordagens iniciais", waiting.LastError);

        f.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, await f.RunQueueAsync());
        f.Clock.Advance(TimeSpan.FromMinutes(3).Add(TimeSpan.FromSeconds(1)));
        // próxima tentativa falha com erro temporário → reagenda com backoff
        f.Evolution.Behavior = _ => new EvolutionSendResult(false, null, null, "HTTP 503", 503);
        Assert.Equal(0, await f.RunQueueAsync());
        var retry = await f.WithDb(db => db.OutboundQueue.AsNoTracking().SingleAsync(q => q.Id == waiting.Id));
        Assert.Equal((MessageStatus.NaFila, 1), (retry.Status, retry.Attempts));
        f.Evolution.Behavior = null;
        f.Clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, await f.RunQueueAsync());
        Assert.Equal(2, f.Evolution.Sent.Count);

        // erro permanente (400) não é retentado
        await f.WithDb(async db => { db.OutboundQueue.Add(new OutboundQueueItem { LeadId = ids.Item1, Kind = OutboundKind.Teste, Text = "t", InstanceName = "dominius" }); return await db.SaveChangesAsync(); });
        f.Evolution.Behavior = _ => new EvolutionSendResult(false, null, null, "HTTP 400: number not exists", 400);
        await f.RunQueueAsync();
        Assert.Equal(MessageStatus.Falhou, await f.WithDb(db => db.OutboundQueue.Where(q => q.Kind == OutboundKind.Teste).Select(q => q.Status).SingleAsync()));
    }

    [Fact]
    public async Task Status_de_entrega_e_descadastro_por_mensagem()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        await c.PostAsJsonAsync("/api/integrations/evolution/test-message", new TestMessageRequest("14 98815-9045", "teste"));
        await f.RunQueueAsync();
        var ext = await f.WithDb(db => db.Messages.Select(m => m.ExternalId!).SingleAsync());
        await PostWebhook(f, $$$"""{"event":"messages.update","instance":"dominius","data":{"keyId":"{{{ext}}}","remoteJid":"5514988159045@s.whatsapp.net","fromMe":true,"status":"DELIVERY_ACK"}}""");
        await PostWebhook(f, $$$"""{"event":"messages.update","instance":"dominius","data":{"keyId":"{{{ext}}}","remoteJid":"5514988159045@s.whatsapp.net","fromMe":true,"status":"SERVER_ACK"}}""");
        Assert.Equal(MessageStatus.Entregue, await f.WithDb(db => db.Messages.Select(m => m.Status).SingleAsync())); // não regride

        var zr = await PostWebhook(f, Upsert("Z9", "5514988159045", "por favor me tire da lista"));
        Assert.Contains("Stored", await zr.Content.ReadAsStringAsync());
        var lead = await f.WithDb(db => db.Leads.SingleAsync());
        Assert.Equal((ConsentStatus.Revogado, KanbanStage.NaoTemInteresse), (lead.ConsentStatus, lead.Stage));
        Assert.True(await f.WithDb(db => db.OptOuts.AnyAsync(o => o.Phone == "5514988159045")));
        var dash = await c.GetFromJsonAsync<DashboardDto>("/api/dashboard");
        Assert.Equal(1, dash!.ByStage["Não tem interesse"]);
    }

    [Fact]
    public async Task Chave_da_API_nunca_volta_ao_cliente_e_fica_criptografada()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        var raw = await c.GetStringAsync("/api/integrations/evolution");
        Assert.DoesNotContain("CHAVE", raw);
        Assert.Contains("\"hasApiKey\":true", raw);
        var stored = await f.WithDb(db => db.Settings.SingleAsync(s => s.Key == SettingKeys.EvolutionApiKey));
        Assert.True(stored.IsSecret); Assert.NotEqual("CHAVE", stored.Value);
    }

    [Fact]
    public async Task Recuperacao_apos_reinicializacao_mantem_fila()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dpcrm-restart-" + Guid.NewGuid().ToString("N"));
        Guid leadId;
        using (var f1 = new RestartFactory(dir))
        {
            var c = await f1.AdminAsync();
            await f1.ConfigureEvolutionAsync(c);
            leadId = await f1.WithDb(async db =>
            {
                var l = new Lead { Phone = "5514988159045" }; db.Leads.Add(l);
                db.OutboundQueue.Add(new OutboundQueueItem { LeadId = l.Id, Kind = OutboundKind.Teste, Text = "x", InstanceName = "dominius", Status = MessageStatus.Enviando });
                await db.SaveChangesAsync(); return l.Id;
            });
        }
        using (var f2 = new RestartFactory(dir, workers: true))
        {
            await f2.AdminAsync(); // sobe o host com workers → recuperação roda no início
            for (var i = 0; i < 50 && f2.Evolution.Sent.Count == 0; i++) await Task.Delay(100);
            Assert.Single(f2.Evolution.Sent);
        }
        try { Directory.Delete(dir, true); } catch { }
    }

    private sealed class RestartFactory(string dir, bool workers = false) : WebApplicationFactory<Program>
    {
        public readonly FakeEvolution Evolution = new();
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            Directory.CreateDirectory(dir);
            b.UseSetting("DataDirectory", dir);
            b.UseSetting("Workers:Enabled", workers ? "true" : "false");
            b.UseSetting("ConnectionStrings:Crm", $"Data Source={Path.Combine(dir, "crm.db")};Pooling=False");
            b.ConfigureTestServices(s => s.AddSingleton<IEvolutionApi>(Evolution));
        }
        public async Task<HttpClient> AdminAsync()
        {
            var c = CreateClient();
            var res = await c.PostAsJsonAsync("/api/auth/setup", new SetupRequest("carlos", "Carlos", "SenhaForte123"));
            if (res.StatusCode == HttpStatusCode.Conflict) res = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest("carlos", "SenhaForte123"));
            c.DefaultRequestHeaders.Authorization = new("Bearer", (await res.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
            return c;
        }
        public async Task ConfigureEvolutionAsync(HttpClient c) =>
            (await c.PutAsJsonAsync("/api/integrations/evolution", new EvolutionSettingsUpdate("https://evo.exemplo.com", "dominius", "CHAVE", null, null))).EnsureSuccessStatusCode();
        public async Task<T> WithDb<T>(Func<CrmDbContext, Task<T>> f)
        {
            using var scope = Services.CreateScope();
            return await f(scope.ServiceProvider.GetRequiredService<CrmDbContext>());
        }
    }
}
