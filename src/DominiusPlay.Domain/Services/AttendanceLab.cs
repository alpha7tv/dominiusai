using System.Text.RegularExpressions;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Domain.Services;

/// <summary>Memória comercial da conversa (parte 7): o que já se sabe e o que já foi dito.</summary>
public sealed class CommercialMemory
{
    public string? Name { get; set; }
    public SalesProfile Profile { get; set; }
    public List<string> Products { get; } = [];
    public string? Device { get; set; }
    public List<string> AnsweredCodes { get; } = [];
    public List<string> Objections { get; } = [];
    public HashSet<string> AskedQuestions { get; } = new(StringComparer.Ordinal);
    public HashSet<CommercialTrigger> OffersMade { get; } = [];
    public string Stage { get; set; } = "Descoberta";
    public bool WantsToBuy { get; set; }
    public bool HumanRequested { get; set; }
    public bool OptedOut { get; set; }

    public IReadOnlyList<string> Describe()
    {
        var l = new List<string>();
        if (Name is not null) l.Add("Nome: " + Name);
        if (Profile != SalesProfile.Indefinido) l.Add("Perfil: " + SalesProfiles.Title(Profile));
        if (Products.Count > 0) l.Add("Produto em discussão: " + string.Join(", ", Products));
        if (Device is not null) l.Add("Aparelho: " + Device);
        if (Objections.Count > 0) l.Add("Objeções: " + string.Join("; ", Objections));
        if (AnsweredCodes.Count > 0) l.Add("Perguntas já respondidas: " + string.Join(", ", AnsweredCodes.Distinct()));
        if (OffersMade.Count > 0) l.Add("Ofertas já apresentadas: " + string.Join(", ", OffersMade.Select(CommercialTriggers.Title)));
        if (AskedQuestions.Count > 0) l.Add("Perguntas que a IA já fez: " + AskedQuestions.Count);
        l.Add("Etapa: " + Stage);
        if (WantsToBuy) l.Add("Interesse em contratar: sim");
        if (HumanRequested) l.Add("Pediu atendimento humano");
        return l;
    }
}

public sealed record LabStep(
    string Message, IntentCategory Intent, string IntentTitle, SalesProfile Profile, CommercialTrigger Trigger,
    string? Category, string? MatchedCode, string? MatchedQuestion, double Score, string? Reply, string? Product,
    IReadOnlyList<string> MemoryUsed, string SuggestedAction, IReadOnlyList<string> Alerts, bool Handoff, bool OptOut);

public sealed record LabScenario(string Key, string Title, IReadOnlyList<string> Messages);

/// <summary>
/// Motor determinístico do Laboratório de Atendimento IA (parte 12): mostra, mensagem a mensagem, a intenção, o perfil,
/// o gatilho, a pergunta encontrada na base, a resposta oficial, a memória usada e a ação sugerida. Não envia nada e
/// não faz pagamentos. Também serve de rede de segurança: quando a IA (Claude) está desligada, é a resposta de referência.
/// </summary>
public static class AttendanceLab
{
    public static readonly IReadOnlyList<LabScenario> Scenarios =
    [
        new("iniciante", "Cliente iniciante", ["Oi, nunca trabalhei com isso e quero começar a revender", "Quanto custa para começar?", "Como funciona o sistema de créditos?", "Os créditos acumulam?"]),
        new("experiente", "Revendedor experiente", ["Já trabalho com painel próprio, tenho uns 300 clientes", "Qual a diferença entre mensalista, apontamento e franquia?", "Qual é a capacidade de clientes simultâneos?"]),
        new("preco", "Cliente que reclama de preço", ["Quanto custa o painel de 500 créditos?", "Achei caro, tenho pouco dinheiro para começar"]),
        new("master", "Interessado em painel Master", ["Posso criar sub-revendedores?", "Os créditos do sub-revendedor saem de onde?"]),
        new("amigo", "Quer dividir com um amigo", ["Meu amigo também quer trabalhar com revenda", "E como fica o rateio entre nós dois?"]),
        new("teste", "Solicitando teste", ["Posso testar o painel antes de contratar?", "Vocês podem me liberar um teste?"]),
        new("aplicativos", "Perguntando sobre aplicativos", ["Quais aplicativos parceiros vocês têm?", "Qual aplicativo posso testar?", "Vou usar numa TV Box"]),
        new("pix", "Solicitando a chave Pix", ["Me passa a chave Pix", "Já paguei, vocês receberam?"]),
        new("contratar", "Querendo contratar", ["Meu nome é Carlos e quero contratar o plano de 100 créditos", "Preciso de CNPJ?", "Quanto tempo demora para liberar meu painel depois do pagamento?"]),
        new("humano", "Solicitando atendimento humano", ["Quanto custa a franquia?", "Quero falar com um atendente"]),
        new("descadastro", "Não deseja mais receber mensagens", ["Não quero mais receber mensagens, me tire da lista", "Oi?"]),
    ];

    private static readonly Regex NameRx = new(@"\b(?:meu nome e|me chamo|aqui e o|aqui e a|sou o|sou a)\s+([a-z]{2,20})", RegexOptions.Compiled);
    private static readonly (string Device, string[] Phrases)[] Devices =
    [
        ("TV Box", ["tv box", "tvbox"]), ("Fire Stick", ["fire stick", "firestick", "fire tv"]), ("Smart TV Samsung", ["samsung"]), ("Smart TV LG", ["lg"]),
        ("Smart TV", ["smart tv", "smarttv"]), ("Celular", ["celular", "android", "iphone"]), ("Computador", ["computador", "notebook", "pc"]),
    ];
    private const string QuestionDevice = "Qual aparelho você pretende utilizar?";
    private const string QuestionStart = "Você já trabalha com revenda ou está começando agora?";

    /// <summary>Generic "quanto custa / mais barato" resolvido pelo produto em discussão (parte 7).</summary>
    private static readonly string[] GenericPrice = ["quanto custa", "qual o valor", "qual valor", "qual investimento", "quanto vou gastar", "mais barato", "tem plano barato", "quanto sai", "qual o preco", "preco"];

    public static IReadOnlyList<LabStep> Run(IReadOnlyList<string> messages, IReadOnlyList<KbEntry> entries, CommercialConfig cfg, IReadOnlyList<Plan> activePlans, CommercialMemory? memory = null)
    {
        memory ??= new CommercialMemory();
        var published = entries.Where(e => e.Status == KbStatus.Publicado).ToList();
        var steps = new List<LabStep>();
        for (var i = 0; i < messages.Count; i++) steps.Add(Step(messages[i], i == 0, published, cfg, activePlans, memory));
        return steps;
    }

    public static LabStep Step(string message, bool first, IReadOnlyList<KbEntry> entries, CommercialConfig cfg, IReadOnlyList<Plan> plans, CommercialMemory mem)
    {
        var alerts = new List<string>();
        var norm = " " + KnowledgeMatcher.Normalize(message) + " ";
        var intent = IntentDetector.Detect(message, first);
        var memoryBefore = mem.Describe();

        if (mem.OptedOut)
            return new(message, intent, IntentDetector.Title(intent), mem.Profile, CommercialTrigger.Nenhum, null, null, null, 0, null, null, memoryBefore,
                "Nenhum envio: o contato pediu descadastro (só um atendente pode retomar com novo consentimento)", ["Contato descadastrado: nenhuma resposta automática"], false, true);

        if (intent == IntentCategory.Descadastro)
        {
            mem.OptedOut = true; mem.Stage = "Encerrada (descadastro)";
            return new(message, intent, IntentDetector.Title(intent), mem.Profile, CommercialTrigger.Nenhum, null, null, null, 0,
                "Pronto, removi seu número e você não vai mais receber nossas mensagens. Desculpe o incômodo.", null, memoryBefore,
                "Registrar descadastro, revogar consentimento, cancelar envios pendentes e encerrar a conversa", [], false, true);
        }
        if (intent == IntentCategory.PedidoHumano)
        {
            mem.HumanRequested = true; mem.Stage = "Atendimento humano";
            return new(message, intent, IntentDetector.Title(intent), mem.Profile, CommercialTrigger.Nenhum, null, null, null, 0,
                "Claro! Vou chamar um atendente da equipe para continuar com você. Já deixei anotado o que conversamos.", mem.Products.LastOrDefault(), memoryBefore,
                "Transferir para atendimento humano com a ficha da conversa", [], true, false);
        }

        // memória: nome, aparelho, perfil, objeções
        if (NameRx.Match(norm) is { Success: true } nm && mem.Name is null) mem.Name = char.ToUpperInvariant(nm.Groups[1].Value[0]) + nm.Groups[1].Value[1..];
        foreach (var (dev, phrases) in Devices) if (phrases.Any(p => norm.Contains(" " + p + " "))) { mem.Device ??= dev; break; }
        mem.Profile = SalesProfiles.Detect(message, mem.Profile);
        var trigger = CommercialTriggers.Detect(message);
        if (trigger is CommercialTrigger.AchouCaro or CommercialTrigger.OrcamentoBaixo) mem.Objections.Add(trigger == CommercialTrigger.AchouCaro ? "preço" : "orçamento baixo");
        if (CommercialTriggers.Product(trigger, cfg) is { } tp) AddProduct(mem, tp);
        if (trigger is CommercialTrigger.QuerContratar or CommercialTrigger.PedePix || intent == IntentCategory.IntencaoCompra) { mem.WantsToBuy = true; mem.Stage = "Contratação"; }
        // plano citado pelo número de créditos ("plano de 100 créditos")
        foreach (var p in plans.Where(p => p.Credits is not null))
            if (norm.Contains($" {p.Credits} credito")) AddProduct(mem, p.Code);

        // busca na base (com o produto em discussão)
        var matches = KnowledgeMatcher.Search(message, entries, mem.Products, take: 3);
        var match = matches.FirstOrDefault();
        if (GenericPrice.Any(g => norm.Contains(" " + g + " ")) && mem.Products.Count > 0 && (match is null || match.Score < 0.9))
        {
            var code = mem.Products[^1] switch
            {
                PlanCodes.Apontamento600 or PlanCodes.ApontamentoIlimitado => DominiusKnowledge.Code(15),
                PlanCodes.Franquia => DominiusKnowledge.Code(16),
                PlanCodes.Mensalista500 when mem.Profile == SalesProfile.Amigo => DominiusKnowledge.Code(72),
                PlanCodes.Mensalista100 or PlanCodes.Mensalista200 or PlanCodes.Mensalista500 => DominiusKnowledge.Code(41),
                _ => DominiusKnowledge.Code(1),
            };
            if (entries.FirstOrDefault(e => e.Code == code) is { } ctxEntry) match = new KbMatch(ctxEntry, 1, "produto em discussão: " + mem.Products[^1]);
        }
        if (match is not null)
            foreach (var p in match.Entry.Products.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(1)) if (mem.Products.Count == 0) AddProduct(mem, p);

        // resposta: oferta do gatilho (uma vez por conversa) ou resposta oficial da base
        string? reply = null; var handoff = false;
        var friendMade = mem.OffersMade.Contains(CommercialTrigger.AmigoInteressado);
        // ofertas comerciais aparecem uma vez por conversa (sem insistir); avisos de teste/Pix/pagamento valem sempre
        var salesOffer = trigger is CommercialTrigger.OrcamentoBaixo or CommercialTrigger.AchouCaro or CommercialTrigger.AmigoInteressado or CommercialTrigger.JaPossuiPainel or CommercialTrigger.MarcaPropria;
        var offer = salesOffer && mem.OffersMade.Contains(trigger) ? null : CommercialTriggers.OfferMessage(trigger, cfg, plans, friendMade);
        // a base responde quando a pergunta é exatamente uma das 75 (ex.: "Vocês podem me liberar um teste?", "Qual aplicativo posso testar?")
        if (offer is not null && match is { Score: >= 0.9 } && trigger is not (CommercialTrigger.QuerTestarPainel or CommercialTrigger.InformaPagamento or CommercialTrigger.PedePix))
            offer = null;
        if (offer is not null) { reply = offer; mem.OffersMade.Add(trigger); }
        else if (PlanPriceQuestion(norm, plans) is { } pp)
        {
            reply = pp.Reply; AddProduct(mem, pp.Code);
            match = entries.FirstOrDefault(e => e.Code == DominiusKnowledge.Code(41)) is { } e41 ? new KbMatch(e41, 1, "preço do plano citado") : match;
        }
        else if (match is not null)
        {
            var r = KnowledgeRenderer.Render(match.Entry.Answer, cfg, plans);
            alerts.AddRange(r.Warnings);
            reply = r.Text;
            // já respondida nesta conversa: não repete o texto inteiro, só a parte que responde à nova pergunta
            if (mem.AnsweredCodes.Contains(match.Entry.Code)) reply = BestSentence(reply, message) is { } best ? "Como te expliquei: " + best : "Reforçando: " + reply;
            if (match.Entry.NeedsConfirmation) { alerts.Add("Resposta depende de confirmação da equipe"); reply += " Se quiser, peço para a equipe confirmar os detalhes para você."; }
            if (match.Entry.Code == DominiusKnowledge.Code(72)) mem.OffersMade.Add(CommercialTrigger.AmigoInteressado);
        }
        if (trigger == CommercialTrigger.InformaPagamento) { handoff = true; alerts.Add("Pagamento informado pelo cliente: confirmar somente pelo sistema financeiro"); }
        if (trigger == CommercialTrigger.QuerContratar && entries.FirstOrDefault(e => e.Code == DominiusKnowledge.Code(20)) is { } contract)
        {
            // confirma o plano e as condições antes dos dados e do Pix (parte 9: "deseja contratar")
            var plan = plans.FirstOrDefault(p => mem.Products.LastOrDefault() == p.Code);
            reply = (plan is null ? "Ótimo! " : $"Ótimo! O {plan.Name} sai por {CreditMath.Money(plan.MonthlyPrice)} mensais" + (plan.Credits is { } c ? $" com {c} créditos" : "") + ". ")
                + KnowledgeRenderer.Render(contract.Answer, cfg, plans).Text;
            match = new KbMatch(contract, 1, "intenção de contratar");
        }
        if (reply is null)
        {
            reply = "Boa pergunta! Não tenho essa informação aprovada aqui, então vou confirmar com a equipe para te passar o dado correto.";
            handoff = intent is not (IntentCategory.PrimeiroContato or IntentCategory.Nenhuma);
            alerts.Add("Sem resposta aprovada na base");
            if (intent is IntentCategory.PrimeiroContato or IntentCategory.Nenhuma)
                reply = "Olá! Sou a assistente virtual da " + cfg.CompanyName + ". Como posso te ajudar?";
        }

        // não pedir de novo o que o cliente já informou
        if (mem.Name is not null) reply = reply.Replace("precisamos do seu primeiro nome e e-mail", "só preciso do seu e-mail").Replace("Solicitamos primeiro nome e e-mail", "Solicitamos apenas o e-mail");
        // não repetir perguntas: tira a pergunta do aparelho se já sabemos; pergunta de diagnóstico no máximo uma vez
        if (reply.Contains(QuestionDevice))
        {
            if (mem.Device is not null || mem.AskedQuestions.Contains(QuestionDevice)) reply = reply.Replace(" " + QuestionDevice, "").Replace(QuestionDevice, "").TrimEnd();
            else mem.AskedQuestions.Add(QuestionDevice);
        }
        if (mem.Profile == SalesProfile.Indefinido && !mem.AskedQuestions.Contains(QuestionStart) && !reply.TrimEnd().EndsWith('?') && !mem.WantsToBuy)
        { reply += " " + QuestionStart; mem.AskedQuestions.Add(QuestionStart); }
        if (mem.Name is not null && steps0(mem) && !reply.Contains(mem.Name)) reply = $"{mem.Name}, " + char.ToLowerInvariant(reply[0]) + reply[1..];

        alerts.AddRange(ReplySafetyGuard.Check(reply, cfg));
        if (match is not null) mem.AnsweredCodes.Add(match.Entry.Code);

        var action = SuggestedAction(intent, trigger, match, handoff, mem);
        if (handoff) mem.Stage = "Atendimento humano";
        return new(message, intent, IntentDetector.Title(intent), mem.Profile, trigger, match?.Entry.Category, match?.Entry.Code, match?.Entry.Question,
            match?.Score ?? 0, reply, mem.Products.LastOrDefault(), memoryBefore, action, alerts.Distinct().ToList(), handoff, false);

        // usa o nome uma única vez, na primeira resposta depois que ele foi informado
        static bool steps0(CommercialMemory m) { if (m.AskedQuestions.Contains("__nome_usado")) return false; m.AskedQuestions.Add("__nome_usado"); return true; }
    }

    /// <summary>"Quanto custa o painel de 500 créditos?" → preço do plano citado (com o custo por crédito).</summary>
    private static (string Code, string Reply)? PlanPriceQuestion(string norm, IReadOnlyList<Plan> plans)
    {
        if (!GenericPrice.Any(g => norm.Contains(" " + g + " ")) && !norm.Contains(" valor ")) return null;
        foreach (var p in plans.Where(p => p.Credits is > 0))
            if (norm.Contains($" {p.Credits} credito"))
            {
                var cost = CreditMath.CostPerCredit(p.MonthlyPrice, p.Credits!.Value);
                var best = plans.Where(x => x.Credits is > 0).MinBy(x => CreditMath.CostPerCredit(x.MonthlyPrice, x.Credits!.Value))?.Code == p.Code;
                return (p.Code, $"O plano de {p.Credits} créditos custa {CreditMath.Money(p.MonthlyPrice)} mensais, ou {CreditMath.Money(cost)} por crédito (cada crédito = uma ativação de 30 dias)."
                    + (best ? " É o pacote com o menor custo por crédito da tabela." : "") + " Os créditos acumulam com a mensalidade em dia.");
            }
        return null;
    }

    /// <summary>Frase da resposta que mais se relaciona com a nova pergunta (para não repetir a resposta inteira).</summary>
    private static string? BestSentence(string answer, string question)
    {
        var q = KnowledgeMatcher.Tokens(question);
        var ranked = Regex.Split(answer, @"(?<=[.!?])\s+").Where(x => x.Length > 3).Select(x => (Text: x, Hits: KnowledgeMatcher.Tokens(x).Count(q.Contains)))
            .OrderByDescending(x => x.Hits).ToList();
        // a mesma pergunta de novo (nenhuma frase se destaca): repete a resposta inteira
        if (ranked.Count == 0 || ranked[0].Hits < 2 || (ranked.Count > 1 && ranked[1].Hits == ranked[0].Hits)) return null;
        var best = ranked[0].Text;
        return char.ToLowerInvariant(best[0]) + best[1..];
    }

    private static void AddProduct(CommercialMemory mem, string code) { mem.Products.Remove(code); mem.Products.Add(code); }

    private static string SuggestedAction(IntentCategory intent, CommercialTrigger trigger, KbMatch? match, bool handoff, CommercialMemory mem) => trigger switch
    {
        CommercialTrigger.InformaPagamento => "Transferir para humano: conferir o pagamento no sistema financeiro (a IA não confirma pagamentos)",
        CommercialTrigger.PedePix => "Informar a chave Pix da configuração comercial e pedir para conferir o recebedor; aguardar confirmação do sistema",
        CommercialTrigger.QuerContratar => "Confirmar plano e condições, pedir primeiro nome e e-mail, orientar o Pix; não declarar contratação concluída",
        CommercialTrigger.QuerTestar or CommercialTrigger.QuerTestarPainel => "Enviar o link do teste do serviço (não existe teste do painel)",
        CommercialTrigger.AmigoInteressado => "Apresentar o painel Master dividido (Mensalista 500, que já sai com permissão Master)",
        CommercialTrigger.JaPossuiPainel => "Qualificar painel e quantidade de ativos; avaliar apontamento",
        CommercialTrigger.MarcaPropria => "Apresentar a franquia e confirmar requisitos; encaminhar à implantação",
        CommercialTrigger.OrcamentoBaixo or CommercialTrigger.AchouCaro => "Tratar a objeção sem pressionar: plano de entrada; divisão com amigo se fizer sentido",
        _ when handoff => "Transferir para humano (sem resposta aprovada na base)",
        _ when intent == IntentCategory.IntencaoCompra => "Priorizar: cliente com intenção de compra",
        _ when match is { Entry.NeedsConfirmation: true } => "Responder com a base e oferecer confirmação da equipe",
        _ when match is not null => "Responder com a base e seguir o diagnóstico do perfil " + SalesProfiles.Title(mem.Profile).ToLowerInvariant(),
        _ => "Continuar o diagnóstico",
    };
}
