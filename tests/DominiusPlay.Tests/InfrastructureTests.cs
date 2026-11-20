using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Evolution;
using DominiusPlay.Infrastructure.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Tests;

public sealed class SqliteDb : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    public SqliteDb() { _conn.Open(); using var db = New(); db.Database.EnsureCreated(); }
    public CrmDbContext New() => new(new DbContextOptionsBuilder<CrmDbContext>().UseSqlite(_conn).Options);
    public void Dispose() => _conn.Dispose();
}

public class ImportTests
{
    [Fact]
    public void Lista_so_de_numeros_sem_cabecalho_e_sem_nome()
    {
        var rows = LeadFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes("14988159045\r\n(21) 99999-8888\r\n\r\n+55 11 98765-4321\r\n")));
        Assert.Equal(["14988159045", "(21) 99999-8888", "+55 11 98765-4321"], rows.Select(r => r.Phone));
        Assert.All(rows, r => Assert.Null(r.Name));
        Assert.Equal(1, rows[0].LineNumber);
    }

    [Theory]
    [InlineData("Números\n14988159045\n21999998888\n")]
    [InlineData("Contatos da feira\n14988159045\n21999998888\n")]
    [InlineData("telefones;observação\n14988159045;\n21999998888;\n")]
    public void Lista_de_numeros_com_cabecalho_qualquer(string csv)
    {
        var rows = LeadFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes(csv)));
        Assert.Equal(["14988159045", "21999998888"], rows.Select(r => r.Phone));
        Assert.All(rows, r => Assert.True(string.IsNullOrEmpty(r.Name)));
    }

    [Fact]
    public void Csv_sem_cabecalho_com_nome_opcional()
    {
        var rows = LeadFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes("Ana;14988159045\n;21999998888\n")));
        Assert.Equal(("Ana", "14988159045"), (rows[0].Name, rows[0].Phone));
        Assert.Equal((null, "21999998888"), (rows[1].Name, rows[1].Phone));
    }

    [Fact]
    public void Xlsx_so_com_numeros_salvos_como_numero()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Plan1");
        ws.Cell(1, 1).Value = 14988159045; ws.Cell(2, 1).Value = 21999998888;
        using var ms = new MemoryStream(); wb.SaveAs(ms); ms.Position = 0;
        var rows = LeadFileParser.ParseXlsx(ms);
        Assert.Equal(["14988159045", "21999998888"], rows.Select(r => r.Phone));
        Assert.All(rows, r => Assert.Null(r.Name));
    }

    [Fact]
    public async Task Autorizacao_declarada_para_a_lista_inteira()
    {
        using var s = new SqliteDb();
        await using var db = s.New();
        var r = await new LeadImportService(db).ImportAsync(
            [new RawLeadRow { LineNumber = 1, Phone = "14988159045" }, new RawLeadRow { LineNumber = 2, Phone = "21999998888" }],
            new("numeros.txt", "TXT", "carlos", null, null, BatchConsentSource: "Pediram informações no Instagram"));
        Assert.Equal((2, 2), (r.Created, r.WithConsent));
        var lead = await db.Leads.Include(l => l.ConsentRecords).FirstAsync();
        Assert.Equal(ConsentStatus.Valido, lead.ConsentStatus);
        Assert.Equal("", lead.Name);
        Assert.Contains("Instagram", lead.ConsentSource);
        Assert.Contains("carlos", lead.ConsentSource);
    }

    [Fact]
    public void Csv_ponto_e_virgula_com_cabecalhos_em_portugues()
    {
        var csv = "Nome;WhatsApp;Cidade;UF;Origem;Tags;Consentimento;Origem do consentimento\n" +
                  "João Silva;(14) 98815-9045;Bauru;sp;Site;revenda;sim;Formulário do site\n" +
                  "Maria;11 8765-4321;;;Evento;;;\n";
        var rows = LeadFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes(csv)));
        Assert.Equal(2, rows.Count);
        Assert.Equal("João Silva", rows[0].Name);
        Assert.Equal("(14) 98815-9045", rows[0].Phone);
        Assert.Equal("Formulário do site", rows[0].ConsentSource);
    }

    [Fact]
    public void Csv_virgula()
    {
        var rows = LeadFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes("name,phone\nAna,+55 21 99999-8888\n")));
        Assert.Equal("Ana", Assert.Single(rows).Name);
    }

    [Fact]
    public void Xlsx_com_telefone_numerico()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Leads");
        ws.Cell(1, 1).Value = "Nome"; ws.Cell(1, 2).Value = "Telefone"; ws.Cell(1, 3).Value = "Cidade";
        ws.Cell(2, 1).Value = "Carlos"; ws.Cell(2, 2).Value = 5514988159045; ws.Cell(2, 3).Value = "Bauru";
        ws.Cell(3, 1).Value = "Paula"; ws.Cell(3, 2).Value = "(21) 99876-5432";
        var ms = new MemoryStream(); wb.SaveAs(ms); ms.Position = 0;
        var rows = LeadFileParser.ParseXlsx(ms);
        Assert.Equal(2, rows.Count);
        Assert.Equal("5514988159045", rows[0].Phone);
        Assert.Equal("Bauru", rows[0].City);
    }

    [Fact]
    public void Colar_varios_formatos()
    {
        var rows = LeadFileParser.ParsePaste("João - (14) 98815-9045\nMaria;21999998888\n11987654321\nPedro\t31 99888-7766\tBH\tMG\n\n");
        Assert.Equal(4, rows.Count);
        Assert.Equal("João", rows[0].Name); Assert.Equal("Maria", rows[1].Name); Assert.Null(rows[2].Name);
        Assert.Equal("BH", rows[3].City); Assert.Equal("MG", rows[3].State);
    }

    [Fact]
    public async Task Importacao_deduplica_normaliza_respeita_bloqueio_e_consentimento()
    {
        using var s = new SqliteDb();
        await using (var db = s.New())
        {
            db.Leads.Add(new Lead { Name = "", Phone = "5521999998888" });
            db.OptOuts.Add(new OptOut { Phone = "5531998887766", Reason = "pediu" });
            await db.SaveChangesAsync();
        }
        await using (var db = s.New())
        {
            var svc = new LeadImportService(db);
            var rows = new List<RawLeadRow>
            {
                new() { LineNumber = 2, Name = "João", Phone = "(14) 98815-9045", Consent = "sim", ConsentSource = "Formulário site" },
                new() { LineNumber = 3, Name = "João dup", Phone = "+55 14 98815-9045" },              // duplicado no arquivo
                new() { LineNumber = 4, Name = "Maria", Phone = "21 99999-8888" },                      // já existe → atualiza nome
                new() { LineNumber = 5, Name = "Bloq", Phone = "31 99888-7766", Consent = "sim", ConsentSource = "x" }, // bloqueado
                new() { LineNumber = 6, Name = "Inválido", Phone = "123" },
                new() { LineNumber = 7, Name = "Sem origem", Phone = "11 98765-4321", Consent = "sim" }, // consentimento sem origem → pendente
            };
            var r = await svc.ImportAsync(rows, new("leads.csv", "CSV", "carlos", "Planilha", "campanha2027"));
            Assert.Equal((2, 1, 1, 1, 1, 1), (r.Created, r.Updated, r.Duplicates, r.Invalid, r.Blocked, r.WithConsent));
        }
        await using (var db = s.New())
        {
            var joao = await db.Leads.Include(l => l.ConsentRecords).SingleAsync(l => l.Phone == "5514988159045");
            Assert.Equal(ConsentStatus.Valido, joao.ConsentStatus);
            Assert.Single(joao.ConsentRecords);
            Assert.Equal("campanha2027", joao.Tags);
            Assert.Equal("Maria", (await db.Leads.SingleAsync(l => l.Phone == "5521999998888")).Name);
            var bloq = await db.Leads.SingleAsync(l => l.Phone == "5531998887766");
            Assert.Equal(ConsentStatus.Revogado, bloq.ConsentStatus);
            Assert.Equal(KanbanStage.NaoTemInteresse, bloq.Stage);
            Assert.Equal(ConsentStatus.Pendente, (await db.Leads.SingleAsync(l => l.Phone == "5511987654321")).ConsentStatus);
            Assert.Equal(1, await db.ImportBatches.CountAsync());
        }
    }

    [Fact]
    public async Task Reimportacao_registra_consentimento_em_lead_existente()
    {
        using var s = new SqliteDb();
        await using (var db = s.New()) { db.Leads.Add(new Lead { Phone = "5514988159045" }); await db.SaveChangesAsync(); }
        await using (var db = s.New())
        {
            var r = await new LeadImportService(db).ImportAsync([new RawLeadRow { LineNumber = 2, Phone = "14988159045", Consent = "sim", ConsentSource = "Formulário" }],
                new("b.csv", "CSV", "carlos", null, null));
            Assert.Equal((0, 1, 1), (r.Created, r.Updated, r.WithConsent));
        }
        await using (var db = s.New())
            Assert.Equal(1, await db.ConsentRecords.CountAsync());
    }

    [Fact]
    public async Task Lista_de_bloqueio_revoga_e_cancela_fila()
    {
        using var s = new SqliteDb();
        Guid leadId;
        await using (var db = s.New())
        {
            var l = new Lead { Phone = "5514988159045", ConsentStatus = ConsentStatus.Valido };
            db.Leads.Add(l);
            db.OutboundQueue.Add(new OutboundQueueItem { LeadId = l.Id, Kind = OutboundKind.AbordagemInicial, Text = "oi", InstanceName = "x" });
            await db.SaveChangesAsync(); leadId = l.Id;
        }
        await using (var db = s.New())
        {
            var (added, invalid, affected) = await new LeadImportService(db).ImportOptOutsAsync(["(14) 98815-9045", "xx"], "pedido", "lista", "carlos");
            Assert.Equal((1, 1, 1), (added, invalid, affected));
        }
        await using (var db = s.New())
        {
            Assert.Equal(ConsentStatus.Revogado, (await db.Leads.FindAsync(leadId))!.ConsentStatus);
            Assert.Equal(MessageStatus.Cancelada, (await db.OutboundQueue.SingleAsync()).Status);
        }
    }

    [Fact]
    public async Task Banco_garante_telefone_unico()
    {
        using var s = new SqliteDb();
        await using var db = s.New();
        db.Leads.Add(new Lead { Phone = "5514988159045" });
        db.Leads.Add(new Lead { Phone = "5514988159045" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}

public class EvolutionTests
{
    public const string UpsertJson = """
    {"event":"messages.upsert","instance":"dominius","data":{"key":{"remoteJid":"5514988159045@s.whatsapp.net","fromMe":false,"id":"3EB0ABC123"},
     "pushName":"João","status":"DELIVERY_ACK","message":{"conversation":"Já trabalho, tenho um Painel Sigma."},"messageType":"conversation",
     "messageTimestamp":1763640000,"instanceId":"x","source":"android"},"destination":"http://crm/webhooks/evolution","date_time":"2026-11-20T10:00:00.000Z",
     "sender":"5514000000000@s.whatsapp.net","server_url":"https://evo.exemplo.com","apikey":"k"}
    """;

    [Fact]
    public void Interpreta_messages_upsert()
    {
        var p = EvolutionWebhookParser.Parse(UpsertJson);
        Assert.Equal(WebhookKind.MessageUpsert, p.Kind);
        Assert.Equal("3EB0ABC123", p.MessageId);
        Assert.Equal("5514988159045", p.Phone);
        Assert.False(p.FromMe);
        Assert.Equal("Já trabalho, tenho um Painel Sigma.", p.Text);
        Assert.Equal("João", p.PushName);
        Assert.Equal("dominius:msg:3EB0ABC123", p.DedupKey);
    }

    [Fact]
    public void Interpreta_lid_com_remoteJidAlt_e_extendedText()
    {
        var json = """{"event":"messages.upsert","instance":"i","data":{"key":{"remoteJid":"12345@lid","remoteJidAlt":"5511987654321@s.whatsapp.net","fromMe":true,"id":"X1"},"message":{"extendedTextMessage":{"text":"olá"}}}}""";
        var p = EvolutionWebhookParser.Parse(json);
        Assert.Equal("5511987654321", p.Phone); Assert.True(p.FromMe); Assert.Equal("olá", p.Text);
    }

    [Fact]
    public void Interpreta_status_e_conexao_e_ignora_grupo()
    {
        var st = EvolutionWebhookParser.Parse("""{"event":"messages.update","instance":"i","data":{"keyId":"X1","remoteJid":"5511987654321@s.whatsapp.net","fromMe":true,"status":"READ"}}""");
        Assert.Equal(WebhookKind.MessageStatusUpdate, st.Kind);
        Assert.Equal(MessageStatus.Lida, EvolutionWebhookParser.MapStatus(st.Status));
        var c = EvolutionWebhookParser.Parse("""{"event":"connection.update","instance":"i","data":{"instance":"i","state":"close"}}""");
        Assert.Equal("close", c.ConnectionState);
        var g = EvolutionWebhookParser.Parse("""{"event":"messages.upsert","instance":"i","data":{"key":{"remoteJid":"123@g.us","fromMe":false,"id":"G"},"message":{"conversation":"x"}}}""");
        Assert.True(g.IsGroup);
    }

    private sealed class Capture(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public List<(HttpMethod, string, string?, string?)> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Add((r.Method, r.RequestUri!.AbsoluteUri, r.Headers.TryGetValues("apikey", out var v) ? v.First() : null,
                r.Content is null ? null : await r.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly EvolutionConfig Cfg = new("https://evo.exemplo.com/", "dominius play", "SECRET");

    [Fact]
    public async Task SendText_usa_endpoint_e_header_corretos()
    {
        var h = new Capture(HttpStatusCode.Created, """{"key":{"remoteJid":"5514988159045@s.whatsapp.net","fromMe":true,"id":"BAE5F00"},"status":"PENDING"}""");
        var r = await new EvolutionApiClient(new HttpClient(h)).SendTextAsync(Cfg, "5514988159045", "Olá");
        Assert.True(r.Success); Assert.Equal("BAE5F00", r.MessageId);
        var (m, url, key, body) = h.Calls.Single();
        Assert.Equal(HttpMethod.Post, m);
        Assert.Equal("https://evo.exemplo.com/message/sendText/dominius%20play", url);
        Assert.Equal("SECRET", key);
        var j = JsonNode.Parse(body!)!;
        Assert.Equal("5514988159045", j["number"]!.GetValue<string>()); Assert.Equal("Olá", j["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConnectionState_e_erro_http()
    {
        var ok = new Capture(HttpStatusCode.OK, """{"instance":{"instanceName":"dominius play","state":"open"}}""");
        var st = await new EvolutionApiClient(new HttpClient(ok)).GetConnectionStateAsync(Cfg);
        Assert.True(st.IsOpen);
        Assert.EndsWith("/instance/connectionState/dominius%20play", ok.Calls.Single().Item2);
        var bad = new Capture(HttpStatusCode.Unauthorized, """{"status":401,"error":"Unauthorized"}""");
        var r = await new EvolutionApiClient(new HttpClient(bad)).SendTextAsync(Cfg, "1", "x");
        Assert.False(r.Success); Assert.Equal(401, r.HttpStatus);
    }

    [Fact]
    public async Task SetWebhook_envia_payload_v2()
    {
        var h = new Capture(HttpStatusCode.Created, "{}");
        var (ok, _) = await new EvolutionApiClient(new HttpClient(h)).SetWebhookAsync(Cfg, "https://crm/webhooks/evolution",
            new Dictionary<string, string> { ["x-dominius-token"] = "T" }, EvolutionApiClient.DefaultWebhookEvents);
        Assert.True(ok);
        var (_, url, _, body) = h.Calls.Single();
        Assert.EndsWith("/webhook/set/dominius%20play", url);
        var w = JsonNode.Parse(body!)!["webhook"]!;
        Assert.True(w["enabled"]!.GetValue<bool>());
        Assert.Equal("T", w["headers"]!["x-dominius-token"]!.GetValue<string>());
        Assert.Contains("MESSAGES_UPSERT", w["events"]!.AsArray().Select(e => e!.GetValue<string>()));
    }
}
