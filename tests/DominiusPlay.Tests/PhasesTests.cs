using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Tests;

/// <summary>Fases 6 a 9: qualificação, equipe, modelos, campanhas, relatórios, LGPD e manutenção.</summary>
public class PhasesTests
{
    [Fact]
    public async Task Configuracao_instalada_grava_banco_no_diretorio_de_dados_e_mantem_os_dados()
    {
        // appsettings.json do instalador: ConnectionStrings:Crm vazio
        string dir;
        using (var f = new CrmFactory(shippedConnection: true))
        {
            dir = f.DataDir;
            await f.AdminAsync(); // cria o administrador
            Assert.True(File.Exists(Path.Combine(dir, "crm.db")));
            Assert.Equal(1, await f.WithDb(db => db.Users.CountAsync()));
        }
    }

    [Fact]
    public async Task Atualizador_do_banco_nao_recria_tabelas_existentes()
    {
        using var f = new CrmFactory();
        await f.AdminAsync();
        // rodar de novo num banco completo não muda nada e não falha
        var changes = await f.WithDb(db => DominiusPlay.Infrastructure.Data.SchemaUpgrader.UpgradeSqliteAsync(db));
        Assert.Empty(changes);
        // tabela que referencia outra (ConsentRecords → Leads) ausente: é criada sozinha, sem tocar nas demais
        await f.WithDb(db => db.Database.ExecuteSqlRawAsync("DROP TABLE \"ConsentRecords\""));
        changes = await f.WithDb(db => DominiusPlay.Infrastructure.Data.SchemaUpgrader.UpgradeSqliteAsync(db));
        Assert.Equal(["tabela ConsentRecords"], changes);
    }

    private static async Task<LeadCard> NewLead(HttpClient c, string name, string phone, string? tags = null, bool consent = true)
    {
        var res = await c.PostAsJsonAsync("/api/leads", new LeadUpsert(name, phone, null, null, "Formulário site", null, tags, IptvExperience.NaoInformado,
            null, null, PainCategory.Nenhuma, null, null, Priority.Normal, null));
        res.EnsureSuccessStatusCode();
        var l = (await res.Content.ReadFromJsonAsync<LeadCard>())!;
        if (consent) (await c.PostAsJsonAsync($"/api/leads/{l.Id}/consent", new ConsentRequest("Formulário do site", "print", null))).EnsureSuccessStatusCode();
        return l;
    }

    private static async Task<HttpClient> LoginAs(CrmFactory f, string user, string pass)
    {
        var c = f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(user, pass));
        res.EnsureSuccessStatusCode();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await res.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
        return c;
    }

    private static async Task<Guid> ApprovedTemplate(HttpClient c, string name, string body)
    {
        var r = await (await c.PostAsJsonAsync("/api/templates", new TemplateCreate(name, "Abordagem inicial", body))).Content.ReadFromJsonAsync<OperationResult>();
        Assert.True(r!.Ok, r.Message);
        var vid = Guid.Parse(r.Detail!);
        (await c.PostAsJsonAsync($"/api/templates/versions/{vid}/approve", new TemplateApprove(null))).EnsureSuccessStatusCode();
        return (await c.GetFromJsonAsync<List<TemplateDto>>("/api/templates"))!.First(t => t.VersionId == vid).Id;
    }

    private static CampaignUpsert AllDay(string name, Guid? template, string tags = "", int max = 100) =>
        new(name, "00:00", "23:59", "0,1,2,3,4,5,6", max, 300, 50, "Teste", template, tags);

    [Fact]
    public async Task Fase6_qualificacao_fatos_e_sugestao_da_ia()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var l = await NewLead(c, "Ana", "11987650001");

        var q = new QualificationUpdate(IptvExperience.JaTrabalha, "Sigma", 120, PainCategory.QuedasServidor, "APONTAMENTO_SIGMA_600", "até R$300", Priority.Alta, "Enviar proposta", "Revende há 2 anos");
        Assert.True((await (await c.PutAsJsonAsync($"/api/leads/{l.Id}/qualification", q)).Content.ReadFromJsonAsync<OperationResult>())!.Ok);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/leads/{l.Id}/qualification", q with { InterestPlanCode = "INVENTADO" })).StatusCode);
        (await c.PostAsJsonAsync($"/api/leads/{l.Id}/facts", new FactCreate("objecao", "Acha caro"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/leads/{l.Id}/facts", new FactCreate("", "x"))).StatusCode);

        var d = (await c.GetFromJsonAsync<LeadDetail>($"/api/leads/{l.Id}"))!;
        Assert.Equal(120, d.ActiveClients);
        Assert.Equal("Revende há 2 anos", d.ConversationSummary);
        Assert.Contains(d.Facts, x => x.Key == "objecao" && x.Source == FactSource.RegistradoPorAtendente);

        // IA sugeriu "Em negociação" → aparece no card; atendente aprova
        await f.WithDb(async db => { var x = await db.Leads.FindAsync(l.Id); x!.SuggestedStage = KanbanStage.EmNegociacao; x.SuggestedStageReason = "Pediu proposta"; return await db.SaveChangesAsync(); });
        var card = (await c.GetFromJsonAsync<List<KanbanColumn>>("/api/kanban"))!.SelectMany(k => k.Cards).First(x => x.Id == l.Id);
        Assert.Equal(KanbanStage.EmNegociacao, card.SuggestedStage);
        var acc = (await (await c.PostAsync($"/api/leads/{l.Id}/suggestion/accept", null)).Content.ReadFromJsonAsync<MoveCardResponse>())!;
        Assert.True(acc.Applied);
        d = (await c.GetFromJsonAsync<LeadDetail>($"/api/leads/{l.Id}"))!;
        Assert.Equal(KanbanStage.EmNegociacao, d.Stage);
        Assert.Contains("sugestão da IA aprovada", d.StageHistory[0].ChangedBy);

        // Sugestão de "Negócio fechado" exige confirmação comercial explícita
        await f.WithDb(async db => { var x = await db.Leads.FindAsync(l.Id); x!.SuggestedStage = KanbanStage.NegocioFechado; return await db.SaveChangesAsync(); });
        acc = (await (await c.PostAsync($"/api/leads/{l.Id}/suggestion/accept", null)).Content.ReadFromJsonAsync<MoveCardResponse>())!;
        Assert.False(acc.Applied);
        Assert.Equal(KanbanStage.EmNegociacao, (await c.GetFromJsonAsync<LeadDetail>($"/api/leads/{l.Id}"))!.Stage);
        Assert.True((await (await c.PostAsync($"/api/leads/{l.Id}/suggestion/reject", null)).Content.ReadFromJsonAsync<OperationResult>())!.Ok);
        Assert.Null(await f.WithDb(async db => (await db.Leads.FindAsync(l.Id))!.SuggestedStage));
    }

    [Fact]
    public async Task Fase7_equipe_perfis_atribuicao_e_senha()
    {
        using var f = new CrmFactory();
        var admin = await f.AdminAsync();
        var created = await admin.PostAsJsonAsync("/api/users", new UserCreate("maria", "Maria", "SenhaMaria1", UserRole.Atendente));
        created.EnsureSuccessStatusCode();
        var maria = (await created.Content.ReadFromJsonAsync<UserDto>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/users", new UserCreate("Maria", "Outra", "SenhaMaria1", UserRole.Atendente))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/users", new UserCreate("joao", "João", "curta", UserRole.Atendente))).StatusCode);

        var m = await LoginAs(f, "maria", "SenhaMaria1");
        Assert.Equal(HttpStatusCode.Forbidden, (await m.PostAsJsonAsync("/api/users", new UserCreate("x1x", "X", "SenhaForte9", UserRole.Administrador))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await m.GetAsync("/api/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await m.GetAsync("/api/export/leads.csv")).StatusCode);

        // Administrador não pode se rebaixar nem desativar o último administrador
        var me = (await admin.GetFromJsonAsync<UserDto>("/api/me"))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/users/{me.Id}", new UserUpdate("Carlos", UserRole.Supervisor, true, null))).StatusCode);

        // Atribuição de conversa
        var l = await NewLead(admin, "Pedro", "11987650002");
        Assert.True((await (await admin.PostAsJsonAsync($"/api/conversations/{l.Id}/assign", new AssignRequest(maria.Id))).Content.ReadFromJsonAsync<OperationResult>())!.Ok);
        Assert.Equal("Maria", (await admin.GetFromJsonAsync<LeadDetail>($"/api/leads/{l.Id}"))!.AssignedTo);
        Assert.Equal(HttpStatusCode.Forbidden, (await m.PostAsJsonAsync($"/api/conversations/{l.Id}/assign", new AssignRequest(null))).StatusCode);

        // Troca de senha
        Assert.Equal(HttpStatusCode.BadRequest, (await m.PostAsJsonAsync("/api/me/password", new ChangePasswordRequest("errada", "NovaSenha123"))).StatusCode);
        (await m.PostAsJsonAsync("/api/me/password", new ChangePasswordRequest("SenhaMaria1", "NovaSenha123"))).EnsureSuccessStatusCode();
        await LoginAs(f, "maria", "NovaSenha123");

        // Desativar usuário: login bloqueado e conversas ficam sem responsável
        var upd = (await (await admin.PutAsJsonAsync($"/api/users/{maria.Id}", new UserUpdate("Maria", UserRole.Atendente, false, null))).Content.ReadFromJsonAsync<OperationResult>())!;
        Assert.Contains("1 conversa", upd.Message);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("maria", "NovaSenha123"))).StatusCode);
        Assert.Null((await admin.GetFromJsonAsync<LeadDetail>($"/api/leads/{l.Id}"))!.AssignedTo);
        Assert.Equal(2, (await admin.GetFromJsonAsync<List<UserDto>>("/api/users"))!.Count);
    }

    [Fact]
    public async Task Fase8_modelos_validacao_previa_similaridade_e_aprovacao()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var bad = await c.PostAsJsonAsync("/api/templates", new TemplateCreate("Erro", "Abordagem inicial", "Olá {nme}, sou {assistente}"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("{nme}", (await bad.Content.ReadFromJsonAsync<OperationResult>())!.Message);
        var noId = await c.PostAsJsonAsync("/api/templates", new TemplateCreate("Sem IA", "Abordagem inicial", "Olá {nome}, temos planos para revenda."));
        Assert.Equal(HttpStatusCode.BadRequest, noId.StatusCode); // precisa se identificar como assistente virtual

        var body = "Olá{, nome}! Sou {assistente}, assistente virtual da Dominius Play. Posso te mostrar nossas opções para revenda?";
        var prev = (await (await c.PostAsJsonAsync("/api/templates/preview", new TemplatePreviewRequest(body, null))).Content.ReadFromJsonAsync<TemplatePreview>())!;
        Assert.StartsWith("Olá, Carlos! Sou Assistente Dominius Play, assistente virtual", prev.Text);

        var r = (await (await c.PostAsJsonAsync("/api/templates", new TemplateCreate("Revenda A", "Abordagem inicial", body))).Content.ReadFromJsonAsync<OperationResult>())!;
        Assert.True(r.Ok);
        var near = body.Replace("Posso te mostrar", "Posso mostrar");
        prev = (await (await c.PostAsJsonAsync("/api/templates/preview", new TemplatePreviewRequest(near, null))).Content.ReadFromJsonAsync<TemplatePreview>())!;
        Assert.Contains("Revenda A", prev.SimilarTo); // alerta de mensagem quase idêntica

        var list = (await c.GetFromJsonAsync<List<TemplateDto>>("/api/templates"))!;
        var mine = list.Single(t => t.Name == "Revenda A");
        Assert.Equal(ApprovalStatus.EmRevisao, mine.Approval);
        var v2 = (await (await c.PostAsJsonAsync($"/api/templates/{mine.Id}/versions", new TemplateVersionCreate(body + " Responda SAIR para não receber mais."))).Content.ReadFromJsonAsync<OperationResult>())!;
        Assert.Contains("Versão 2", v2.Message);
        (await c.PostAsJsonAsync($"/api/templates/versions/{v2.Detail}/approve", new TemplateApprove("dominius_revenda_v2"))).EnsureSuccessStatusCode();
        list = (await c.GetFromJsonAsync<List<TemplateDto>>("/api/templates"))!;
        Assert.Equal("dominius_revenda_v2", list.Single(t => t.VersionId == Guid.Parse(v2.Detail!)).ChannelTemplateName);
        Assert.Equal(ApprovalStatus.Aprovado, list.Single(t => t.VersionId == Guid.Parse(v2.Detail!)).Approval);
    }

    [Fact]
    public async Task Fase8_campanha_filtra_elegiveis_respeita_intervalo_pausa_e_cancelamento()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        var a = await NewLead(c, "joão silva", "11987650010", "revenda");
        var b = await NewLead(c, "", "11987650011", "revenda");
        await NewLead(c, "Sem tag", "11987650012", "outros");
        await NewLead(c, "Sem consentimento", "11987650013", "revenda", consent: false);
        var blocked = await NewLead(c, "Bloqueado", "11987650014", "revenda");
        (await c.PostAsync($"/api/leads/{blocked.Id}/optout", null)).EnsureSuccessStatusCode();

        // Sem modelo aprovado não inicia
        var draft = (await (await c.PostAsJsonAsync("/api/campaigns", AllDay("Sem modelo", null, "revenda"))).Content.ReadFromJsonAsync<CampaignDto>())!;
        Assert.False((await (await c.PostAsync($"/api/campaigns/{draft.Id}/start", null)).Content.ReadFromJsonAsync<OperationResult>())!.Ok);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/campaigns", AllDay("Horário", null) with { StartTime = "18:00", EndTime = "09:00" })).StatusCode);

        var tid = await ApprovedTemplate(c, "Abordagem", "Olá{, nome}! Sou {assistente}, assistente virtual da Dominius Play. Podemos conversar sobre revenda?");
        var camp = (await (await c.PostAsJsonAsync("/api/campaigns", AllDay("Revenda novembro", tid, "revenda") with { MinIntervalSeconds = 10 })).Content.ReadFromJsonAsync<CampaignDto>())!;
        Assert.Equal(300, camp.MinIntervalSeconds); // nunca abaixo de 5 minutos
        Assert.Equal(2, (await c.GetFromJsonAsync<List<CampaignDto>>("/api/campaigns"))!.Single(x => x.Id == camp.Id).Eligible);

        var st = (await (await c.PostAsync($"/api/campaigns/{camp.Id}/start", null)).Content.ReadFromJsonAsync<OperationResult>())!;
        Assert.True(st.Ok, st.Message);
        Assert.False((await (await c.PostAsync($"/api/campaigns/{camp.Id}/start", null)).Content.ReadFromJsonAsync<OperationResult>())!.Ok); // não duplica

        Assert.Equal(1, await f.RunQueueAsync()); // só a primeira; a segunda aguarda 5 minutos
        Assert.Single(f.Evolution.Sent);
        Assert.StartsWith("Olá, João! Sou Assistente Dominius Play", f.Evolution.Sent[0].Text);
        f.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, await f.RunQueueAsync());

        // Pausa: nada sai mesmo depois do intervalo
        Assert.True((await (await c.PostAsync($"/api/campaigns/{camp.Id}/pause", null)).Content.ReadFromJsonAsync<OperationResult>())!.Ok);
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, await f.RunQueueAsync());
        Assert.True((await (await c.PostAsync($"/api/campaigns/{camp.Id}/resume", null)).Content.ReadFromJsonAsync<OperationResult>())!.Ok);
        f.Clock.Advance(TimeSpan.FromMinutes(6)); // adiamento da pausa (5 min) vence
        Assert.Equal(1, await f.RunQueueAsync());
        Assert.StartsWith("Olá! Sou Assistente", f.Evolution.Sent[1].Text); // sem nome: não personaliza

        var dto = (await c.GetFromJsonAsync<List<CampaignDto>>("/api/campaigns"))!.Single(x => x.Id == camp.Id);
        Assert.Equal(2, dto.Sent);
        Assert.Equal(CampaignStatus.Concluida, dto.Status);
        Assert.Equal(2, (await c.GetFromJsonAsync<List<TemplateDto>>("/api/templates"))!.Single(t => t.Id == tid).TimesUsed);

        // Cancelamento: itens na fila são cancelados e nunca enviados
        var c2 = (await (await c.PostAsJsonAsync("/api/campaigns", AllDay("Outros", tid, ""))).Content.ReadFromJsonAsync<CampaignDto>())!;
        Assert.True((await (await c.PostAsync($"/api/campaigns/{c2.Id}/start", null)).Content.ReadFromJsonAsync<OperationResult>())!.Ok);
        var cancel = (await (await c.PostAsync($"/api/campaigns/{c2.Id}/cancel", null)).Content.ReadFromJsonAsync<OperationResult>())!;
        Assert.Contains("1 envio", cancel.Message); // só o lead "Sem tag" ainda era elegível
        f.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await f.RunQueueAsync());
        Assert.Equal(2, f.Evolution.Sent.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/campaigns/{c2.Id}", AllDay("x", tid))).StatusCode);
    }

    [Fact]
    public async Task Fase9_relatorios_exportacoes_lgpd_backup_e_limpeza()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var l = await NewLead(c, "=HYPERLINK(\"x\")", "11987650020", "revenda");
        await NewLead(c, "Bia", "11987650021");
        (await c.PostAsJsonAsync($"/api/kanban/{l.Id}/move", new MoveCardRequest(KanbanStage.NaoTemInteresse, "Sem orçamento", false))).EnsureSuccessStatusCode();

        var rep = (await c.GetFromJsonAsync<ReportDto>("/api/reports"))!;
        Assert.Equal(2, rep.NewLeads);
        Assert.Equal(1, rep.Lost);
        Assert.Contains(rep.LossReasons, r => r.Label == "Sem orçamento" && r.Value == 1);
        Assert.Contains(rep.Sources, r => r.Label == "Formulário site" && r.Value == 2);

        var csv = await c.GetByteArrayAsync("/api/export/leads.csv");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, csv[..3]); // BOM para o Excel abrir com acentos
        var text = System.Text.Encoding.UTF8.GetString(csv);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", text); // fórmula neutralizada
        Assert.Contains("5511987650021", text);
        Assert.Contains("Data/hora", System.Text.Encoding.UTF8.GetString(await c.GetByteArrayAsync("/api/export/messages.csv")));

        var json = JsonDocument.Parse(await c.GetStringAsync($"/api/leads/{l.Id}/export")).RootElement;
        Assert.Equal("5511987650020", json.GetProperty("titular").GetProperty("WhatsApp").GetString());
        Assert.Equal(1, json.GetProperty("consentimento").GetProperty("historico").GetArrayLength());

        var bk = (await (await c.PostAsync("/api/maintenance/backup", null)).Content.ReadFromJsonAsync<OperationResult>())!;
        Assert.True(bk.Ok, bk.Message);
        Assert.True(new FileInfo(bk.Detail!).Length > 0);
        await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={bk.Detail};Mode=ReadOnly;Pooling=false"))
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand(); cmd.CommandText = "select count(*) from Leads";
            Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync())!);
        }
        Assert.Single((await c.GetFromJsonAsync<List<ReportRow>>("/api/maintenance/backups"))!);

        await f.WithDb(async db =>
        {
            db.WebhookEvents.Add(new() { EventKey = "old", EventType = "x", InstanceName = "i", Processed = true, CreatedAt = DateTimeOffset.UtcNow.AddDays(-200) });
            db.WebhookEvents.Add(new() { EventKey = "new", EventType = "x", InstanceName = "i", Processed = true });
            return await db.SaveChangesAsync();
        });
        var purge = (await (await c.PostAsync("/api/maintenance/purge?days=90", null)).Content.ReadFromJsonAsync<OperationResult>())!;
        Assert.Contains("1 evento", purge.Message);
        Assert.Equal(1, await f.WithDb(db => db.WebhookEvents.CountAsync()));
    }

    [Fact]
    public async Task Banco_da_versao_anterior_e_atualizado_sem_perder_dados()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var l = await NewLead(c, "Antigo", "11987650030");
        // simula banco da v0.1: sem colunas novas e sem a tabela de campanhas
        await f.WithDb(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Leads DROP COLUMN SuggestedStage");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Leads DROP COLUMN SuggestedStageReason");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE Campaigns");
            return 0;
        });
        var changes = await f.WithDb(db => DominiusPlay.Infrastructure.Data.SchemaUpgrader.UpgradeSqliteAsync(db));
        Assert.Contains("Leads.SuggestedStage", changes);
        Assert.Contains("tabela Campaigns", changes);
        Assert.Empty(await f.WithDb(db => DominiusPlay.Infrastructure.Data.SchemaUpgrader.UpgradeSqliteAsync(db))); // idempotente
        Assert.Equal("Antigo", (await c.GetFromJsonAsync<LeadDetail>($"/api/leads/{l.Id}"))!.Name);
        Assert.Empty((await c.GetFromJsonAsync<List<CampaignDto>>("/api/campaigns"))!);
        (await c.PostAsJsonAsync("/api/campaigns", AllDay("Depois da atualização", null))).EnsureSuccessStatusCode();
    }

    [Fact]
    public void Personalizacao_de_modelo()
    {
        Assert.Equal("Olá, Ana! Sou Bia.", TemplateRenderer.Render("Olá{, nome}! Sou {assistente}.", "ana maria", "Bia"));
        Assert.Equal("Olá! Sou Bia.", TemplateRenderer.Render("Olá{, nome}! Sou {assistente}.", "5511999", "Bia"));
        Assert.Equal("Oi, tudo bem?", TemplateRenderer.Render("Oi {nome}, tudo bem?", null, "Bia"));
        Assert.Equal(["{nme}"], TemplateRenderer.UnknownVariables("Oi {nme} {nome}"));
        Assert.True(TemplateRenderer.Similarity("a mesma frase longa aqui", "a mesma frase longa aqui") == 1);
    }
}
