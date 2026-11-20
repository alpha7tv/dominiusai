using System.Net.Http.Json;
using System.Text;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DominiusPlay.Tests;

/// <summary>
/// Conversas simuladas com o modelo REAL da OpenAI (sem WhatsApp real: a Evolution é simulada).
/// Só roda quando OPENAI_API_KEY está definida; grava as transcrições em conversas-simuladas.md.
/// </summary>
public class LiveConversationTests
{
    private static readonly string? Key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    private static readonly string Model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o";

    public static IEnumerable<object[]> Scenarios() =>
    [
        ["Revendedor com Painel Sigma (350 clientes)", "5514900000001", new[] { "Oi, vi o anúncio de vocês", "Já trabalho, tenho um Painel Sigma.", "Meu servidor cai muito e tenho 350 clientes.", "Principalmente nos jogos.", "Quero sim, quais são os requisitos?" }],
        ["Iniciante", "5514900000002", new[] { "Olá, quero começar a revender", "Não conheço, nunca usei painel", "Quero começar com uns 10 clientes, renda extra", "Quanto custa pra começar?" }],
        ["Quer marca própria", "5514900000003", new[] { "Boa tarde", "Já trabalho há 3 anos, tenho uns 1200 clientes", "Quero ter meu próprio app com minha logo e meu painel", "Em quanto tempo fica pronto?" }],
        ["Pergunta sobre garantias e canais", "5514900000004", new[] { "Oi", "Vocês garantem que nunca trava? E tem todos os canais de futebol?" }],
        ["Pede atendente humano", "5514900000005", new[] { "Oi", "Prefiro falar com uma pessoa, pode ser?" }],
        ["Pede para não receber mais mensagens", "5514900000006", new[] { "Oi", "Não tenho interesse, me tire da lista" }],
    ];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Conversa_real(string title, string phone, string[] turns)
    {
        if (string.IsNullOrWhiteSpace(Key)) return; // sem chave: não roda (não envia nada)
        using var f = new CrmFactory(realAi: true);
        var c = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(c);
        (await c.PutAsJsonAsync("/api/ai/settings", new AiSettingsUpdate(true, Model, Key, 0.4, 500, "Lia", "", 1_000_000, false, 0))).EnsureSuccessStatusCode();
        var token = await f.WebhookTokenAsync();
        var log = new StringBuilder($"\n## {title}\n\n");
        var i = 0;
        foreach (var turn in turns)
        {
            var body = $$$"""{"event":"messages.upsert","instance":"dominius","data":{"key":{"remoteJid":"{{{phone}}}@s.whatsapp.net","fromMe":false,"id":"L{{{phone}}}{{{i++}}}"},"pushName":"Cliente","message":{"conversation":"{{{turn}}}"},"messageTimestamp":{{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}}}""";
            var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/evolution") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Add("x-dominius-token", token);
            (await f.CreateClient().SendAsync(req)).EnsureSuccessStatusCode();
            log.AppendLine($"**Cliente:** {turn}  ");
            var leadId = await f.WithDb(db => db.Leads.Select(l => l.Id).SingleAsync());
            using (var scope = f.Services.CreateScope())
            {
                var outcome = await scope.ServiceProvider.GetRequiredService<AiResponder>().RespondAsync(leadId);
                if (outcome != AiReplyOutcome.Replied) log.AppendLine($"_(IA não respondeu: {outcome})_  ");
            }
            var before = f.Evolution.Sent.Count;
            await f.RunQueueAsync();
            foreach (var s in f.Evolution.Sent.Skip(before)) log.AppendLine($"**Assistente:** {s.Text}  ");
            var actions = await f.WithDb(db => db.AuditLogs.Where(a => a.Action == "ai.actions" && a.EntityId == leadId.ToString()).OrderByDescending(a => a.CreatedAt).Select(a => a.Details).FirstOrDefaultAsync());
            if (actions is not null && f.Evolution.Sent.Count > before) log.AppendLine($"<sub>Ações internas: {actions}</sub>  ");
            await f.WithDb(async db => { foreach (var a in db.AuditLogs.Where(a => a.Action == "ai.actions")) a.Action = "ai.actions.seen"; return await db.SaveChangesAsync(); });
        }
        var lead = await f.WithDb(db => db.Leads.AsNoTracking().SingleAsync());
        log.AppendLine($"\n> Resultado no CRM: etapa **{Domain.Services.KanbanRules.Title(lead.Stage)}**, modo **{Domain.Services.KanbanRules.ModeTitle(lead.Mode)}**, " +
                       $"experiência {lead.Experience}, painel {lead.PanelUsed ?? "—"}, clientes {lead.ActiveClients?.ToString() ?? "—"}, dor {Domain.Services.PainClassifier.Title(lead.MainPain)}, " +
                       $"plano {lead.InterestPlanCode ?? "—"}, consentimento {lead.ConsentStatus}.\n");
        lock (typeof(LiveConversationTests))
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "conversas-simuladas.md"), log.ToString());

        // verificações mínimas que valem para qualquer modelo
        Assert.All(f.Evolution.Sent, s => Assert.DoesNotContain("R$ 1.400", s.Text)); // nunca usa preços do site
        if (title.StartsWith("Pede para não")) { Assert.Empty(f.Evolution.Sent.Skip(1)); Assert.Equal(ConsentStatus.Revogado, lead.ConsentStatus); }
        if (title.StartsWith("Pede atendente")) Assert.Equal(ConversationMode.AtendimentoHumano, lead.Mode);
        Assert.NotEqual(KanbanStage.NegocioFechado, lead.Stage);
    }
}
