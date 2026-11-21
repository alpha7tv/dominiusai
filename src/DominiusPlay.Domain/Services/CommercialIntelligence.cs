using System.Text.RegularExpressions;
using DominiusPlay.Domain.Entities;

namespace DominiusPlay.Domain.Services;

/// <summary>Perfis comerciais do Prompt Mestre (parte 8).</summary>
public enum SalesProfile { Indefinido = 0, Iniciante = 1, RevendedorExperiente = 2, Franquia = 3, Amigo = 4, Provedor = 5 }

/// <summary>Gatilhos comerciais contextuais (parte 9) e sinais de atendimento.</summary>
public enum CommercialTrigger
{
    Nenhum = 0, OrcamentoBaixo, AchouCaro, AmigoInteressado, JaPossuiPainel, MarcaPropria, QuerTestar, QuerTestarPainel,
    QuerContratar, PedePix, InformaPagamento, PerguntaAplicativo, SubRevendedor,
}

public static class SalesProfiles
{
    private static readonly (SalesProfile P, string[] Phrases)[] Rules =
    [
        (SalesProfile.Provedor, ["provedor", "provedor de internet", "sou provedor", "tenho um provedor", "isp", "fibra", "assinantes de internet"]),
        (SalesProfile.Amigo, ["meu amigo", "minha amiga", "um amigo", "com um amigo", "meu socio", "minha socia", "um socio", "dividir o painel", "dividir com", "rachar"]),
        (SalesProfile.Franquia, ["minha marca", "marca propria", "meu proprio aplicativo", "meu proprio app", "app com minha", "aplicativo com minha", "franquia", "minha logo", "identidade visual"]),
        // (franquia só define o perfil quando não é uma comparação entre modalidades — ver Detect)
        (SalesProfile.RevendedorExperiente, ["ja trabalho", "ja revendo", "ja tenho painel", "painel proprio", "tenho meu painel", "uso meu painel", "tenho clientes", "minha carteira", "sigma", "ja sou revendedor", "trabalho com iptv", "tenho revenda"]),
        (SalesProfile.Iniciante, ["comecar", "comecando", "iniciante", "nunca trabalhei", "nao conheco", "quero revender", "sou novo", "primeira vez", "pouco dinheiro"]),
    ];

    /// <summary>Atualiza o perfil com a mensagem. Perfis específicos (provedor, amigo, franquia) prevalecem sobre os genéricos.</summary>
    public static SalesProfile Detect(string? text, SalesProfile current = SalesProfile.Indefinido)
    {
        var t = " " + KnowledgeMatcher.Normalize(text) + " ";
        var comparing = t.Contains(" diferenca ") || t.Contains(" mensalista ") || t.Contains(" apontamento ");
        foreach (var (p, phrases) in Rules)
            if (phrases.Any(ph => t.Contains(" " + ph + " ") || (ph.Length > 5 && t.Contains(" " + ph))))
            {
                if (p == SalesProfile.Franquia && comparing) continue;
                // genérico não rebaixa um perfil específico já identificado
                if (current is SalesProfile.Provedor or SalesProfile.Amigo or SalesProfile.Franquia && p is SalesProfile.Iniciante or SalesProfile.RevendedorExperiente) return current;
                return p;
            }
        return current;
    }

    public static string Title(SalesProfile p) => p switch
    {
        SalesProfile.Iniciante => "Iniciante",
        SalesProfile.RevendedorExperiente => "Revendedor experiente",
        SalesProfile.Franquia => "Interessado em franquia",
        SalesProfile.Amigo => "Quer começar com um amigo",
        SalesProfile.Provedor => "Provedor de internet",
        _ => "Ainda não identificado",
    };

    /// <summary>Prioridades de atendimento por perfil (vão no prompt e no Laboratório).</summary>
    public static string Guide(SalesProfile p) => p switch
    {
        SalesProfile.Iniciante => "explique como funciona a revenda, o investimento inicial (plano de entrada), os créditos e o painel; oriente o teste do serviço; encaminhe para contratação quando houver interesse.",
        SalesProfile.RevendedorExperiente => "entenda as dificuldades atuais e a quantidade de clientes; apresente a modalidade adequada (apontamento quando já tem painel); esclareça estrutura técnica e condições; ofereça atendimento comercial.",
        SalesProfile.Franquia => "entenda se já tem marca e se precisa de aplicativo; explique painel personalizado, DNS exclusiva e valores; confirme os requisitos; encaminhe à equipe de implantação.",
        SalesProfile.Amigo => "explique o painel Master, o plano de 500 créditos, a divisão em 250 créditos por pessoa e o rateio combinado entre eles; o titular administra o painel principal e paga a mensalidade integral; créditos acumulam com pagamento em dia.",
        SalesProfile.Provedor => "entenda os serviços que já oferece e a necessidade de agregar entretenimento; apresente somente soluções com direitos comerciais adequados; explique gerenciamento, personalização e suporte; encaminhe para proposta conforme o porte.",
        _ => "descubra se a pessoa já trabalha com revenda ou está começando, sem pressionar.",
    };
}

public static class CommercialTriggers
{
    private static readonly (CommercialTrigger T, string[] Phrases)[] Rules =
    [
        (CommercialTrigger.InformaPagamento, ["ja paguei", "acabei de pagar", "fiz o pix", "paguei o pix", "mandei o pix", "transferi", "segue o comprovante", "comprovante", "voces receberam", "caiu o pix", "caiu ai"]),
        (CommercialTrigger.PedePix, ["chave pix", "me passa o pix", "manda o pix", "qual o pix", "qual e o pix", "passa o pix", "pix de voces", "dados para pagamento", "como pago", "como faco para pagar"]),
        (CommercialTrigger.QuerTestarPainel, ["testar o painel", "teste do painel", "painel de teste", "painel para teste", "painel teste", "testar painel"]),
        (CommercialTrigger.QuerTestar, ["teste", "testar", "experimentar", "liberar um teste", "teste gratis"]),
        (CommercialTrigger.AmigoInteressado, ["meu amigo", "minha amiga", "um amigo", "meu socio", "um socio", "dividir o painel", "dividir com", "rachar"]),
        (CommercialTrigger.MarcaPropria, ["minha marca", "marca propria", "meu proprio aplicativo", "meu proprio app", "aplicativo com minha", "app com minha", "minha logo"]),
        (CommercialTrigger.JaPossuiPainel, ["ja trabalho com painel", "painel proprio", "ja tenho painel", "tenho meu painel", "uso meu painel", "uso o sigma", "tenho painel sigma", "trabalho com sigma"]),
        (CommercialTrigger.OrcamentoBaixo, ["pouco dinheiro", "sem dinheiro", "nao tenho muito", "investimento pequeno", "orcamento curto", "orcamento baixo", "grana curta", "comecar com pouco"]),
        (CommercialTrigger.AchouCaro, ["caro", "muito caro", "achei caro", "ta caro", "esta caro", "puxado", "salgado", "fora do meu orcamento"]),
        (CommercialTrigger.QuerContratar, ["quero contratar", "vou contratar", "quero fechar", "vamos fechar", "bora fechar", "quero assinar", "quero comprar", "quero o plano", "pode fechar"]),
        (CommercialTrigger.SubRevendedor, ["sub revendedor", "sub revendedores", "subrevendedor", "sub revenda", "subpainel", "sub painel", "painel master", "master"]),
        (CommercialTrigger.PerguntaAplicativo, ["aplicativo", "aplicativos", "app", "apps", "downloader", "playsim", "assist", "magic player", "player"]),
    ];

    public static CommercialTrigger Detect(string? text)
    {
        var t = " " + KnowledgeMatcher.Normalize(text) + " ";
        foreach (var (trig, phrases) in Rules)
            if (phrases.Any(p => t.Contains(" " + p + " "))) return trig;
        return CommercialTrigger.Nenhum;
    }

    public static string Title(CommercialTrigger t) => t switch
    {
        CommercialTrigger.OrcamentoBaixo => "Orçamento baixo",
        CommercialTrigger.AchouCaro => "Achou caro",
        CommercialTrigger.AmigoInteressado => "Tem um amigo interessado",
        CommercialTrigger.JaPossuiPainel => "Já possui painel",
        CommercialTrigger.MarcaPropria => "Quer marca própria",
        CommercialTrigger.QuerTestar => "Quer testar antes de comprar",
        CommercialTrigger.QuerTestarPainel => "Pediu teste do painel",
        CommercialTrigger.QuerContratar => "Quer contratar",
        CommercialTrigger.PedePix => "Pediu a chave Pix",
        CommercialTrigger.InformaPagamento => "Diz que já pagou",
        CommercialTrigger.PerguntaAplicativo => "Pergunta sobre aplicativos",
        CommercialTrigger.SubRevendedor => "Sub-revendedor / painel Master",
        _ => "Nenhum",
    };

    /// <summary>Produto que o gatilho coloca em discussão.</summary>
    public static string? Product(CommercialTrigger t, CommercialConfig cfg) => t switch
    {
        CommercialTrigger.OrcamentoBaixo => PlanCodes.Mensalista10,
        CommercialTrigger.AmigoInteressado or CommercialTrigger.SubRevendedor => cfg.FriendSplitPlanCode,
        CommercialTrigger.JaPossuiPainel => PlanCodes.Apontamento600,
        CommercialTrigger.MarcaPropria => PlanCodes.Franquia,
        _ => null,
    };

    /// <summary>
    /// Mensagem sugerida para o gatilho (parte 9), com valores da tabela vigente. Nulo quando o gatilho não tem oferta
    /// ou quando o plano necessário não está aprovado — nesse caso a resposta vem da base.
    /// </summary>
    public static string? OfferMessage(CommercialTrigger t, CommercialConfig cfg, IReadOnlyList<Plan> plans, bool friendOfferAlreadyMade = false)
    {
        Plan? P(string code) => plans.FirstOrDefault(p => p.Code == code);
        switch (t)
        {
            case CommercialTrigger.OrcamentoBaixo:
                return P(PlanCodes.Mensalista10) is { Credits: { } c } p10
                    ? $"Temos um plano inicial de apenas {CreditMath.Money(p10.MonthlyPrice)} mensais, com {c} créditos. Assim você pode começar com um investimento pequeno e ampliar conforme conquistar clientes. Quer conhecer como funcionam os créditos?"
                    : null;
            case CommercialTrigger.AchouCaro:
            {
                // primeiro o plano de entrada; a divisão com amigo só quando fizer sentido (não oferecer compulsivamente)
                var start = P(PlanCodes.Mensalista10);
                if (start is { Credits: { } sc })
                    return $"Entendo! Para começar com pouco, temos o plano inicial de {CreditMath.Money(start.MonthlyPrice)} mensais com {sc} créditos, e você amplia conforme conquistar clientes. Se tiver alguém para começar junto, também dá para dividir um painel maior. Quer que eu explique?";
                return null;
            }
            case CommercialTrigger.AmigoInteressado:
            {
                if (!cfg.FriendSplitEnabled || friendOfferAlreadyMade) return null;
                if (P(cfg.FriendSplitPlanCode) is not { Credits: { } fc } fp) return null;
                var split = CreditMath.Split(fc, fp.MonthlyPrice);
                return $"Uma opção interessante é contratar um painel Master de {fc} créditos por {CreditMath.Money(fp.MonthlyPrice)} mensais e dividir {split.CreditsEach} créditos para cada um. Assim vocês podem combinar o rateio de {CreditMath.Money(split.AmountEach)} por pessoa e administrar seus clientes separadamente. O titular fica responsável pelo pagamento integral e pela distribuição dos créditos.";
            }
            case CommercialTrigger.JaPossuiPainel:
                return P(PlanCodes.Apontamento600) is { } ap
                    ? $"Entendi! Nesse caso, podemos avaliar nossa modalidade de apontamento, que começa em {CreditMath.Money(ap.MonthlyPrice)} mensais para até {ap.MaxActiveLogins} logins ativos, com DNS exclusiva. Qual painel você utiliza atualmente?"
                    : null;
            case CommercialTrigger.MarcaPropria:
                return P(PlanCodes.Franquia) is { } fr
                    ? $"Temos uma franquia completa por {CreditMath.Money(fr.MonthlyPrice)} mensais, que inclui painel, DNS exclusiva e aplicativo personalizado com sua marca. Você já possui uma identidade visual definida?"
                    : null;
            case CommercialTrigger.QuerTestar:
            case CommercialTrigger.QuerTestarPainel:
                return (t == CommercialTrigger.QuerTestarPainel ? "Não disponibilizamos teste do painel de gerenciamento, mas você pode testar o serviço. " : "")
                    + $"Você pode solicitar um teste de {cfg.TestHours} horas do serviço em {cfg.TestUrl}. Basta preencher o formulário e confirmar seu e-mail e WhatsApp; os dados de acesso chegam no seu e-mail."
                    + (t == CommercialTrigger.QuerTestar ? " Não disponibilizamos teste do painel de gerenciamento." : "");
            case CommercialTrigger.PedePix:
                return $"Nossa forma de pagamento é Pix, pela chave {cfg.PixKey}. Antes de pagar, confira os dados do recebedor no seu aplicativo bancário. Assim que o pagamento for confirmado pelo sistema, seu painel normalmente fica disponível em aproximadamente {cfg.PanelReleaseMinutes} minutos.";
            case CommercialTrigger.InformaPagamento:
                return "Obrigado por avisar! Eu não consigo confirmar pagamentos por aqui. Vou pedir para a equipe conferir no sistema financeiro e, assim que for confirmado, você recebe as informações de acesso.";
        }
        return null;
    }
}

/// <summary>
/// Conferência da resposta da IA antes do envio (testes 17, 18, 24, 26, 27, 28, 32 da parte 13): bloqueia
/// confirmação financeira inventada, lucro garantido, teste do painel, parceiros antigos como ativos, DNS inventada,
/// autorização de conteúdo inventada, créditos ilimitados e chave Pix diferente da configurada.
/// </summary>
public static class ReplySafetyGuard
{
    private static readonly Regex Ip = new(@"\b\d{1,3}(\.\d{1,3}){3}\b", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex Host = new(@"\b(?:[a-z0-9-]+\.)+[a-z]{2,}(?::\d+)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static bool Affirms(string norm, string phrase)
    {
        var idx = 0;
        while ((idx = norm.IndexOf(" " + phrase + " ", idx, StringComparison.Ordinal)) >= 0)
        {
            var before = norm[Math.Max(0, idx - 40)..idx];
            if (!(before.Contains(" nao ") || before.Contains(" sem ") || before.Contains(" nenhum") || before.Contains(" nunca ") || before.Contains(" nem "))) return true;
            idx += phrase.Length;
        }
        return false;
    }

    public static IReadOnlyList<string> Check(string? reply, CommercialConfig cfg)
    {
        var v = new List<string>();
        if (string.IsNullOrWhiteSpace(reply)) return v;
        var n = " " + KnowledgeMatcher.Normalize(reply) + " ";

        if (new[] { "pagamento confirmado", "pagamento foi confirmado", "recebi seu pix", "recebemos seu pix", "recebemos seu pagamento", "recebi seu pagamento", "pix confirmado",
                "pix foi confirmado", "pagamento aprovado", "ja esta pago", "contratacao concluida", "contratacao foi concluida", "seu painel ja esta ativo", "pagamento identificado" }.Any(p => Affirms(n, p)))
            v.Add("Confirmação financeira sem consulta ao sistema financeiro");
        if (new[] { "lucro garantido", "lucros garantidos", "ganho garantido", "ganhos garantidos", "renda garantida", "garanto que voce vai lucrar", "garantimos o lucro", "lucro liquido garantido" }.Any(p => Affirms(n, p)))
            v.Add("Promessa de lucro/ganho garantido");
        if (new[] { "teste do painel", "painel de teste", "painel para teste", "testar o painel", "painel teste" }.Any(p => Affirms(n, p)))
            v.Add("Ofereceu teste do painel de gerenciamento (só existe teste do serviço)");
        var former = cfg.FormerPartnerApps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(KnowledgeMatcher.Normalize).Where(x => x.Length > 0);
        if (former.Any(f => n.Contains(" " + f + " ")) && !(n.Contains(" encerr") || n.Contains(" nao trabalhamos mais") || n.Contains(" anteriormente") || n.Contains(" buscando novas parcerias")))
            v.Add("Apresentou parceiro antigo como ativo");
        if (Ip.IsMatch(reply)) v.Add("Endereço DNS/IP informado sem orientação técnica");
        else if (n.Contains(" dns "))
        {
            var allowed = new[] { cfg.Site, cfg.TestUrl, cfg.PlayerUrl, cfg.AppLink, cfg.P2pLink }.Select(u => Uri.TryCreate(u, UriKind.Absolute, out var x) ? x.Host : "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Host.Matches(reply).Select(m => m.Value.Split(':')[0]).Any(h => !allowed.Contains(h) && !h.Contains('@') && !Email.IsMatch(h) && !cfg.PixKey.Contains(h, StringComparison.OrdinalIgnoreCase)))
                v.Add("Endereço DNS inventado (a DNS é informada pela equipe técnica)");
        }
        if (new[] { "conteudo licenciado", "conteudos licenciados", "todos os canais sao licenciados", "100 legal", "totalmente legalizado", "totalmente legal", "temos autorizacao para todos",
                "conteudo autorizado", "tudo autorizado", "temos os direitos" }.Any(p => Affirms(n, p)))
            v.Add("Afirmou autorização de conteúdo sem confirmação da equipe");
        if (new[] { "creditos ilimitados", "credito ilimitado" }.Any(p => Affirms(n, p)))
            v.Add("Afirmou créditos ilimitados");
        foreach (Match m in Email.Matches(reply))
            if (!m.Value.Equals(cfg.PixKey, StringComparison.OrdinalIgnoreCase) && n.Contains(" pix "))
                v.Add($"Chave Pix diferente da configurada ({m.Value})");
        return v;
    }
}
