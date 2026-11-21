using System.Globalization;
using System.Text;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Services;

/// <summary>Conhecimento comercial carregado para uma rodada (lido do banco a cada mensagem: alterações valem na hora).</summary>
public sealed record KbSnapshot(CommercialConfig Config, IReadOnlyList<KbEntry> Entries, IReadOnlyList<Plan> Plans, bool AttendanceAllowed, bool DispatchAllowed);

/// <summary>Análise comercial da última mensagem, usada no prompt, nas ferramentas e no Laboratório.</summary>
public sealed record KbAnalysis(SalesProfile Profile, CommercialTrigger Trigger, string? Offer, IReadOnlyList<KbMatch> Matches, IReadOnlyList<string> Products);

/// <summary>
/// Base de conhecimento Dominius Play separada do prompt do agente (v0.4.1): configuração comercial, 75 perguntas,
/// perfil do cliente e gatilhos comerciais. O agente de atendimento consulta a cada mensagem.
/// </summary>
public class KnowledgeBaseService(CrmDbContext db, TimeProvider clock)
{
    public const string FactProfile = "perfil_comercial";
    public const string FactProduct = "produto_em_discussao";
    public const string FactOffer = "oferta_apresentada";
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public async Task<CommercialConfig> ConfigAsync(CancellationToken ct = default) =>
        CommercialConfig.FromJson((await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKeys.CommercialConfig, ct))?.Value);

    public async Task<(bool Attendance, bool Dispatch)> AccessAsync(CancellationToken ct = default)
    {
        var v = (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKeys.KbAccess, ct))?.Value ?? "Atendimento";
        return (v.Contains("Atendimento", StringComparison.OrdinalIgnoreCase), v.Contains("Disparo", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<Plan>> ActivePlansAsync(CancellationToken ct = default) =>
        (await db.Plans.AsNoTracking().ToListAsync(ct)).Where(p => p.IsActiveAt(clock.GetUtcNow())).OrderBy(p => p.SortOrder).ToList();

    public async Task<KbSnapshot> LoadAsync(bool includeDrafts = false, CancellationToken ct = default)
    {
        var entries = await db.KbEntries.AsNoTracking().Where(e => e.Base == DominiusKnowledge.BaseName && (e.Status == KbStatus.Publicado || (includeDrafts && e.Status == KbStatus.Rascunho)))
            .OrderBy(e => e.Code).ToListAsync(ct);
        var (att, disp) = await AccessAsync(ct);
        return new KbSnapshot(await ConfigAsync(ct), entries, await ActivePlansAsync(ct), att, disp);
    }

    /// <summary>Produtos em discussão: plano de interesse do lead + último produto registrado na memória.</summary>
    public static List<string> ProductsOf(Lead lead)
    {
        var list = new List<string>();
        foreach (var f in lead.Facts.Where(f => f.Key == FactProduct).OrderBy(f => f.CreatedAt)) { list.Remove(f.Value); list.Add(f.Value); }
        if (!string.IsNullOrWhiteSpace(lead.InterestPlanCode)) { list.Remove(lead.InterestPlanCode); list.Add(lead.InterestPlanCode); }
        return list;
    }

    public static SalesProfile ProfileOf(Lead lead) =>
        lead.Facts.Where(f => f.Key == FactProfile).OrderBy(f => f.CreatedAt).Select(f => Enum.TryParse<SalesProfile>(f.Value, out var p) ? p : SalesProfile.Indefinido).LastOrDefault();

    public static bool OfferMade(Lead lead, CommercialTrigger t) => lead.Facts.Any(f => f.Key == FactOffer && f.Value == t.ToString());

    /// <summary>
    /// Analisa a última mensagem do cliente e grava na memória do lead (em memória; quem chama decide se salva):
    /// perfil comercial, produto em discussão e oferta apresentada (para não oferecer de novo).
    /// </summary>
    public static KbAnalysis Analyze(Lead lead, string? lastInbound, KbSnapshot kb, DateTimeOffset now)
    {
        var before = ProfileOf(lead);
        var profile = SalesProfiles.Detect(lastInbound, before);
        if (profile != before && profile != SalesProfile.Indefinido)
            lead.Facts.Add(new LeadFact { LeadId = lead.Id, Key = FactProfile, Value = profile.ToString(), Source = FactSource.InferidoPelaIa, CreatedAt = now });
        var trigger = CommercialTriggers.Detect(lastInbound);
        var products = ProductsOf(lead);
        if (CommercialTriggers.Product(trigger, kb.Config) is { } tp && products.LastOrDefault() != tp)
        {
            lead.Facts.Add(new LeadFact { LeadId = lead.Id, Key = FactProduct, Value = tp, Source = FactSource.InferidoPelaIa, CreatedAt = now });
            products.Remove(tp); products.Add(tp);
        }
        var matches = KnowledgeMatcher.Search(lastInbound, kb.Entries, products, take: 3);
        string? offer = null;
        // ofertas comerciais uma vez por conversa (sem insistir); avisos de teste/Pix/pagamento valem sempre
        var salesOffer = trigger is CommercialTrigger.OrcamentoBaixo or CommercialTrigger.AchouCaro or CommercialTrigger.AmigoInteressado or CommercialTrigger.JaPossuiPainel or CommercialTrigger.MarcaPropria;
        if (trigger != CommercialTrigger.Nenhum && (!salesOffer || !OfferMade(lead, trigger)))
        {
            offer = CommercialTriggers.OfferMessage(trigger, kb.Config, kb.Plans, OfferMade(lead, CommercialTrigger.AmigoInteressado));
            if (offer is not null && salesOffer)
                lead.Facts.Add(new LeadFact { LeadId = lead.Id, Key = FactOffer, Value = trigger.ToString(), Source = FactSource.InferidoPelaIa, CreatedAt = now });
        }
        return new KbAnalysis(profile, trigger, offer, matches, products);
    }

    /// <summary>Bloco do prompt com os fatos oficiais (prevalecem sobre o histórico e sobre artigos antigos).</summary>
    public static string Brief(KbSnapshot kb, KbAnalysis a)
    {
        var c = kb.Config;
        var sb = new StringBuilder();
        sb.AppendLine("\nBASE DE CONHECIMENTO DOMINIUS PLAY (fonte oficial, editada no painel; prevalece sobre a base antiga e sobre qualquer valor dito antes no histórico):");
        sb.AppendLine("CONFIGURAÇÃO COMERCIAL VIGENTE:");
        sb.AppendLine($"- Empresa: {c.CompanyName}, cerca de {c.YearsInMarket} anos de mercado (informação da empresa). Site: {c.Site}");
        sb.AppendLine($"- Teste: SOMENTE do serviço, por {c.TestHours} horas, em {c.TestUrl} (preenche o formulário, confirma e-mail e WhatsApp, recebe o acesso por e-mail). NÃO existe teste do painel de gerenciamento.");
        sb.AppendLine($"- Pagamento: {c.PaymentMethods}. Chave Pix: {c.PixKey}. Sempre peça para conferir os dados do recebedor no app do banco. Você NUNCA confirma pagamento nem diz que a contratação foi concluída: isso só o sistema financeiro/equipe confirma.");
        sb.AppendLine($"- Liberação do painel: aproximadamente {c.PanelReleaseMinutes} minutos após a confirmação do Pix (sem garantia se houver pendência técnica ou de validação). Franquia: até {c.FranchiseDeliveryHours} horas após pagamento e requisitos, sujeito à disponibilidade técnica.");
        sb.AppendLine($"- Contratação: {c.ContractRequirements}");
        sb.AppendLine($"- Aplicativos oficiais: app oficial código Downloader {c.AppCode} ({c.AppLink}); app oficial P2P código Downloader {c.P2pCode} ({c.P2pLink}); player web {c.PlayerUrl}. Smart TVs Samsung/LG antigas: possivelmente {c.SmartTvApps}; a DNS STB é informada pela equipe técnica (nunca invente endereço DNS).");
        sb.AppendLine(string.IsNullOrWhiteSpace(c.ActivePartnerApps)
            ? $"- Parceiros: buscando novas parcerias. {c.FormerPartnerApps} encerraram a locação para servidores: nunca apresente como parceiros ativos."
            : $"- Parceiros ativos: {c.ActivePartnerApps}. Encerraram a locação (não apresentar como ativos): {c.FormerPartnerApps}.");
        sb.AppendLine("- Regras de créditos: " + c.CreditRules.Replace("\n", " "));
        sb.AppendLine("- Subpainéis: " + c.SubpanelRules.Replace("\n", " "));
        sb.AppendLine(c.PostPaidEnabled
            ? $"- Modalidade pós-paga por cliente ativo (habilitada; não misture com créditos): {c.PostPaidTiers.Replace("\n", "; ")}; adesão {CreditMath.Money(c.PostPaidAdhesionFee)}."
            : "- Modalidade pós-paga por cliente ativo: NÃO está habilitada no momento; não ofereça (se perguntarem, diga que a equipe confirma a vigência).");
        if (!string.IsNullOrWhiteSpace(c.Promotions)) sb.AppendLine("- Ofertas promocionais vigentes: " + c.Promotions.Replace("\n", " "));
        sb.AppendLine("- Exemplos de margem são ilustrativos: margem bruta antes de despesas, nunca lucro líquido nem garantia.");
        sb.AppendLine($"PERFIL COMERCIAL: {SalesProfiles.Title(a.Profile)} — prioridades: {SalesProfiles.Guide(a.Profile)}");
        if (a.Products.Count > 0) sb.AppendLine("PRODUTO EM DISCUSSÃO: " + string.Join(", ", a.Products) + " (perguntas genéricas como \"quanto custa?\" ou \"qual o mais barato?\" referem-se a este produto).");
        if (a.Trigger != CommercialTrigger.Nenhum)
        {
            sb.AppendLine($"GATILHO COMERCIAL NA ÚLTIMA MENSAGEM: {CommercialTriggers.Title(a.Trigger)}.");
            if (a.Offer is not null) sb.AppendLine("Mensagem sugerida (adapte ao contexto, mantendo os fatos): " + a.Offer);
            else sb.AppendLine("A oferta deste gatilho já foi apresentada nesta conversa: não repita, responda à dúvida atual.");
        }
        if (a.Matches.Count > 0)
        {
            sb.AppendLine("RESPOSTAS OFICIAIS MAIS RELEVANTES PARA A ÚLTIMA MENSAGEM (adapte à conversa sem mudar valores, links, códigos e prazos):");
            foreach (var m in a.Matches)
            {
                var r = KnowledgeRenderer.Render(m.Entry.Answer, c, kb.Plans);
                sb.AppendLine($"- [{m.Entry.Code}] {m.Entry.Question} → {r.Text}" + (m.Entry.NeedsConfirmation ? " (depende de confirmação da equipe: não afirme além disso)" : ""));
            }
        }
        sb.AppendLine("Para outras perguntas use a ferramenta consultar_base_conhecimento. Não ofereça a divisão do painel com amigo em toda conversa: só quando o cliente citar amigo/sócio, custo alto ou sub-revendedores.");
        return sb.ToString();
    }

    public static string FormatMatches(IEnumerable<KbMatch> matches, KbSnapshot kb) =>
        System.Text.Json.JsonSerializer.Serialize(matches.Select(m => new
        {
            codigo = m.Entry.Code, categoria = m.Entry.Category, pergunta = m.Entry.Question, relevancia = Math.Round(m.Score, 2),
            resposta_oficial = KnowledgeRenderer.Render(m.Entry.Answer, kb.Config, kb.Plans).Text, precisa_confirmacao = m.Entry.NeedsConfirmation,
        }));
}
