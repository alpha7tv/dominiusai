using System.Net.Http.Json;
using System.Text;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Evolution;
using DominiusPlay.Infrastructure.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DominiusPlay.Tests;

/// <summary>Agente de Disparo IA (v0.4): agentes, campanha ampliada, acompanhamento em etapas, personalização com validação e pausa automática.</summary>
public class DispatchTests
{
    private const string Approved = "Olá{, nome}! Sou {assistente}, assistente virtual da Dominius Play. Você trabalha com revenda de IPTV? Se não quiser receber mensagens, responda SAIR.";

    [Theory]
    [InlineData("Oi! Aqui é a Lia, assistente virtual da Dominius Play. Você já revende IPTV hoje? Se preferir não receber mensagens, responda SAIR.", null)]
    [InlineData("Oi! Lia da Dominius Play. Planos a partir de R$ 50,00! Responda SAIR para sair.", "preço")]
    [InlineData("Oi! Lia da Dominius Play. Veja www.site.com.br e responda SAIR.", "link")]
    [InlineData("Oi! Aqui é a Lia, assistente virtual. Você revende IPTV? Responda SAIR se não quiser.", "identificação da empresa")]
    [InlineData("Oi! Lia da Dominius Play, assistente virtual. Você revende IPTV hoje?", "opção de saída")]
    [InlineData("Oi! Como combinamos, aqui é a Lia da Dominius Play. Responda SAIR se não quiser.", "relação prévia")]
    [InlineData("Oi {nome}! Lia da Dominius Play. Responda SAIR.", "variável")]
    public void Validador_de_abordagem(string generated, string? problem)
    {
        var r = ApproachValidator.Validate(generated, "Olá! Sou Lia, assistente virtual da Dominius Play. Você trabalha com revenda de IPTV? Se não quiser receber mensagens, responda SAIR.", "Dominius Play", "");
        if (problem is null) Assert.Null(r); else Assert.Contains(problem, r);
    }

    [Fact]
    public void Importacao_le_empresa_segmento_e_finalidade()
    {
        var csv = "telefone;nome;empresa;segmento;finalidade\n11987650099;Ana;TV Norte;Revendedor;Ofertas de revenda\n";
        var row = Assert.Single(LeadFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes(csv))));
        Assert.Equal("TV Norte", row.Company); Assert.Equal("Revendedor", row.Segment); Assert.Equal("Ofertas de revenda", row.ConsentPurpose);
    }

    // ------------------------------------------------------------ servidor

    private static async Task<LeadCard> NewLead(HttpClient c, string name, string phone)
    {
        var l = (await (await c.PostAsJsonAsync("/api/leads", new LeadUpsert(name, phone, null, null, "Formulário site", null, "revenda", IptvExperience.NaoInformado,
            null, null, PainCategory.Nenhuma, null, null, Priority.Normal, null))).Content.ReadFromJsonAsync<LeadCard>())!;
        (await c.PostAsJsonAsync($"/api/leads/{l.Id}/consent", new ConsentRequest("Formulário do site", "print", null))).EnsureSuccessStatusCode();
        return l;
    }

    private static async Task<Guid> ApprovedTemplate(HttpClient c, string body)
    {
        var r = await (await c.PostAsJsonAsync("/api/templates", new TemplateCreate("Abordagem " + Guid.NewGuid().ToString("N")[..4], "Abordagem inicial", body))).Content.ReadFromJsonAsync<OperationResult>();
        var vid = Guid.Parse(r!.Detail!);
        (await c.PostAsJsonAsync($"/api/templates/versions/{vid}/approve", new TemplateApprove(null))).EnsureSuccessStatusCode();
        return (await c.GetFromJsonAsync<List<TemplateDto>>("/api/templates"))!.First(t => t.VersionId == vid).Id;
    }

    /// <summary>Aprova as 3 versões do acompanhamento criado na primeira execução.</summary>
    private static async Task<Guid> ApprovedFollowUp(HttpClient c)
    {
        var fu = (await c.GetFromJsonAsync<List<TemplateDto>>("/api/templates"))!.Where(t => t.Purpose == "Acompanhamento").ToList();
        Assert.NotEmpty(fu);
        return fu[0].Id;
    }

    private static async Task ApproveAllVersions(CrmFactory f, HttpClient c, Guid templateId)
    {
        var ids = await f.WithDb(db => db.MessageTemplateVersions.Where(v => v.TemplateId == templateId).Select(v => v.Id).ToListAsync());
        foreach (var id in ids) (await c.PostAsJsonAsync($"/api/templates/versions/{id}/approve", new TemplateApprove(null))).EnsureSuccessStatusCode();
    }

    private static async Task<AgentDto> DispatchAgent(HttpClient c, AgentStatus status = AgentStatus.Ativo, string instance = "") =>
        (await (await c.PostAsJsonAsync("/api/agents", new AgentUpsert(AgentKind.Disparo, "Prospecção revenda", "Lia", "Abordagem de revendedores", "Abrir conversa",
            "Dominius Play", "Consultiva", "Curto e cordial", "", "", "", "", "", "", "", status, true, "00:00", "23:59", "0,1,2,3,4,5,6", "America/Sao_Paulo", "",
            instance, 40, 300))).Content.ReadFromJsonAsync<AgentDto>())!;

    private static CampaignUpsert Camp(Guid template, Guid? agent = null) =>
        new("Revenda", "00:00", "23:59", "0,1,2,3,4,5,6", 100, 300, 50, "Abrir conversa com revendedores", template, "revenda", agent);

    private static async Task Inbound(CrmFactory f, string phone, string id, string text)
    {
        var body = $$$"""{"event":"messages.upsert","instance":"dominius","data":{"key":{"remoteJid":"{{{phone}}}@s.whatsapp.net","fromMe":false,"id":"{{{id}}}"},"pushName":"X","message":{"conversation":"{{{text}}}"},"messageTimestamp":{{{f.Clock.GetUtcNow().ToUnixTimeSeconds()}}}}}""";
        var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/evolution") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("x-dominius-token", await f.WebhookTokenAsync());
        (await f.CreateClient().SendAsync(req)).EnsureSuccessStatusCode();
    }

    private static async Task<OperationResult> Act(HttpClient c, Guid id, string action) =>
        (await (await c.PostAsync($"/api/campaigns/{id}/{action}", null)).Content.ReadFromJsonAsync<OperationResult>())!;

    [Fact]
    public async Task Acompanhamento_em_etapas_para_quando_o_contato_responde()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        await NewLead(c, "Ana", "11987650010");
        await NewLead(c, "Bia", "11987650011");
        var tid = await ApprovedTemplate(c, Approved);
        var fu = await ApprovedFollowUp(c);

        // acompanhamento sem versões aprovadas: validação acusa
        var camp = (await (await c.PostAsJsonAsync("/api/campaigns", Camp(tid) with { FollowUpTemplateId = fu, FollowUpDays = "1,3,7" })).Content.ReadFromJsonAsync<CampaignDto>())!;
        var v = (await (await c.PostAsync($"/api/campaigns/{camp.Id}/validate", null)).Content.ReadFromJsonAsync<CampaignValidation>())!;
        Assert.False(v.Ok);
        Assert.Contains(v.Problems, p => p.Contains("acompanhamento"));
        await ApproveAllVersions(f, c, fu);
        v = (await (await c.PostAsync($"/api/campaigns/{camp.Id}/validate", null)).Content.ReadFromJsonAsync<CampaignValidation>())!;
        Assert.True(v.Ok, string.Join("; ", v.Problems));
        Assert.Equal(2, v.Eligible);

        var st = await Act(c, camp.Id, "start");
        Assert.True(st.Ok, st.Message);
        Assert.Equal(6, await f.WithDb(db => db.OutboundQueue.CountAsync(q => q.Kind == OutboundKind.Acompanhamento)));

        Assert.Equal(1, await f.RunQueueAsync()); // Ana
        f.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, await f.RunQueueAsync()); // Bia
        // máquina de estados: depois da abordagem quem tem a palavra é o Agente de Disparo
        Assert.All(await f.WithDb(db => db.Leads.ToListAsync()), l => Assert.Equal(ConversationMode.Prospeccao, l.Mode));
        // Ana responde: as etapas dela param e o Agente de Atendimento assume
        await Inbound(f, "5511987650010", "R1", "Oi, quem é?");
        Assert.Equal(ConversationMode.Ia, await f.WithDb(db => db.Leads.Where(l => l.Phone.EndsWith("87650010")).Select(l => l.Mode).SingleAsync()));
        Assert.Equal(3, await f.WithDb(db => db.OutboundQueue.CountAsync(q => q.Kind == OutboundKind.Acompanhamento && q.Status == MessageStatus.Cancelada)));

        f.Clock.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(10)));
        Assert.Equal(1, await f.RunQueueAsync()); // etapa 1 só para a Bia
        Assert.Contains("Passando só para saber", f.Evolution.Sent.Last().Text);
        Assert.Equal("5511987650011", f.Evolution.Sent.Last().Number);

        // Bia responde depois da etapa 1: etapas 2 e 3 bloqueadas
        await Inbound(f, "5511987650011", "R2", "tenho interesse");
        f.Clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(0, await f.RunQueueAsync());
        Assert.Equal(3, f.Evolution.Sent.Count);
        var dto = (await c.GetFromJsonAsync<List<CampaignDto>>("/api/campaigns"))!.Single(x => x.Id == camp.Id);
        Assert.Equal(1, dto.FollowUpsSent);
        Assert.Equal(2, dto.Replied);
        Assert.Equal(CampaignStatus.Concluida, dto.Status);
        // origem do contato registrada para o CRM e para o agente de atendimento
        Assert.All(await f.WithDb(db => db.Leads.ToListAsync()), l => Assert.Equal(camp.Id, l.OriginCampaignId));
    }

    [Fact]
    public async Task Disparo_passa_a_conversa_para_o_agente_de_atendimento_vinculado()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        (await c.PutAsJsonAsync("/api/ai/settings", new AiSettingsUpdate(true, "claude-haiku-5-5", "claude-sonnet-5-5", "sk-ant-teste-123", 4000, "Lia", "", 100000, false, 0))).EnsureSuccessStatusCode();
        var att = (await (await c.PostAsJsonAsync("/api/agents", new AgentUpsert(AgentKind.Atendimento, "Fechamento revenda", "Bruno", "", "Qualificar e fechar",
            "Dominius Play", "Consultivo", "Direto", "", "", "", "", "", "claude-sonnet-5-5", "", AgentStatus.Ativo, false, "00:00", "23:59", "0,1,2,3,4,5,6", "America/Sao_Paulo", ""))).Content.ReadFromJsonAsync<AgentDto>())!;
        var disp = (await (await c.PostAsJsonAsync("/api/agents", new AgentUpsert(AgentKind.Disparo, "Prospecção revenda", "Lia", "", "Abrir conversa",
            "Dominius Play", "", "", "", "", "", "", "", "", "", AgentStatus.Ativo, true, "00:00", "23:59", "0,1,2,3,4,5,6", "America/Sao_Paulo", "",
            "", 40, 300, att.Id))).Content.ReadFromJsonAsync<AgentDto>())!;
        Assert.Equal(att.Id, disp.AttendanceAgentId);
        await NewLead(c, "Ana", "11987650020");
        var tid = await ApprovedTemplate(c, Approved);
        var camp = (await (await c.PostAsJsonAsync("/api/campaigns", Camp(tid, disp.Id) with { Name = "Revenda Sul" })).Content.ReadFromJsonAsync<CampaignDto>())!;
        Assert.True((await Act(c, camp.Id, "start")).Ok);
        Assert.Equal(1, await f.RunQueueAsync());

        // enquanto o contato não responde, o Atendimento não fala (só o Disparo tem a palavra)
        var leadId = await f.WithDb(db => db.Leads.Select(l => l.Id).SingleAsync());
        Assert.Equal(KanbanStage.AbordagemRealizada, await f.WithDb(db => db.Leads.Select(l => l.Stage).SingleAsync()));
        using (var scope = f.Services.CreateScope())
            Assert.Equal(DominiusPlay.Server.Services.AiReplyOutcome.Skipped,
                await scope.ServiceProvider.GetRequiredService<DominiusPlay.Server.Services.AiResponder>().RespondAsync(leadId));

        await Inbound(f, "5511987650020", "R1", "Oi, como funciona?");
        Assert.Equal(KanbanStage.Respondeu, await f.WithDb(db => db.Leads.Select(l => l.Stage).SingleAsync()));
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Oi, Ana! Aqui é o Bruno. Você já revende hoje?"));
        using (var scope = f.Services.CreateScope())
            Assert.Equal(DominiusPlay.Server.Services.AiReplyOutcome.Replied,
                await scope.ServiceProvider.GetRequiredService<DominiusPlay.Server.Services.AiResponder>().RespondAsync(leadId));
        var req = f.Ai.Requests.Last();
        Assert.Equal("claude-sonnet-5-5", req.Model); // modelo do agente de atendimento vinculado
        Assert.Contains("ORIGEM DO CONTATO", req.System);
        Assert.Contains("Revenda Sul", req.System);
        Assert.Contains("Bruno", req.System);
        Assert.Equal(ConversationMode.Ia, await f.WithDb(db => db.Leads.Select(l => l.Mode).SingleAsync()));
        Assert.Equal(KanbanStage.EmAtendimento, await f.WithDb(db => db.Leads.Select(l => l.Stage).SingleAsync()));
        // movimentações automáticas auditadas, com a campanha e o agente no card
        Assert.Equal(3, await f.WithDb(db => db.StageChanges.CountAsync(sc => sc.ChangedBy == "Sistema")));
        var card = (await c.GetFromJsonAsync<List<KanbanColumn>>("/api/kanban"))!.Single(k => k.Stage == KanbanStage.EmAtendimento).Cards.Single();
        Assert.Equal("Revenda Sul", card.CampaignName);
        Assert.Equal("Fechamento revenda", card.AgentName);

        // painel filtrado por campanha e por agente
        var dash = (await c.GetFromJsonAsync<AgentsDashboardDto>($"/api/dashboard/agents?campaignId={camp.Id}"))!;
        Assert.Equal(1, dash.Sent); // só o que saiu: a abordagem (a resposta da IA ainda está na fila)
        Assert.Equal(1, dash.Replies);
        var row = Assert.Single(dash.ByCampaign);
        Assert.Equal(1, row.Sent); Assert.Equal(1, row.Replied); Assert.Equal(100, row.ResponseRate);
        var byAgent = (await c.GetFromJsonAsync<AgentsDashboardDto>($"/api/dashboard/agents?agentId={att.Id}"))!;
        Assert.Equal(1, byAgent.Replies);
        var other = (await c.GetFromJsonAsync<AgentsDashboardDto>($"/api/dashboard/agents?agentId={Guid.NewGuid()}"))!;
        Assert.Equal(0, other.Replies);
        Assert.True(dash.AiTokens > 0);
    }

    [Fact]
    public async Task Agente_de_disparo_define_instancia_e_limites_e_pausado_nao_inicia()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        await NewLead(c, "Ana", "11987650010");
        var tid = await ApprovedTemplate(c, Approved);
        var paused = await DispatchAgent(c, AgentStatus.Pausado);
        var camp = (await (await c.PostAsJsonAsync("/api/campaigns", Camp(tid, paused.Id))).Content.ReadFromJsonAsync<CampaignDto>())!;
        var st = await Act(c, camp.Id, "start");
        Assert.False(st.Ok);
        Assert.Contains("pausado", st.Message);

        var agent = await DispatchAgent(c, AgentStatus.Ativo, "prospeccao2");
        (await c.PutAsJsonAsync($"/api/campaigns/{camp.Id}", Camp(tid, agent.Id))).EnsureSuccessStatusCode();
        Assert.True((await Act(c, camp.Id, "start")).Ok);
        Assert.Equal("prospeccao2", await f.WithDb(db => db.OutboundQueue.Select(q => q.InstanceName).SingleAsync()));
        Assert.Equal(1, await f.RunQueueAsync());
        Assert.Equal("prospeccao2", await f.WithDb(db => db.Messages.Select(m => m.InstanceName).SingleAsync()));
    }

    [Fact]
    public async Task Personalizacao_por_IA_recusa_preco_inventado_e_usa_texto_valido()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        (await c.PutAsJsonAsync("/api/ai/settings", new AiSettingsUpdate(true, "claude-haiku-5-5", "claude-sonnet-5-5", "sk-ant-teste-123", 4000, "Lia", "", 100000, false, 0))).EnsureSuccessStatusCode();
        await NewLead(c, "Ana", "11987650010");
        await NewLead(c, "", "11987650011");
        var tid = await ApprovedTemplate(c, Approved);
        var agent = await DispatchAgent(c);
        var camp = (await (await c.PostAsJsonAsync("/api/campaigns", Camp(tid, agent.Id) with { AiPersonalization = true, Prompt = "Fale de estabilidade" })).Content.ReadFromJsonAsync<CampaignDto>())!;
        Assert.True((await Act(c, camp.Id, "start")).Ok);

        const string good = "Oi, Ana! Aqui é a Lia, assistente virtual da Dominius Play. Você já trabalha com revenda de IPTV? Se não quiser receber mensagens, responda SAIR.";
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Oi Ana! Lia da Dominius Play: planos a partir de R$ 50,00. Responda SAIR."));
        f.Ai.Script.Enqueue(_ => FakeAi.Text(good));
        Assert.Equal(1, await f.RunQueueAsync());
        Assert.Equal(good, f.Evolution.Sent[0].Text);
        Assert.Contains("Instruções da campanha: Fale de estabilidade", f.Ai.Requests[0].System);
        Assert.Contains("A versão anterior foi recusada: preço", ((Infrastructure.Ai.AiUserText)f.Ai.Requests[1].Turns[0]).Text);
        Assert.Contains("Personalizada pela IA", await f.WithDb(db => db.OutboundQueue.Where(q => q.Status == MessageStatus.EnviadaServidor).Select(q => q.PersonalizationNote).SingleAsync()));

        // duas recusas: vai o modelo aprovado
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Visite www.x.com"));
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Visite www.x.com"));
        f.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, await f.RunQueueAsync());
        Assert.StartsWith("Olá! Sou Lia, assistente virtual da Dominius Play.", f.Evolution.Sent[1].Text);

        // modo oficial (Cloud API): personalização não pode ser ligada
        await c.PutAsJsonAsync("/api/integrations/evolution", new EvolutionSettingsUpdate("https://evo.exemplo.com", "dominius", null, "WHATSAPP-BUSINESS", "https://crm.exemplo.com"));
        var camp2 = (await (await c.PostAsJsonAsync("/api/campaigns", Camp(tid) with { AiPersonalization = true })).Content.ReadFromJsonAsync<CampaignDto>())!;
        var v = (await (await c.PostAsync($"/api/campaigns/{camp2.Id}/validate", null)).Content.ReadFromJsonAsync<CampaignValidation>())!;
        Assert.Contains(v.Problems, p => p.Contains("Cloud API oficial"));
    }

    [Fact]
    public async Task Pre_visualizacao_nao_salva_nem_envia()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        await NewLead(c, "Ana", "11987650010");
        var tid = await ApprovedTemplate(c, Approved);
        var camp = (await (await c.PostAsJsonAsync("/api/campaigns", Camp(tid))).Content.ReadFromJsonAsync<CampaignDto>())!;
        var prev = (await (await c.PostAsync($"/api/campaigns/{camp.Id}/preview", null)).Content.ReadFromJsonAsync<List<ApproachPreview>>())!;
        Assert.StartsWith("Olá, Ana!", Assert.Single(prev).Text);
        Assert.Equal(0, await f.WithDb(db => db.OutboundQueue.CountAsync()));
        Assert.Empty(f.Evolution.Sent);
    }

    [Fact]
    public async Task Pausa_automatica_por_taxa_de_falhas()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        for (var i = 0; i < 12; i++) await NewLead(c, "Lead " + i, "119876501" + i.ToString("00"));
        var tid = await ApprovedTemplate(c, Approved);
        var camp = (await (await c.PostAsJsonAsync("/api/campaigns", Camp(tid) with { MaxFailurePercent = 30 })).Content.ReadFromJsonAsync<CampaignDto>())!;
        Assert.True((await Act(c, camp.Id, "start")).Ok);
        f.Evolution.Behavior = n => new EvolutionSendResult(false, null, null, "número inexistente", 400);
        for (var i = 0; i < 12; i++) { await f.RunQueueAsync(); f.Clock.Advance(TimeSpan.FromMinutes(6)); }
        var dto = (await c.GetFromJsonAsync<List<CampaignDto>>("/api/campaigns"))!.Single(x => x.Id == camp.Id);
        Assert.Equal(CampaignStatus.Pausada, dto.Status);
        Assert.Contains("Pausa automática", dto.PauseReason);
        Assert.Equal(10, dto.Failed); // parou ao atingir o mínimo de 10 envios
        Assert.True((await Act(c, camp.Id, "resume")).Ok);
        Assert.Null((await c.GetFromJsonAsync<List<CampaignDto>>("/api/campaigns"))!.Single(x => x.Id == camp.Id).PauseReason);
    }
}
