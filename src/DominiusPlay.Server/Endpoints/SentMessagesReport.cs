using System.Globalization;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Endpoints;

/// <summary>
/// Relatório de mensagens enviadas (tela Relatórios → aba "Mensagens enviadas").
/// Conta toda mensagem de saída que chegou a ser enviada ou falhou: IA, atendentes, envios manuais pelo celular e testes.
/// Notas internas do sistema (status Recebida) não entram. Cada mensagem conta só no status atual dela.
/// </summary>
public static class SentMessagesReport
{
    public const string NoCampaign = "none";
    public const string AuthorAi = "ia";
    public const string AuthorManual = "manual";
    public const string AuthorUnknownAgent = "atendente";

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private sealed record Item(DateTimeOffset Local, MessageStatus Status, string CampaignKey, string AuthorKey, string? Error);

    /// <summary>Agrupa o status da Evolution nos quatro status do relatório.</summary>
    public static string Bucket(MessageStatus s) => s switch
    {
        MessageStatus.Lida => "lido",
        MessageStatus.Entregue => "entregue",
        MessageStatus.Falhou => "falhou",
        _ => "enviado", // Enviando, Pendente, EnviadaServidor: saiu do sistema, ainda sem confirmação de entrega
    };

    public static async Task<SentMessagesReportDto> BuildAsync(CrmDbContext db, DateTimeOffset from, DateTimeOffset to, string? campaign, string? author)
    {
        if (to < from) (from, to) = (to, from);
        var offset = from.Offset; // fuso do aplicativo que pediu o relatório (o "from" vem com o offset local)
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var campaigns = await db.Campaigns.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name);

        // SQLite não compara DateTimeOffset no banco: filtra em memória, só com as colunas necessárias.
        var raw = await db.Messages.AsNoTracking()
            .Where(m => m.Direction == MessageDirection.Saida && m.Status != MessageStatus.Recebida
                        && m.Status != MessageStatus.NaFila && m.Status != MessageStatus.Cancelada)
            .Select(m => new { m.Timestamp, m.Status, m.CampaignId, m.Author, m.SentByUserId, m.Error })
            .ToListAsync();

        var all = raw.Where(m => m.Timestamp >= from && m.Timestamp <= to)
            .Select(m => new Item(m.Timestamp.ToOffset(offset), m.Status,
                m.CampaignId is { } c ? c.ToString() : NoCampaign,
                AuthorKey(m.Author, m.SentByUserId), m.Error))
            .ToList();

        string CampaignLabel(string key) =>
            key == NoCampaign ? "Sem campanha (conversas)" : Guid.TryParse(key, out var id) && campaigns.TryGetValue(id, out var n) ? n : "Campanha excluída";
        string AuthorLabel(string key) => key switch
        {
            AuthorAi => "IA (assistente virtual)",
            AuthorManual => "Manual pelo celular / WhatsApp Web",
            AuthorUnknownAgent => "Atendente (não identificado)",
            _ when key.StartsWith("user:") && Guid.TryParse(key[5..], out var id) && users.TryGetValue(id, out var n) => n,
            _ => "Usuário excluído",
        };

        // Opções dos filtros: sempre todas as campanhas e atendentes que enviaram no período, mesmo com filtro aplicado.
        var campaignOptions = all.Select(i => i.CampaignKey).Distinct().Concat(campaigns.Keys.Select(k => k.ToString())).Distinct()
            .Select(k => new FilterOption(k, CampaignLabel(k))).OrderBy(o => o.Key == NoCampaign ? 0 : 1).ThenBy(o => o.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        var authorOptions = all.Select(i => i.AuthorKey).Concat([AuthorAi]).Concat(users.Keys.Select(k => "user:" + k)).Distinct()
            .Select(k => new FilterOption(k, AuthorLabel(k))).OrderBy(o => o.Key == AuthorAi ? 0 : 1).ThenBy(o => o.Label, StringComparer.CurrentCultureIgnoreCase).ToList();

        var items = all.Where(i => string.IsNullOrEmpty(campaign) || i.CampaignKey == campaign)
            .Where(i => string.IsNullOrEmpty(author) || i.AuthorKey == author).ToList();

        // Granularidade do período: por hora (até 2 dias), por dia (até 92 dias) ou por mês.
        var span = to - from;
        var grouping = span <= TimeSpan.FromDays(2) ? "hora" : span <= TimeSpan.FromDays(92) ? "dia" : "mes";
        Func<DateTimeOffset, DateTimeOffset> slot = grouping switch
        {
            "hora" => t => new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Offset),
            "dia" => t => new DateTimeOffset(t.Year, t.Month, t.Day, 0, 0, 0, t.Offset),
            _ => t => new DateTimeOffset(t.Year, t.Month, 1, 0, 0, 0, t.Offset),
        };
        Func<DateTimeOffset, DateTimeOffset> next = grouping switch { "hora" => t => t.AddHours(1), "dia" => t => t.AddDays(1), _ => t => t.AddMonths(1) };
        Func<DateTimeOffset, string> label = grouping switch
        {
            "hora" => t => t.ToString("dd/MM HH'h'", PtBr),
            "dia" => t => t.ToString("ddd dd/MM", PtBr),
            _ => t => PtBr.TextInfo.ToTitleCase(t.ToString("MMM/yyyy", PtBr)),
        };
        var bySlot = items.GroupBy(i => slot(i.Local)).ToDictionary(g => g.Key, g => g.ToList());
        var byPeriod = new List<SentMessagesRow>();
        for (var t = slot(from.ToOffset(offset)); t <= to.ToOffset(offset); t = next(t))
            byPeriod.Add(Row(t.ToString("o"), label(t), bySlot.TryGetValue(t, out var l) ? l : []));

        var byCampaign = items.GroupBy(i => i.CampaignKey).Select(g => Row(g.Key, CampaignLabel(g.Key), g))
            .OrderByDescending(r => r.Total).ToList();
        var byAuthor = items.GroupBy(i => i.AuthorKey).Select(g => Row(g.Key, AuthorLabel(g.Key), g))
            .OrderBy(r => r.Key == AuthorAi ? 0 : 1).ThenByDescending(r => r.Total).ToList();
        var failures = items.Where(i => i.Status == MessageStatus.Falhou)
            .GroupBy(i => FailureReason(i.Error)).Select(g => new ReportRow(g.Key, g.Count()))
            .OrderByDescending(r => r.Value).Take(10).ToList();

        var queued = await db.OutboundQueue.AsNoTracking().CountAsync(q => q.Status == MessageStatus.NaFila || q.Status == MessageStatus.Enviando);

        return new SentMessagesReportDto(from, to, grouping, Row("total", "Total", items), queued,
            byPeriod, byCampaign, byAuthor, failures, campaignOptions, authorOptions);
    }

    private static string AuthorKey(MessageAuthor a, Guid? userId) => a switch
    {
        MessageAuthor.Ia => AuthorAi,
        MessageAuthor.ManualNoAparelho => AuthorManual,
        MessageAuthor.Atendente => userId is { } u ? "user:" + u : AuthorUnknownAgent,
        // Falhas e testes ficam gravados como Sistema. Com usuário: envio pedido por ele (atendimento humano ou teste).
        // Sem usuário: envio automático (campanha, resposta ou acompanhamento), que é da IA.
        _ when userId is { } u2 => "user:" + u2,
        _ => AuthorAi,
    };

    private static string FailureReason(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "Sem detalhe do erro";
        var e = error.Trim().ReplaceLineEndings(" ");
        return e.Length > 90 ? e[..90] + "…" : e;
    }

    private static SentMessagesRow Row(string key, string label, IEnumerable<Item> items)
    {
        int sent = 0, delivered = 0, read = 0, failed = 0;
        foreach (var i in items)
            switch (Bucket(i.Status))
            {
                case "lido": read++; break;
                case "entregue": delivered++; break;
                case "falhou": failed++; break;
                default: sent++; break;
            }
        return new SentMessagesRow(key, label, sent + delivered + read + failed, sent, delivered, read, failed);
    }
}
