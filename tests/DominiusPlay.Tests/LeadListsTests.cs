using System.Net.Http.Json;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Import;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Tests;

/// <summary>Listas (pastas) de importação, números repetidos ignorados e público da campanha por lista/período.</summary>
public class LeadListsTests
{
    private const string Approved = "Olá{, nome}! Sou {assistente}, assistente virtual da Dominius Play. Você trabalha com revenda de IPTV? Se não quiser receber mensagens, responda SAIR.";

    [Fact]
    public async Task Importacao_com_nome_ignora_numeros_ja_existentes_e_acrescenta_na_mesma_lista()
    {
        using var s = new SqliteDb();
        Guid first;
        await using (var db = s.New())
        {
            var r = await new LeadImportService(db).ImportAsync(
                [new RawLeadRow { LineNumber = 1, Phone = "11987650001" }, new RawLeadRow { LineNumber = 2, Phone = "11987650002" }],
                new("a.csv", "CSV", "carlos", null, "lote1", BatchName: "Leads Nov 25"));
            Assert.Equal((2, 0), (r.Created, r.Updated));
            Assert.Equal("Leads Nov 25", r.BatchName);
            first = r.BatchId;
        }
        await using (var db = s.New())
        {
            // outra lista: 1 número já existia (ignorado, continua na lista de origem e não ganha a tag nova) e 1 novo
            var r = await new LeadImportService(db).ImportAsync(
                [new RawLeadRow { LineNumber = 1, Phone = "(11) 98765-0002" }, new RawLeadRow { LineNumber = 2, Phone = "11987650003" }],
                new("b.csv", "CSV", "carlos", null, "lote2", BatchName: "Grupos IPTV"));
            Assert.Equal((1, 1), (r.Created, r.Updated));
            Assert.NotEqual(first, r.BatchId);
            Assert.Contains(r.Rows, x => x.Outcome == ImportRowOutcome.Updated && x.Message!.Contains("ignorado"));
        }
        await using (var db = s.New())
        {
            // mesmo nome (sem diferenciar maiúsculas): acrescenta à lista existente
            var r = await new LeadImportService(db).ImportAsync([new RawLeadRow { LineNumber = 1, Phone = "11987650004" }],
                new("c.csv", "CSV", "carlos", null, null, BatchName: "leads nov 25"));
            Assert.Equal(first, r.BatchId);
        }
        await using (var db = s.New())
        {
            Assert.Equal(2, await db.ImportBatches.CountAsync());
            var two = await db.Leads.SingleAsync(l => l.Phone == "5511987650002");
            Assert.Equal(first, two.ImportBatchId);
            Assert.Equal("lote1", two.Tags);
            Assert.Equal(3, await db.Leads.CountAsync(l => l.ImportBatchId == first));
            Assert.Equal(3, (await db.ImportBatches.SingleAsync(b => b.Id == first)).Created);
        }
    }

    [Fact]
    public void Guard_nunca_envia_segunda_abordagem_para_o_mesmo_numero()
    {
        var lead = new Lead { Phone = "5514988159045", ConsentStatus = ConsentStatus.Valido };
        var ctx = new SendContext(lead, OutboundKind.AbordagemInicial, DateTimeOffset.UtcNow, false, true, null, null, false, 0, 50,
            TimeSpan.FromMinutes(15), null, true, 0, TimeZoneInfo.Utc, AlreadyApproached: true);
        var r = SendGuard.Evaluate(ctx);
        Assert.False(r.Allowed);
        Assert.Null(r.RetryAt);
        Assert.Contains("duas vezes", r.Reason);
    }

    private static async Task<Guid> ApprovedTemplate(HttpClient c)
    {
        var r = await (await c.PostAsJsonAsync("/api/templates", new TemplateCreate("Abordagem listas", "Abordagem inicial", Approved))).Content.ReadFromJsonAsync<OperationResult>();
        var vid = Guid.Parse(r!.Detail!);
        (await c.PostAsJsonAsync($"/api/templates/versions/{vid}/approve", new TemplateApprove(null))).EnsureSuccessStatusCode();
        return (await c.GetFromJsonAsync<List<TemplateDto>>("/api/templates"))!.First(t => t.VersionId == vid).Id;
    }

    private static CampaignUpsert Camp(Guid template, string batchIds = "", DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        new("Lote", "00:00", "23:59", "0,1,2,3,4,5,6", 100, 900, 50, null, template, "", ImportBatchIds: batchIds, ImportedFrom: from, ImportedTo: to);

    private static async Task<CampaignValidation> Validate(HttpClient c, Guid id) =>
        (await (await c.PostAsync($"/api/campaigns/{id}/validate", null)).Content.ReadFromJsonAsync<CampaignValidation>())!;

    [Fact]
    public async Task Campanha_escolhe_lista_e_periodo_de_importacao_e_nunca_repete_numero()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        var a = (await (await c.PostAsJsonAsync("/api/import/paste", new PasteImportRequest("11987650001\n11987650002", null, null, "Pediram contato no Instagram", "Leads Nov 25")))
            .Content.ReadFromJsonAsync<ImportResponse>())!;
        var b = (await (await c.PostAsJsonAsync("/api/import/paste", new PasteImportRequest("11987650002\n11987650003\n11987650004", null, null, "Grupo de revenda", "Grupos IPTV")))
            .Content.ReadFromJsonAsync<ImportResponse>())!;
        Assert.Equal((2, 1), (b.Created, b.Updated)); // o 0002 já existia: não entra de novo

        var batches = (await c.GetFromJsonAsync<List<ImportBatchDto>>("/api/import/batches"))!;
        Assert.Equal(2, batches.Count);
        Assert.Equal(2, batches.Single(x => x.Name == "Grupos IPTV").Leads);
        Assert.Equal(2, (await c.GetFromJsonAsync<List<LeadCard>>($"/api/leads?batch={b.BatchId}"))!.Count);

        var tid = await ApprovedTemplate(c);
        var camp = (await (await c.PostAsJsonAsync("/api/campaigns", Camp(tid, b.BatchId.ToString()))).Content.ReadFromJsonAsync<CampaignDto>())!;
        Assert.Equal(b.BatchId.ToString(), camp.ImportBatchIds);
        Assert.Equal(2, (await Validate(c, camp.Id)).Eligible);

        // as duas listas
        (await c.PutAsJsonAsync($"/api/campaigns/{camp.Id}", Camp(tid, $"{a.BatchId},{b.BatchId}"))).EnsureSuccessStatusCode();
        Assert.Equal(4, (await Validate(c, camp.Id)).Eligible);

        // período de importação: hoje entra; amanhã em diante não há ninguém
        var today = DateTimeOffset.UtcNow.Date;
        (await c.PutAsJsonAsync($"/api/campaigns/{camp.Id}", Camp(tid, from: new DateTimeOffset(today.AddDays(-1)), to: new DateTimeOffset(today)))).EnsureSuccessStatusCode();
        Assert.Equal(4, (await Validate(c, camp.Id)).Eligible);
        (await c.PutAsJsonAsync($"/api/campaigns/{camp.Id}", Camp(tid, from: new DateTimeOffset(today.AddDays(1))))).EnsureSuccessStatusCode();
        var v = await Validate(c, camp.Id);
        Assert.Equal(0, v.Eligible);
        Assert.Contains(v.Problems, p => p.Contains("período"));
        var bad = await c.PutAsJsonAsync($"/api/campaigns/{camp.Id}", Camp(tid, from: new DateTimeOffset(today), to: new DateTimeOffset(today.AddDays(-2))));
        Assert.False(bad.IsSuccessStatusCode);

        // envia para a lista "Grupos IPTV"
        (await c.PutAsJsonAsync($"/api/campaigns/{camp.Id}", Camp(tid, b.BatchId.ToString()))).EnsureSuccessStatusCode();
        var st = (await (await c.PostAsync($"/api/campaigns/{camp.Id}/start", null)).Content.ReadFromJsonAsync<OperationResult>())!;
        Assert.True(st.Ok, st.Message);
        Assert.Equal(1, await f.RunQueueAsync());
        var sentPhone = f.Evolution.Sent.Single().Number;

        // um item extra para o mesmo número (outra campanha/agente): é cancelado, nunca enviado duas vezes
        var leadId = await f.WithDb(db => db.Leads.Where(l => l.Phone == sentPhone).Select(l => l.Id).SingleAsync());
        await f.WithDb(async db =>
        {
            var l = await db.Leads.SingleAsync(x => x.Id == leadId);
            l.Mode = ConversationMode.AguardandoContato; l.Stage = KanbanStage.PrimeiroContato; // mesmo se o cartão voltar para o início
            db.OutboundQueue.Add(new OutboundQueueItem { LeadId = leadId, Kind = OutboundKind.AbordagemInicial, Text = "Oi de novo", NotBefore = DateTimeOffset.UtcNow.AddDays(-1) });
            await db.SaveChangesAsync();
            return 0;
        });
        await f.RunQueueAsync();
        Assert.Single(f.Evolution.Sent, m => m.Number == sentPhone);
        var extra = await f.WithDb(db => db.OutboundQueue.SingleAsync(q => q.Text == "Oi de novo"));
        Assert.Equal(MessageStatus.Cancelada, extra.Status);
        Assert.Contains("duas vezes", extra.LastError);

        // nova campanha com as duas listas: fica de fora quem já recebeu e quem já está na fila da primeira (4 - 1 - 1)
        var camp2 = (await (await c.PostAsJsonAsync("/api/campaigns", Camp(tid, $"{a.BatchId},{b.BatchId}"))).Content.ReadFromJsonAsync<CampaignDto>())!;
        Assert.Equal(2, (await Validate(c, camp2.Id)).Eligible);
    }

    [Fact]
    public async Task Renomear_lista_com_nome_existente_junta_as_listas()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var a = (await (await c.PostAsJsonAsync("/api/import/paste", new PasteImportRequest("11987650001", null, null))).Content.ReadFromJsonAsync<ImportResponse>())!;
        var b = (await (await c.PostAsJsonAsync("/api/import/paste", new PasteImportRequest("11987650002", null, null, null, "Lista antiga"))).Content.ReadFromJsonAsync<ImportResponse>())!;
        var r = (await (await c.PutAsJsonAsync($"/api/import/batches/{a.BatchId}", new ImportBatchRename("Primeiros 700"))).Content.ReadFromJsonAsync<OperationResult>())!;
        Assert.True(r.Ok, r.Message);
        r = (await (await c.PutAsJsonAsync($"/api/import/batches/{b.BatchId}", new ImportBatchRename("primeiros 700"))).Content.ReadFromJsonAsync<OperationResult>())!;
        Assert.True(r.Ok, r.Message);
        var batches = (await c.GetFromJsonAsync<List<ImportBatchDto>>("/api/import/batches"))!;
        var only = Assert.Single(batches);
        Assert.Equal(("Primeiros 700", 2), (only.Name, only.Leads));
    }
}
