using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Infrastructure.Data;

/// <summary>Carga inicial: tabela comercial da campanha (aprovada pelo administrador) e base de conhecimento do site (em revisão).</summary>
public static class SeedData
{
    public static async Task EnsureSeededAsync(CrmDbContext db, CancellationToken ct = default)
    {
        if (!await db.Plans.AnyAsync(ct))
        {
            db.Plans.AddRange(CampaignPlans());
            db.AuditLogs.Add(new AuditLog { Actor = "Sistema", Action = "plans.seed", EntityType = "Plan", Details = "Tabela da campanha 2027 carregada" });
        }
        if (!await db.KnowledgeArticles.AnyAsync(ct)) db.KnowledgeArticles.AddRange(SiteKnowledge());
        if (!await db.MessageTemplates.AnyAsync(ct)) db.MessageTemplates.AddRange(Templates());
        await db.SaveChangesAsync(ct);
        await EnsureCommercialAsync(db, ct);
    }

    /// <summary>Tabela comercial oficial (Prompt Mestre, novembro de 2026). Também usada para atualizar bancos antigos.</summary>
    public static IEnumerable<Plan> CampaignPlans()
    {
        var by = "Administrador (tabela oficial nov/2026)";
        const string credits = "Créditos acumulativos com mensalidade em dia. 1 crédito = 1 ativação de 30 dias; renovação consome outro crédito; 12 meses = 12 créditos. Créditos não são ilimitados.";
        var master = new CommercialConfig().MasterMinPrice;
        Plan M(string code, string name, decimal price, int c, int order, string profile, string extra = "") => new()
        {
            Code = code, Name = name, MonthlyPrice = price, Credits = c, SortOrder = order,
            Description = $"Plano mensalista com {c} créditos (custo de {CreditMath.Money(CreditMath.CostPerCredit(price, c))} por crédito). 1 crédito = 1 ativação de 30 dias." + extra,
            Benefits = $"{c} créditos por mês\nCréditos acumulativos com pagamento em dia\nPainel de gerenciamento pelo navegador"
                + (price >= master ? "\nPermissão Master (cria subpainéis para sub-revendedores)" : ""),
            Limits = credits, TargetProfile = profile, Approval = ApprovalStatus.Aprovado, ApprovedBy = by, IsCampaignPrice = true,
        };
        yield return M(PlanCodes.Mensalista10, "Mensalista 10", 30m, 10, 1, "Iniciante com investimento reduzido");
        yield return M(PlanCodes.Mensalista50, "Mensalista 50", 50m, 50, 2, "Iniciante que já quer mais créditos");
        yield return M(PlanCodes.Mensalista100, "Mensalista 100", 80m, 100, 3, "Revendedores em início ou expansão");
        yield return M(PlanCodes.Mensalista200, "Mensalista 200", 100m, 200, 4, "Revendedores em expansão",
            " Já sai com permissão Master: permite criar subpainéis (os créditos dos sub-revendedores saem do saldo principal).");
        yield return M(PlanCodes.Mensalista500, "Mensalista 500", 200m, 500, 5, "Operação maior ou painel Master dividido entre amigos",
            " Já sai com permissão Master: permite criar subpainéis (os créditos dos sub-revendedores saem do saldo principal).");
        yield return new Plan
        {
            Code = PlanCodes.Apontamento600, Name = "Apontamento Sigma 600", MonthlyPrice = 250m, MaxActiveLogins = 600, SortOrder = 6,
            Description = "Apontamento em painel compatível (ex.: Sigma): o cliente mantém o próprio painel e usa a infraestrutura da Dominius Play.",
            Benefits = "Até 600 logins ativos\nDNS exclusiva conforme condições técnicas\nContinua usando o próprio painel compatível",
            TechnicalRequirements = "Confirmar compatibilidade técnica do painel antes da contratação.",
            TargetProfile = "Revendedor com painel próprio até 600 ativos", Approval = ApprovalStatus.Aprovado, ApprovedBy = by, IsCampaignPrice = true,
        };
        yield return new Plan
        {
            Code = PlanCodes.ApontamentoIlimitado, Name = "Apontamento Ilimitado", MonthlyPrice = 500m, MaxActiveLogins = null, SortOrder = 7,
            Description = "Apontamento sem limite contratual de ativos, para operações de maior volume.",
            Benefits = "Sem limite contratual de ativos",
            Limits = "Não é capacidade técnica ilimitada de conexões simultâneas: condições de uso e capacidade devem ser verificadas pela equipe.",
            TargetProfile = "Revendedor com painel próprio acima de 600 ativos", Approval = ApprovalStatus.Aprovado, ApprovedBy = by, IsCampaignPrice = true,
        };
        yield return new Plan
        {
            Code = PlanCodes.Franquia, Name = "Franquia Completa", MonthlyPrice = 650m, SortOrder = 8,
            Description = "Operação sob a marca comercial do cliente.",
            Benefits = "Painel de gerenciamento\nDNS exclusiva\nAplicativo personalizado com a marca do cliente\nEstrutura comercial personalizada",
            Limits = "Personalização e controle administrativo não significam propriedade dos servidores nem direitos de distribuição de conteúdos.",
            TechnicalRequirements = "Entrega prevista em até 4 horas após a confirmação do pagamento e dos requisitos, sujeita à disponibilidade técnica e à validação da equipe.",
            TargetProfile = "Cliente que quer marca própria", Approval = ApprovalStatus.Aprovado, ApprovedBy = by, IsCampaignPrice = true,
        };
    }

    public const string DefaultPixSentence = "Nunca gere cobrança nem passe chave Pix.";
    public const string NewPixSentence = "Pagamento: informe a chave Pix da CONFIGURAÇÃO COMERCIAL e peça para conferir o recebedor no app do banco; nunca diga que o pagamento foi recebido (quem confirma é o sistema financeiro/equipe).";

    /// <summary>
    /// Base Dominius Play (75 perguntas), configuração comercial e tabela oficial de nov/2026 em bancos novos ou antigos.
    /// Aditivo e idempotente: nunca apaga contatos, campanhas, agentes nem entradas editadas pelo administrador.
    /// </summary>
    public static async Task<IReadOnlyList<string>> EnsureCommercialAsync(CrmDbContext db, CancellationToken ct = default)
    {
        var changes = new List<string>();
        async Task<AppSetting?> S(string k) => await db.Settings.FirstOrDefaultAsync(x => x.Key == k, ct);
        if (await S(SettingKeys.CommercialConfig) is null)
        {
            db.Settings.Add(new AppSetting { Key = SettingKeys.CommercialConfig, Value = new CommercialConfig().ToJson() });
            changes.Add("configuração comercial");
        }
        if (await S(SettingKeys.KbAccess) is null) db.Settings.Add(new AppSetting { Key = SettingKeys.KbAccess, Value = "Atendimento,Disparo" });
        if (!await db.KbEntries.AnyAsync(e => e.Base == DominiusKnowledge.BaseName, ct))
        {
            foreach (var e in DominiusKnowledge.Entries())
            {
                db.KbEntries.Add(e);
                db.KbEntryRevisions.Add(KbHistory.Revision(e, "criada", e.UpdatedBy));
            }
            db.AuditLogs.Add(new AuditLog { Actor = "Sistema", Action = "kb.seed", EntityType = "KbEntry", Details = "Base Dominius Play: 75 perguntas e respostas" });
            changes.Add("base Dominius Play (75)");
        }
        await db.SaveChangesAsync(ct);

        if (await S(SettingKeys.CommercialTable202611) is null)
        {
            var all = await db.Plans.ToListAsync(ct);
            var now = DateTimeOffset.UtcNow;
            foreach (var official in CampaignPlans())
            {
                var current = all.Where(p => p.Code == official.Code && p.Approval == ApprovalStatus.Aprovado).OrderByDescending(p => p.Version).FirstOrDefault();
                if (current is not null && current.MonthlyPrice == official.MonthlyPrice && current.Credits == official.Credits && current.MaxActiveLogins == official.MaxActiveLogins) continue;
                foreach (var old in all.Where(p => p.Code == official.Code && p.Approval == ApprovalStatus.Aprovado)) { old.Approval = ApprovalStatus.Arquivado; old.UpdatedAt = now; }
                official.Version = all.Where(p => p.Code == official.Code).Select(p => p.Version).DefaultIfEmpty(0).Max() + 1;
                db.Plans.Add(official);
                changes.Add($"plano {official.Name} v{official.Version}");
            }
            foreach (var agent in await db.AgentProfiles.Where(a => a.AllowedAnswers.Contains(DefaultPixSentence)).ToListAsync(ct))
            { agent.AllowedAnswers = agent.AllowedAnswers.Replace(DefaultPixSentence, NewPixSentence); changes.Add($"agente {agent.Name}: regra do Pix"); }
            db.Settings.Add(new AppSetting { Key = SettingKeys.CommercialTable202611, Value = "true" });
            if (changes.Count > 0)
                db.AuditLogs.Add(new AuditLog { Actor = "Sistema", Action = "commercial.upgrade", EntityType = "Plan", Details = "Tabela oficial nov/2026: " + string.Join(", ", changes) });
            await db.SaveChangesAsync(ct);
        }

        if (await S(SettingKeys.CommercialAnswers20261121) is null)
        {
            var done = new List<string>();
            var now = DateTimeOffset.UtcNow;
            // 1. Mensalista 50 continua à venda (a primeira 0.4.1 arquivava): volta como versão aprovada, histórico preservado
            var m50 = CampaignPlans().Single(p => p.Code == PlanCodes.Mensalista50);
            var plans50 = await db.Plans.Where(p => p.Code == PlanCodes.Mensalista50).ToListAsync(ct);
            if (!plans50.Any(p => p.Approval == ApprovalStatus.Aprovado))
            {
                m50.Version = plans50.Select(p => p.Version).DefaultIfEmpty(0).Max() + 1;
                db.Plans.Add(m50);
                done.Add($"Mensalista 50 ativo (v{m50.Version})");
            }
            // 2. pós-pago confirmado (R$ 60 de adesão + R$ 5 por login ativo); valores editados pelo administrador são mantidos
            var cfgRow = await S(SettingKeys.CommercialConfig);
            if (cfgRow is not null)
            {
                var cfg = CommercialConfig.FromJson(cfgRow.Value);
                var tiers = cfg.PostPaidTiers.Trim() == CommercialConfig.LegacyPostPaidTiers ? CommercialConfig.DefaultPostPaidTiers : cfg.PostPaidTiers;
                var subpanel = cfg.SubpanelRules.Contains("permissão Master.") && !cfg.SubpanelRules.Contains("R$ 100")
                    ? new CommercialConfig().SubpanelRules : cfg.SubpanelRules;
                var upd = cfg with { PostPaidEnabled = true, PostPaidTiers = tiers, SubpanelRules = subpanel };
                if (upd != cfg) { cfgRow.Value = upd.ToJson(); done.Add("pós-pago ligado (adesão + R$ 5,00 por login ativo), Master a partir de R$ 100,00"); }
            }
            // 3. agente de disparo também consulta a base
            var acc = await S(SettingKeys.KbAccess);
            if (acc is not null && !acc.Value.Contains("Disparo", StringComparison.OrdinalIgnoreCase))
            { acc.Value = string.IsNullOrWhiteSpace(acc.Value) ? "Atendimento,Disparo" : acc.Value + ",Disparo"; done.Add("agente de disparo consulta a base"); }
            // 4. respostas reescritas, só onde o administrador ainda não editou
            var codes = DominiusKnowledge.RevisedOn20261121.Select(DominiusKnowledge.Code).ToList();
            var seeds = DominiusKnowledge.Entries().Where(e => codes.Contains(e.Code)).ToDictionary(e => e.Code);
            foreach (var e in await db.KbEntries.Where(e => e.Base == DominiusKnowledge.BaseName && codes.Contains(e.Code)).ToListAsync(ct))
            {
                if (e.UpdatedBy != DominiusKnowledge.SeedAuthor || e.Version != 1) continue;
                var seed = seeds[e.Code];
                if (e.Answer == seed.Answer && e.Products == seed.Products && e.NeedsConfirmation == seed.NeedsConfirmation) continue;
                e.Answer = seed.Answer; e.Products = seed.Products; e.Keywords = seed.Keywords; e.NeedsConfirmation = seed.NeedsConfirmation;
                e.Version++; e.UpdatedAt = now;
                db.KbEntryRevisions.Add(KbHistory.Revision(e, "editada", "Sistema (confirmação comercial 21/11/2026)"));
                done.Add($"resposta {e.Code}");
            }
            db.Settings.Add(new AppSetting { Key = SettingKeys.CommercialAnswers20261121, Value = "true" });
            if (done.Count > 0)
                db.AuditLogs.Add(new AuditLog { Actor = "Sistema", Action = "commercial.upgrade", EntityType = "Plan", Details = "Confirmações comerciais de 21/11/2026: " + string.Join(", ", done) });
            await db.SaveChangesAsync(ct);
            changes.AddRange(done);
        }
        return changes;
    }

    private static IEnumerable<KnowledgeArticle> SiteKnowledge()
    {
        const string site = "https://dominiusplay.top/";
        KnowledgeArticle A(string cat, string title, string content, string url = site) =>
            new() { Category = cat, Title = title, Content = content, SourceUrl = url, Approval = ApprovalStatus.EmRevisao };

        yield return A("Empresa", "Quem é a Dominius Play",
            "Plataforma de serviços IPTV voltada a revendedores, com aproximadamente dez anos de experiência declarada no mercado. A empresa informa possuir plataforma e estrutura de servidores próprias. (Informação comercial fornecida pela empresa.)");
        yield return A("Contato", "Canais e horário",
            "WhatsApp: (14) 98815-9045. E-mail: vendas@dominiusplay.top. Horário publicado no site: segunda a sábado, das 9h às 21h.");
        yield return A("Créditos", "O que é um crédito",
            "Pela regra padrão publicada, 1 crédito ativa 1 cliente por 30 dias; cada renovação consome outro crédito.", site);
        yield return A("Aplicativos", "Aplicativos disponíveis",
            "Dominius Play (Android TV/TV Box/Fire TV/celular Android, requer Android 5.0+); Dominius Play P2P EXPRESS (Android); Dominius Play Web (player no navegador de Smart TV, computador e celular); app parceiro disponível em lojas de Smart TV. Celular e TV Box: app; Smart TV: player web ou app parceiro.",
            site + "aplicativos");
        yield return A("Teste", "Como funciona o teste",
            "O interessado preenche nome, e-mail e WhatsApp e confirma o e-mail pelo link recebido; o teste é gerado automaticamente e os dados de acesso vão para o e-mail confirmado. Duração e limites do teste: NÃO publicados — confirmar com o administrador.",
            site + "teste");
        yield return A("Revenda", "Sub-revendas",
            "Criação de sub-revendas depende do plano. ATENÇÃO: o site tem informação divergente sobre qual plano libera sub-revendas — confirmar com o administrador antes de afirmar.");
        yield return A("Conflitos", "⚠ Preços do site diferentes da campanha",
            "O site publica mensalistas Start (100 créditos, R$80), Pro (200, R$100) e Premium (500, R$200); apontamentos 1000 (R$250), 3000 (R$1.000) e Ilimitado (R$1.400); franquias 1000 (R$850), 3000 (R$1.300) e Ilimitada (R$1.600); Painel Ilimitado (R$500). A campanha atual aprovada usa outra tabela (Mensalista 50/100, Apontamento Sigma 600, Apontamento Ilimitado, Franquia Completa). REGRA: a IA usa somente a tabela comercial aprovada no módulo Planos.");
    }

    private static IEnumerable<MessageTemplate> Templates()
    {
        string[] bodies =
        [
            "Olá{, nome}, tudo bem? Sou {assistente}, assistente virtual da Dominius Play. Você demonstrou interesse em conhecer soluções para revenda. Posso fazer uma pergunta rápida para entender melhor o que procura?",
            "Oi{, nome}! Aqui é {assistente}, assistente virtual da Dominius Play. Estamos entrando em contato sobre seu interesse em soluções para revendedores. Você já trabalha com serviços IPTV ou está conhecendo esse mercado agora?",
            "Olá{, nome}! Tudo certo? Sou {assistente}, assistente virtual da Dominius Play. Gostaria de entender qual é sua principal necessidade hoje: iniciar uma operação, melhorar a estrutura atual ou trabalhar com sua própria marca?",
        ];
        var i = 0;
        foreach (var b in bodies)
        {
            var t = new MessageTemplate { Name = $"Abordagem inicial {++i}", Purpose = "Abordagem inicial" };
            t.Versions.Add(new MessageTemplateVersion { TemplateId = t.Id, Version = 1, Body = b, Approval = ApprovalStatus.EmRevisao });
            yield return t;
        }
    }
}
