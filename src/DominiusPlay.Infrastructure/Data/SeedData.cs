using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
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
    }

    public static IEnumerable<Plan> CampaignPlans()
    {
        var by = "Administrador (campanha)";
        yield return new Plan
        {
            Code = PlanRecommender.Mensalista50, Name = "Mensalista 50", MonthlyPrice = 50m, Credits = 50, SortOrder = 1,
            Description = "Plano mensal por créditos. 1 crédito corresponde a uma ativação de 30 dias, conforme a regra comercial.",
            Benefits = "50 créditos por mês\nOperação por créditos",
            Limits = "Confirmar no cadastro se os créditos são acumulativos e quais permissões estão incluídas.",
            TargetProfile = "Iniciante com investimento reduzido", Approval = ApprovalStatus.Aprovado, ApprovedBy = by, IsCampaignPrice = true,
        };
        yield return new Plan
        {
            Code = PlanRecommender.Mensalista100, Name = "Mensalista 100", MonthlyPrice = 80m, Credits = 100, SortOrder = 2,
            Description = "Plano mensal com 100 créditos. Condições de créditos conforme cadastro comercial.",
            Benefits = "100 créditos por mês", Limits = "Condições de créditos conforme cadastro comercial.",
            TargetProfile = "Revendedores em início ou expansão", Approval = ApprovalStatus.Aprovado, ApprovedBy = by, IsCampaignPrice = true,
        };
        yield return new Plan
        {
            Code = PlanRecommender.Sigma600, Name = "Apontamento Sigma 600", MonthlyPrice = 250m, MaxActiveLogins = 600, SortOrder = 3,
            Description = "O cliente continua utilizando seu Painel Sigma, com a infraestrutura de servidores da Dominius Play.",
            Benefits = "Até 600 logins ativos\nDNS exclusiva incluída\nContinua usando o próprio Painel Sigma",
            TechnicalRequirements = "Confirmar requisitos técnicos de integração antes da contratação.",
            TargetProfile = "Revendedor com Painel Sigma até 600 ativos", Approval = ApprovalStatus.Aprovado, ApprovedBy = by, IsCampaignPrice = true,
        };
        yield return new Plan
        {
            Code = PlanRecommender.ApontamentoIlimitado, Name = "Apontamento Ilimitado", MonthlyPrice = 500m, MaxActiveLogins = null, SortOrder = 4,
            Description = "Sem limite contratado de ativos, destinado a operações de maior volume.",
            Benefits = "Sem limite contratado de ativos",
            Limits = "Os limites físicos de infraestrutura, políticas de uso e condições técnicas continuam valendo e devem ser informados.",
            TargetProfile = "Revendedor Sigma acima de 600 ativos", Approval = ApprovalStatus.Aprovado, ApprovedBy = by, IsCampaignPrice = true,
        };
        yield return new Plan
        {
            Code = PlanRecommender.Franquia, Name = "Franquia Completa", MonthlyPrice = 650m, SortOrder = 5,
            Description = "Operação sob a marca comercial do cliente.",
            Benefits = "Painel administrativo exclusivo\nDNS exclusiva\nAplicativo com marca personalizada\nIdentidade visual própria\nCréditos ilimitados conforme condições contratuais",
            Limits = "Personalização e controle administrativo não significam propriedade dos servidores nem direitos de distribuição de conteúdos.",
            TechnicalRequirements = "Prazo estimado de entrega de até 4 horas após confirmação dos requisitos, quando tecnicamente viável (não prometer de forma incondicional).",
            TargetProfile = "Cliente que quer marca própria", Approval = ApprovalStatus.Aprovado, ApprovedBy = by, IsCampaignPrice = true,
        };
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
