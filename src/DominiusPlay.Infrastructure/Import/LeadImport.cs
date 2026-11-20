using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Infrastructure.Import;

/// <summary>Linha bruta lida de qualquer fonte (CSV, XLSX, colar, manual).</summary>
public sealed class RawLeadRow
{
    public int LineNumber { get; set; }
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Source { get; set; }
    public string? Notes { get; set; }
    public string? Tags { get; set; }
    /// <summary>Texto da coluna de consentimento ("sim", "true", "1", data…). Vazio = sem consentimento.</summary>
    public string? Consent { get; set; }
    public string? ConsentSource { get; set; }
    public string? ConsentDate { get; set; }
}

public enum ImportRowOutcome { Created, Updated, DuplicateInFile, Invalid, Blocked }

public sealed record ImportRowResult(int Line, string? RawPhone, string? Phone, ImportRowOutcome Outcome, string? Message);

public sealed record ImportResult(Guid BatchId, int Total, int Created, int Updated, int Duplicates, int Invalid, int Blocked,
    int WithConsent, IReadOnlyList<ImportRowResult> Rows);

public static class LeadFileParser
{
    private static readonly Dictionary<string, string> HeaderMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nome"] = "name", ["name"] = "name", ["cliente"] = "name", ["contato"] = "name",
        ["telefone"] = "phone", ["phone"] = "phone", ["whatsapp"] = "phone", ["celular"] = "phone", ["numero"] = "phone", ["fone"] = "phone",
        ["telefones"] = "phone", ["numeros"] = "phone", ["celulares"] = "phone", ["whatsapps"] = "phone", ["tel"] = "phone", ["zap"] = "phone",
        ["numero de telefone"] = "phone", ["numero do whatsapp"] = "phone", ["numero whatsapp"] = "phone", ["phone number"] = "phone",
        ["cidade"] = "city", ["city"] = "city",
        ["estado"] = "state", ["uf"] = "state", ["state"] = "state",
        ["origem"] = "source", ["source"] = "source", ["fonte"] = "source",
        ["observacoes"] = "notes", ["observacao"] = "notes", ["obs"] = "notes", ["notes"] = "notes",
        ["tags"] = "tags", ["tag"] = "tags", ["etiquetas"] = "tags",
        ["consentimento"] = "consent", ["autorizacao"] = "consent", ["autorizado"] = "consent", ["optin"] = "consent", ["opt-in"] = "consent",
        ["origem consentimento"] = "consentSource", ["origem do consentimento"] = "consentSource", ["fonte consentimento"] = "consentSource",
        ["data consentimento"] = "consentDate", ["data do consentimento"] = "consentDate",
    };

    public static string? MapHeader(string header) =>
        HeaderMap.TryGetValue(PainClassifier.Strip(header.Trim().ToLowerInvariant()).Replace('_', ' '), out var v) ? v : null;

    /// <summary>
    /// CSV/TXT. Aceita arquivo com cabeçalho (telefone, nome, cidade…) ou só uma lista de números,
    /// sem cabeçalho e sem nome: o nome é opcional e a coluna de telefone é detectada pelo conteúdo.
    /// </summary>
    public static List<RawLeadRow> ParseCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var firstLine = text.Split('\n')[0];
        var delimiter = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ";" : firstLine.Contains('\t') ? "\t" : ",";
        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = delimiter, BadDataFound = null, MissingFieldFound = null, DetectColumnCountChanges = false, HasHeaderRecord = false };
        using var csv = new CsvReader(new StringReader(text), cfg);
        var table = new List<(int Line, string[] Cells)>();
        while (csv.Read())
        {
            var cells = csv.Parser.Record ?? [];
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            table.Add((csv.Parser.Row, cells));
        }
        return FromTable(table);
    }

    public static List<RawLeadRow> ParseXlsx(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.First();
        var range = ws.RangeUsed();
        if (range is null) return [];
        var table = new List<(int Line, string[] Cells)>();
        foreach (var row in range.RowsUsed())
        {
            var cells = row.Cells(1, range.ColumnCount()).Select(c =>
                // números de telefone salvos como número no Excel
                c.DataType == XLDataType.Number ? c.GetDouble().ToString("0", CultureInfo.InvariantCulture)
                : c.DataType == XLDataType.DateTime ? c.GetDateTime().ToString("yyyy-MM-dd") : c.GetString()).ToArray();
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            table.Add((row.RowNumber(), cells));
        }
        return FromTable(table);
    }

    /// <summary>Célula que parece um telefone (8 ou mais dígitos).</summary>
    public static bool LooksLikePhone(string? s) => s is not null && s.Count(char.IsAsciiDigit) >= 8;

    /// <summary>
    /// Converte a planilha em linhas. Se a primeira linha tiver cabeçalhos conhecidos, usa os nomes das colunas.
    /// Senão (lista só de números, ou cabeçalho com outros nomes), detecta a coluna de telefone pelo conteúdo
    /// e usa a primeira coluna de texto, se houver, como nome.
    /// </summary>
    private static List<RawLeadRow> FromTable(List<(int Line, string[] Cells)> table)
    {
        if (table.Count == 0) return [];
        var header = table[0].Cells;
        var map = new Dictionary<int, string>();
        for (var i = 0; i < header.Length; i++)
            if (MapHeader(header[i]) is { } key) map.TryAdd(i, key);

        var rows = new List<RawLeadRow>();
        if (map.ContainsValue("phone"))
        {
            foreach (var (line, cells) in table.Skip(1))
            {
                var c = new Dictionary<string, string>();
                foreach (var (col, key) in map) c[key] = col < cells.Length ? cells[col] : "";
                rows.Add(FromCells(c, line));
            }
            return rows;
        }

        // Sem coluna "telefone": a primeira linha só é dados se tiver um número.
        var data = header.Any(LooksLikePhone) ? table : table.Skip(1).ToList();
        if (data.Count == 0) return [];
        var width = data.Max(r => r.Cells.Length);
        var phoneCol = Enumerable.Range(0, width).OrderByDescending(i => data.Count(r => i < r.Cells.Length && LooksLikePhone(r.Cells[i]))).First();
        foreach (var (line, cells) in data)
        {
            var phone = phoneCol < cells.Length ? cells[phoneCol].Trim() : "";
            var others = cells.Where((_, j) => j != phoneCol).Select(p => p.Trim()).Where(p => p.Length > 0 && !LooksLikePhone(p)).ToList();
            rows.Add(new RawLeadRow { LineNumber = line, Phone = phone, Name = others.FirstOrDefault() });
        }
        return rows;
    }

    /// <summary>
    /// Copiar/colar: uma pessoa por linha. Aceita "Nome;Telefone", "Nome,Telefone", "Nome - Telefone",
    /// "Telefone Nome", colunas separadas por TAB (colado do Excel) ou só o telefone.
    /// </summary>
    public static List<RawLeadRow> ParsePaste(string text)
    {
        var rows = new List<RawLeadRow>();
        var lines = text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.Length == 0) continue;
            string[] parts = l.Contains('\t') ? l.Split('\t') : l.Contains(';') ? l.Split(';') : l.Split(',');
            if (parts.Length == 1)
            {
                // separa o trecho numérico do texto
                var m = System.Text.RegularExpressions.Regex.Match(l, @"\+?[\d\s().-]{8,}");
                if (m.Success)
                {
                    var name = (l[..m.Index] + " " + l[(m.Index + m.Length)..]).Trim(' ', '-', ':', '–');
                    rows.Add(new RawLeadRow { LineNumber = i + 1, Phone = m.Value.Trim(), Name = name.Length > 0 ? name : null });
                }
                else rows.Add(new RawLeadRow { LineNumber = i + 1, Phone = l });
                continue;
            }
            var phoneIdx = Array.FindIndex(parts, p => p.Count(char.IsAsciiDigit) >= 8);
            var row = new RawLeadRow { LineNumber = i + 1 };
            if (phoneIdx >= 0)
            {
                row.Phone = parts[phoneIdx].Trim();
                var others = parts.Where((_, j) => j != phoneIdx).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                if (others.Count > 0) row.Name = others[0];
                if (others.Count > 1) row.City = others[1];
                if (others.Count > 2) row.State = others[2];
            }
            else row.Phone = l;
            rows.Add(row);
        }
        return rows;
    }

    private static RawLeadRow FromCells(Dictionary<string, string> c, int line) => new()
    {
        LineNumber = line,
        Name = c.GetValueOrDefault("name"), Phone = c.GetValueOrDefault("phone"), City = c.GetValueOrDefault("city"),
        State = c.GetValueOrDefault("state"), Source = c.GetValueOrDefault("source"), Notes = c.GetValueOrDefault("notes"),
        Tags = c.GetValueOrDefault("tags"), Consent = c.GetValueOrDefault("consent"),
        ConsentSource = c.GetValueOrDefault("consentSource"), ConsentDate = c.GetValueOrDefault("consentDate"),
    };
}

public class LeadImportService(CrmDbContext db)
{
    public sealed record Options(string FileName, string Format, string ImportedBy, string? DefaultSource, string? DefaultTags,
        bool UpdateExisting = true, string? BatchConsentSource = null);

    /// <summary>
    /// Importa linhas: normaliza telefone, descarta inválidos, detecta duplicados no arquivo e no banco,
    /// respeita lista de bloqueio e só registra consentimento quando a linha traz origem verificável.
    /// Importar NÃO autoriza envio: sem consentimento o lead fica "Pendente" e fora de qualquer disparo.
    /// </summary>
    public async Task<ImportResult> ImportAsync(IReadOnlyList<RawLeadRow> rows, Options opt, CancellationToken ct = default)
    {
        var batch = new ImportBatch { FileName = opt.FileName, Format = opt.Format, ImportedBy = opt.ImportedBy, TotalRows = rows.Count };
        var now = DateTimeOffset.UtcNow;
        var results = new List<ImportRowResult>();
        var seen = new HashSet<string>();
        var normalized = rows.Select(r => (Row: r, Res: PhoneNormalizer.Normalize(r.Phone))).ToList();
        var phones = normalized.Where(x => x.Res.IsValid).Select(x => x.Res.Normalized).Distinct().ToList();
        var existing = await db.Leads.Where(l => phones.Contains(l.Phone)).ToDictionaryAsync(l => l.Phone, ct);
        var blocked = (await db.OptOuts.Where(o => phones.Contains(o.Phone)).Select(o => o.Phone).ToListAsync(ct)).ToHashSet();
        var withConsent = 0;

        foreach (var (row, res) in normalized)
        {
            if (!res.IsValid) { batch.Invalid++; results.Add(new(row.LineNumber, row.Phone, null, ImportRowOutcome.Invalid, res.Error)); continue; }
            var phone = res.Normalized;
            if (!seen.Add(phone)) { batch.Duplicates++; results.Add(new(row.LineNumber, row.Phone, phone, ImportRowOutcome.DuplicateInFile, "Número repetido no arquivo")); continue; }

            var isBlocked = blocked.Contains(phone);
            var consent = ParseConsent(row, opt.FileName) ?? BatchConsent(opt);

            if (existing.TryGetValue(phone, out var lead))
            {
                if (opt.UpdateExisting)
                {
                    if (string.IsNullOrWhiteSpace(lead.Name) && !string.IsNullOrWhiteSpace(row.Name)) lead.Name = row.Name.Trim();
                    lead.City ??= Clean(row.City); lead.State ??= Clean(row.State)?.ToUpperInvariant();
                    if (!string.IsNullOrWhiteSpace(row.Notes)) lead.Notes = string.Join("\n", new[] { lead.Notes, row.Notes.Trim() }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    lead.Tags = MergeTags(lead.Tags, row.Tags, opt.DefaultTags);
                    lead.UpdatedAt = now;
                    if (consent is not null && !isBlocked && lead.ConsentStatus != ConsentStatus.Revogado) { ApplyConsent(lead, consent, opt.ImportedBy); withConsent++; }
                }
                batch.Updated++;
                results.Add(new(row.LineNumber, row.Phone, phone, ImportRowOutcome.Updated, "Contato já existia — dados complementados"));
                continue;
            }

            lead = new Lead
            {
                Name = Clean(row.Name) ?? "", Phone = phone, City = Clean(row.City), State = Clean(row.State)?.ToUpperInvariant(),
                Source = Clean(row.Source) ?? opt.DefaultSource, Notes = Clean(row.Notes), Tags = MergeTags("", row.Tags, opt.DefaultTags),
                ImportedAt = now, ImportBatchId = batch.Id,
            };
            if (isBlocked) { lead.ConsentStatus = ConsentStatus.Revogado; lead.Stage = KanbanStage.NaoTemInteresse; lead.Mode = ConversationMode.Encerrado; }
            else if (consent is not null) { ApplyConsent(lead, consent, opt.ImportedBy); withConsent++; }
            db.Leads.Add(lead);
            existing[phone] = lead;
            if (isBlocked) { batch.Blocked++; results.Add(new(row.LineNumber, row.Phone, phone, ImportRowOutcome.Blocked, "Número na lista de bloqueio — cadastrado sem envio")); }
            else { batch.Created++; results.Add(new(row.LineNumber, row.Phone, phone, ImportRowOutcome.Created, consent is null ? "Sem consentimento: ficará em pendências" : null)); }
        }
        db.ImportBatches.Add(batch);
        db.AuditLogs.Add(new AuditLog { Actor = opt.ImportedBy, Action = "leads.import", EntityType = "ImportBatch", EntityId = batch.Id.ToString(),
            Details = $"{opt.Format} {opt.FileName}: {batch.Created} novos, {batch.Updated} atualizados, {batch.Duplicates} duplicados, {batch.Invalid} inválidos, {batch.Blocked} bloqueados" });
        await db.SaveChangesAsync(ct);
        return new(batch.Id, rows.Count, batch.Created, batch.Updated, batch.Duplicates, batch.Invalid, batch.Blocked, withConsent, results);
    }

    private sealed record ParsedConsent(string Source, DateTimeOffset At);

    /// <summary>Consentimento só é aceito com resposta afirmativa E origem identificável (coluna ou arquivo).</summary>
    private static ParsedConsent? ParseConsent(RawLeadRow r, string fileName)
    {
        var v = PainClassifier.Strip((r.Consent ?? "").Trim().ToLowerInvariant());
        if (v is not ("sim" or "s" or "yes" or "y" or "true" or "1" or "x" or "autorizado" or "opt-in" or "optin")) return null;
        if (string.IsNullOrWhiteSpace(r.ConsentSource)) return null; // sem origem verificável → pendente
        var at = DateTimeOffset.TryParse(r.ConsentDate, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.AssumeUniversal, out var d) ? d : DateTimeOffset.UtcNow;
        return new(r.ConsentSource.Trim() + $" (importado de {fileName})", at);
    }

    /// <summary>
    /// Autorização declarada para a lista inteira (ex.: "clientes que pediram contato no Instagram").
    /// Fica registrada com a origem, o arquivo e quem importou.
    /// </summary>
    private static ParsedConsent? BatchConsent(Options opt) =>
        string.IsNullOrWhiteSpace(opt.BatchConsentSource) ? null
            : new(opt.BatchConsentSource.Trim() + $" (declarado por {opt.ImportedBy} ao importar {opt.FileName})", DateTimeOffset.UtcNow);

    private static void ApplyConsent(Lead lead, ParsedConsent c, string by)
    {
        lead.ConsentStatus = ConsentStatus.Valido; lead.ConsentAt = c.At; lead.ConsentSource = c.Source;
        lead.ConsentRecords.Add(new ConsentRecord { LeadId = lead.Id, Status = ConsentStatus.Valido, Source = c.Source, GrantedAt = c.At, RegisteredBy = by });
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static string MergeTags(params string?[] lists) =>
        string.Join(",", lists.Where(l => !string.IsNullOrWhiteSpace(l))
            .SelectMany(l => l!.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(t => t.ToLowerInvariant()).Distinct());

    /// <summary>Importa/atualiza lista de bloqueio. Leads existentes têm consentimento revogado e envios pendentes cancelados.</summary>
    public async Task<(int Added, int Invalid, int LeadsAffected)> ImportOptOutsAsync(IEnumerable<string> rawPhones, string reason, string source, string by, CancellationToken ct = default)
    {
        int added = 0, invalid = 0;
        var phones = new HashSet<string>();
        foreach (var raw in rawPhones)
        {
            var r = PhoneNormalizer.Normalize(raw);
            if (!r.IsValid) { invalid++; continue; }
            phones.Add(r.Normalized);
        }
        var already = (await db.OptOuts.Where(o => phones.Contains(o.Phone)).Select(o => o.Phone).ToListAsync(ct)).ToHashSet();
        foreach (var p in phones.Where(p => !already.Contains(p))) { db.OptOuts.Add(new OptOut { Phone = p, Reason = reason, Source = source }); added++; }
        var leads = await db.Leads.Where(l => phones.Contains(l.Phone)).ToListAsync(ct);
        foreach (var l in leads) RevokeConsent(db, l, source, by);
        await db.SaveChangesAsync(ct);
        return (added, invalid, leads.Count);
    }

    public static void RevokeConsent(CrmDbContext db, Lead l, string source, string by)
    {
        l.ConsentStatus = ConsentStatus.Revogado; l.ConsentSource = source; l.ConsentAt = DateTimeOffset.UtcNow;
        db.ConsentRecords.Add(new ConsentRecord { LeadId = l.Id, Status = ConsentStatus.Revogado, Source = source, GrantedAt = DateTimeOffset.UtcNow, RegisteredBy = by });
        foreach (var q in db.OutboundQueue.Where(q => q.LeadId == l.Id && q.Status == MessageStatus.NaFila))
        { q.Status = MessageStatus.Cancelada; q.LastError = "Descadastro"; }
    }
}
