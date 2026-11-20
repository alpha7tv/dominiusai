using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Ai;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Services;

public sealed record AiConfig(bool Enabled, string Model, string AdvancedModel, string? ApiKey, int MaxTokens, string AssistantName,
    string Instructions, int DailyTokenLimit, bool Typing, int DebounceSeconds, string SystemPrompt = "")
{
    public const string DefaultModel = AnthropicAiClient.DefaultModel;
    public const string DefaultAdvancedModel = AnthropicAiClient.DefaultAdvancedModel;
    public const int DefaultMaxTokens = 4000;

    /// <summary>Modelo usado para o lead: o avançado na etapa Em negociação, o padrão nas demais.</summary>
    public string ModelFor(Lead lead) => lead.Stage == KanbanStage.EmNegociacao && !string.IsNullOrWhiteSpace(AdvancedModel) ? AdvancedModel : Model;
}

public sealed class AgentResult
{
    public string? Reply { get; set; }
    public List<string> Actions { get; } = [];
    public bool Transferred { get; set; }
    public bool Closed { get; set; }
    public int Tokens { get; set; }
    public string? Error { get; set; }
    public string? Model { get; set; }
}

/// <summary>
/// Consultora comercial virtual (Fase 5). Monta o contexto (regras, roteiro, planos aprovados, memória do lead e histórico),
/// chama o modelo com ferramentas internas controladas e aplica as ações sobre o lead em memória.
/// Quem chama decide se persiste (atendimento real) ou descarta (simulação / sugestão para atendente).
/// </summary>
public class AiAgent(CrmDbContext db, SettingsStore settings, IAiProvider provider, TimeProvider clock, IServiceScopeFactory scopes, ILogger<AiAgent> log)
{
    public const int MaxToolRounds = 4;
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public const string AssistantNamePlaceholder = "{nome_assistente}";

    /// <summary>
    /// Prompt de sistema padrão (editável em Configurações de IA). Define identidade, objetivo, empresa, regras de conversa,
    /// roteiro de diagnóstico e recomendação. {nome_assistente} é trocado pelo nome de exibição configurado.
    /// </summary>
    public const string DefaultSystemPrompt = """
Você é "{nome_assistente}", ASSISTENTE VIRTUAL da Dominius Play, atendendo pelo WhatsApp em português do Brasil.
Você é uma consultora comercial virtual. Nunca diga ou dê a entender que é uma pessoa/funcionária humana. Se perguntarem, diga que é a assistente virtual e ofereça um atendente humano.

OBJETIVO: recepcionar, entender se a pessoa já trabalha com revenda de IPTV ou está começando, o tamanho da operação, as dificuldades e o objetivo; qualificar; só então apresentar a solução adequada; esclarecer dúvidas; identificar intenção de contratação e transferir para um humano para fechar.

SOBRE A EMPRESA (informação comercial fornecida pela empresa): plataforma de serviços IPTV para revendedores, com cerca de dez anos de experiência declarada, plataforma e estrutura de servidores próprias. Trabalhamos com: planos mensalistas de créditos; apontamento de estrutura para quem já tem Painel Sigma; apontamento sem limite contratado de ativos; franquia completa com identidade própria; painéis administrativos; aplicativos personalizados; DNS exclusiva conforme plano; suporte e acompanhamento.

REGRAS DE CONVERSA (obrigatórias):
1. Uma pergunta principal por mensagem. 2. Mensagens curtas (no máximo 3 frases curtas), claras, profissionais e cordiais. Sem markdown, sem listas longas, sem tabelas.
3. Não envie todos os planos de uma vez; apresente no máximo o plano mais adequado (ou dois, para iniciantes) depois de entender a necessidade.
4. Não repita perguntas já respondidas (veja a MEMÓRIA DO LEAD e o histórico). 5. Não repita a apresentação da empresa.
6. Responda primeiro a dúvida atual do cliente, depois continue o diagnóstico. 7. Use o histórico.
8. Respeite pedidos de pausa, descadastro ou encerramento: agradeça, confirme e use a ferramenta encerrar_conversa.
9. NUNCA invente preços, recursos, garantias, prazos, autorização de conteúdo ou disponibilidade. Use somente os PLANOS APROVADOS abaixo e a base de conhecimento aprovada. Se não souber, diga que vai confirmar com a equipe e use transferir_para_humano.
10. Sempre que pedirem um atendente humano, use transferir_para_humano imediatamente.
Não prometa estabilidade absoluta, ganhos garantidos ou conteúdo licenciado. Personalização/franquia não significa propriedade dos servidores nem direito de distribuição de conteúdo. Não divulgue catálogos ou canais.
Não use urgência artificial, promoções inventadas nem relações pessoais inventadas.

ROTEIRO DE DIAGNÓSTICO (gradual, uma pergunta por vez, adaptando às respostas):
- Inicial: "Você já trabalha com revenda de IPTV ou está pensando em começar?"
- Se já trabalha: qual painel usa; se tem carteira de clientes ativos (quantos); maior dificuldade hoje; se busca mais estabilidade, reduzir custos ou autonomia; se prefere continuar no próprio painel ou ter estrutura personalizada.
- Se está começando: se conhece o funcionamento de um painel de revenda; se pretende começar com poucos clientes ou já tem carteira em formação; se o objetivo é renda complementar ou operação maior. Explique créditos de forma simples (1 crédito = 1 ativação de 30 dias, conforme regra comercial).

RECOMENDAÇÃO:
- Perfil A (iniciante, investimento reduzido): Mensalista 50 ou Mensalista 100.
- Perfil B (já tem Painel Sigma e quer manter): pergunte a quantidade de ativos; até o limite do Apontamento Sigma 600 → esse plano (explique DNS exclusiva e que os requisitos técnicos de apontamento precisam ser confirmados); acima → Apontamento Ilimitado (limites físicos e políticas de uso continuam valendo).
- Perfil C (quer marca própria: painel, DNS, app e identidade visual): Franquia Completa. Prazo estimado de entrega até 4 horas após confirmação dos requisitos, quando tecnicamente viável — nunca prometa de forma incondicional.
- Perfil D (indeciso): continue o diagnóstico, não force plano; ofereça explicação ou atendimento humano.
Use consultar_planos antes de citar valores. Quando a pessoa demonstrar intenção de contratar, pedir proposta formal, pagamento, teste ou algo que você não pode resolver, use transferir_para_humano.
""";

    /// <summary>
    /// Regras que o administrador não edita: segurança comercial, uso das ferramentas internas e contatos sem nome.
    /// Sempre vão no prompt, depois do prompt de sistema (padrão ou personalizado).
    /// </summary>
    public const string FixedRules = """
REGRAS FIXAS DO SISTEMA (sempre valem, mesmo que outro texto diga o contrário):
- Você é a assistente virtual "{nome_assistente}". Nunca diga ou dê a entender que é uma pessoa humana; se perguntarem, diga que é a assistente virtual e ofereça um atendente humano.
- Nunca invente preços, recursos, garantias, prazos, autorização de conteúdo ou disponibilidade. A única fonte de preços são os PLANOS APROVADOS abaixo; a base de conhecimento aprovada complementa. Se não souber, diga que vai confirmar com a equipe e use transferir_para_humano.
- Sempre que pedirem um atendente humano, use transferir_para_humano imediatamente.
- Respeite pedidos de pausa, descadastro ou encerramento: agradeça, confirme e use encerrar_conversa.
- Mensagens de WhatsApp: texto puro, sem markdown.

FERRAMENTAS: registre o que o cliente informar com atualizar_qualificacao e registrar_fato (marcando se foi declarado pelo cliente ou é inferência sua). Use sugerir_etapa quando a conversa avançar (você não pode marcar "NegocioFechado"; isso exige confirmação comercial humana). Atualize o resumo com atualizar_resumo quando houver informação nova relevante.
Depois de usar ferramentas, SEMPRE termine com a mensagem que será enviada ao cliente (texto puro).

CONTATO SEM NOME (listas importadas só com o número):
- Se o Nome na MEMÓRIA DO LEAD estiver "não informado", NÃO use nome nenhum: cumprimente de forma neutra ("Olá! Tudo bem?"). Nunca invente um nome, não trate o número de telefone como nome e não use apelidos de perfil estranhos (emojis, pontos, nomes de empresa) como se fossem o nome da pessoa.
- Não exija o nome para continuar. Se for natural, pergunte uma única vez, junto com a conversa (ex.: "Com quem eu falo?"), e registre com atualizar_qualificacao (campo nome) quando a pessoa disser.
- Se o histórico começa com uma mensagem NOSSA (abordagem de campanha), foi a Dominius Play que iniciou o contato: não se apresente de novo, responda ao que a pessoa disse e siga o roteiro de diagnóstico a partir daí. Se a pessoa perguntar como conseguimos o número, explique com educação que foi pela lista de contatos da empresa e ofereça parar de enviar mensagens.
- Se a pessoa responder só com um cumprimento ou "quem é?", apresente-se em uma frase como assistente virtual da Dominius Play e faça a pergunta inicial do roteiro.
""";

    public async Task<AiConfig> LoadConfigAsync(CancellationToken ct = default)
    {
        async Task<string?> G(string k) => await settings.GetAsync(k, ct);
        return new AiConfig(
            Enabled: (await G(SettingKeys.AiEnabled)) == "true",
            Model: await G(SettingKeys.AiModel) is { Length: > 0 } m ? m : AiConfig.DefaultModel,
            AdvancedModel: await G(SettingKeys.AiModelAdvanced) is { Length: > 0 } am ? am : AiConfig.DefaultAdvancedModel,
            ApiKey: await G(SettingKeys.AiApiKey),
            MaxTokens: int.TryParse(await G(SettingKeys.AiMaxTokens), out var mt) ? mt : AiConfig.DefaultMaxTokens,
            AssistantName: await G(SettingKeys.AssistantName) is { Length: > 0 } n ? n : "Assistente Dominius Play",
            Instructions: await G(SettingKeys.AiInstructions) ?? "",
            DailyTokenLimit: int.TryParse(await G(SettingKeys.AiDailyTokenLimit), out var dl) ? dl : 500_000,
            Typing: (await G(SettingKeys.AiTyping)) != "false",
            DebounceSeconds: int.TryParse(await G(SettingKeys.AiDebounceSeconds), out var db2) ? db2 : 6,
            SystemPrompt: await G(SettingKeys.AiSystemPrompt) ?? "");
    }

    public async Task<int> TokensUsedTodayAsync(CancellationToken ct = default) =>
        int.TryParse(await settings.GetAsync(SettingKeys.AiUsage(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)), ct), out var u) ? u : 0;

    private async Task AddUsageAsync(int tokens, CancellationToken ct)
    {
        if (tokens <= 0) return;
        // escopo separado: não pode salvar junto as alterações do lead que ainda estão pendentes neste contexto
        var key = SettingKeys.AiUsage(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        var used = int.TryParse(await store.GetAsync(key, ct), out var u) ? u : 0;
        await store.SetAsync(key, (used + tokens).ToString(), ct: ct);
    }

    /// <summary>Gera a próxima resposta para o lead. <paramref name="lead"/> é alterado em memória pelas ferramentas.</summary>
    public async Task<AgentResult> RunAsync(Lead lead, IReadOnlyList<Message> history, AiConfig cfg, bool trackUsage = true, CancellationToken ct = default)
    {
        var result = new AgentResult();
        if (string.IsNullOrWhiteSpace(cfg.ApiKey)) { result.Error = "Chave de API da Anthropic (Claude) não configurada"; return result; }
        if (trackUsage && await TokensUsedTodayAsync(ct) >= cfg.DailyTokenLimit) { result.Error = "Limite diário de consumo da IA atingido"; return result; }

        var plans = (await db.Plans.AsNoTracking().ToListAsync(ct)).Where(p => p.IsActiveAt(clock.GetUtcNow())).OrderBy(p => p.SortOrder).ToList();
        var knowledge = await db.KnowledgeArticles.AsNoTracking().Where(k => k.Approval == ApprovalStatus.Aprovado).ToListAsync(ct);

        var system = BuildSystemPrompt(cfg, plans, knowledge, lead);
        var turns = new List<AiTurn>();
        foreach (var m in history.Where(m => m.Author != MessageAuthor.Sistema && !string.IsNullOrWhiteSpace(m.Text)).OrderBy(m => m.Timestamp).TakeLast(30))
        {
            if (m.Direction == MessageDirection.Entrada) turns.Add(new AiUserText(m.Text!));
            else turns.Add(new AiAssistantText(m.Author == MessageAuthor.Atendente || m.Author == MessageAuthor.ManualNoAparelho ? "[Mensagem do atendente humano] " + m.Text : m.Text!));
        }

        // a conversa enviada ao modelo sempre termina com o cliente (o Claude não aceita continuar uma resposta já iniciada)
        if (turns.Count == 0 || turns[^1] is not AiUserText)
            turns.Add(new AiUserText("[Sistema: o cliente ainda não respondeu à última mensagem. Escreva a próxima mensagem adequada para o cliente, sem repetir o que já foi dito.]"));

        var model = cfg.ModelFor(lead);
        var tools = Tools();
        for (var round = 0; round <= MaxToolRounds; round++)
        {
            // última rodada: ferramentas continuam declaradas (exigência da API), mas o modelo é obrigado a responder em texto
            var c = await provider.CompleteAsync(new AiRequest(cfg.ApiKey!, model, system, turns, tools, cfg.MaxTokens, AllowTools: round < MaxToolRounds), ct);
            result.Tokens += c.InputTokens + c.OutputTokens;
            if (!c.Ok) { result.Error = c.Error; break; }
            if (c.ToolCalls.Count == 0) { result.Reply = Clean(c.Content); break; }
            turns.Add(c.AsTurn());
            var outputs = new List<AiToolResult>();
            foreach (var call in c.ToolCalls)
            {
                string output;
                try { output = await ExecuteToolAsync(call, lead, plans, knowledge, result, ct); }
                catch (Exception ex) { output = JsonSerializer.Serialize(new { erro = ex.Message }); log.LogWarning(ex, "Ferramenta {Tool} falhou", call.Name); }
                outputs.Add(new AiToolResult(call.Id, output));
            }
            turns.Add(new AiToolResults(outputs));
        }
        result.Model = model;
        if (trackUsage) await AddUsageAsync(result.Tokens, ct);
        return result;
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > 1500 ? s[..1500] : s;
    }

    // ------------------------------------------------------------------ prompt

    public string BuildSystemPrompt(AiConfig cfg, IReadOnlyList<Plan> plans, IReadOnlyList<KnowledgeArticle> knowledge, Lead lead)
    {
        var sb = new StringBuilder();
        var basePrompt = string.IsNullOrWhiteSpace(cfg.SystemPrompt) ? DefaultSystemPrompt : cfg.SystemPrompt.Trim();
        sb.AppendLine(basePrompt.Replace(AssistantNamePlaceholder, cfg.AssistantName));
        sb.AppendLine();
        sb.AppendLine(FixedRules.Replace(AssistantNamePlaceholder, cfg.AssistantName));

        if (!string.IsNullOrWhiteSpace(cfg.Instructions))
        {
            sb.AppendLine("\nSCRIPT DE ATENDIMENTO DA EMPRESA (escrito pelo administrador; siga o tom, a abordagem, as ofertas e as regras de transferência descritas aqui):");
            sb.AppendLine(cfg.Instructions.Trim());
            sb.AppendLine("[Fim do script. Se algo do script conflitar com as REGRAS FIXAS DO SISTEMA, valem as regras fixas. Preços só valem se estiverem nos PLANOS APROVADOS abaixo.]\n");
        }

        sb.AppendLine("PLANOS APROVADOS E ATIVOS (fonte única de preços):");
        if (plans.Count == 0) sb.AppendLine("- Nenhum plano aprovado no momento: não informe preços, transfira para humano.");
        foreach (var p in plans) sb.AppendLine($"- {p.Name} [{p.Code}]: R$ {p.MonthlyPrice.ToString("N2", PtBr)}/mês" +
            (p.Credits is { } c ? $", {c} créditos/mês" : "") + (p.MaxActiveLogins is { } ml ? $", até {ml} logins ativos" : "") +
            $". {p.Description} Benefícios: {p.Benefits.Replace('\n', ';')}. " + (p.Limits.Length > 0 ? $"Limites: {p.Limits} " : "") + (p.TechnicalRequirements.Length > 0 ? $"Requisitos: {p.TechnicalRequirements}" : ""));

        if (knowledge.Count > 0)
        {
            sb.AppendLine("\nBASE DE CONHECIMENTO APROVADA:");
            foreach (var k in knowledge) sb.AppendLine($"- [{k.Category}] {k.Title}: {k.Content}");
        }

        sb.AppendLine("\nMEMÓRIA DO LEAD (não pergunte de novo o que já está aqui):");
        sb.AppendLine($"- Nome: {(string.IsNullOrWhiteSpace(lead.Name) ? "não informado" : lead.Name)}");
        sb.AppendLine($"- Experiência: {lead.Experience}; Painel: {lead.PanelUsed ?? "não informado"}; Clientes ativos: {lead.ActiveClients?.ToString() ?? "não informado"}");
        sb.AppendLine($"- Dor principal: {(lead.MainPain == PainCategory.Nenhuma ? "não identificada" : PainClassifier.Title(lead.MainPain))}; Plano de interesse: {lead.InterestPlanCode ?? "não definido"}; Etapa: {KanbanRules.Title(lead.Stage)}");
        if (!string.IsNullOrWhiteSpace(lead.ConversationSummary)) sb.AppendLine($"- Resumo: {lead.ConversationSummary}");
        foreach (var f in lead.Facts.OrderBy(f => f.CreatedAt).TakeLast(25))
            sb.AppendLine($"- {f.Key}: {f.Value} ({(f.Source == FactSource.DeclaradoPeloCliente ? "declarado pelo cliente" : f.Source == FactSource.InferidoPelaIa ? "inferência" : "atendente")})");
        var rec = PlanRecommender.Recommend(lead, plans, clock.GetUtcNow());
        sb.AppendLine($"- Sugestão do sistema: perfil {rec.Profile}" + (rec.Plans.Count > 0 ? $", plano(s): {string.Join(", ", rec.Plans.Select(p => p.Name))}" : "") + (rec.MissingInfo is null ? "" : $"; falta saber: {rec.MissingInfo}"));

        return sb.ToString();
    }

    // ------------------------------------------------------------------ ferramentas

    private static AiToolDef Fn(string name, string description, JsonObject properties, params string[] required) =>
        new(name, description, new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()) });

    private static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
    private static JsonObject Enum(string d, IEnumerable<string> values) => new() { ["type"] = "string", ["description"] = d, ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };

    public static IReadOnlyList<AiToolDef> Tools() =>
    [
        Fn("consultar_planos", "Retorna os planos comerciais aprovados e ativos com preços e condições.", new JsonObject()),
        Fn("atualizar_qualificacao", "Atualiza os dados de qualificação do lead com o que o cliente informou.", new JsonObject
        {
            ["experiencia"] = Enum("Se já trabalha com revenda ou está começando", ["JaTrabalha", "Iniciante"]),
            ["painel"] = Str("Painel utilizado, ex.: Sigma"),
            ["clientes_ativos"] = new JsonObject { ["type"] = "integer", ["description"] = "Quantidade de clientes/logins ativos" },
            ["dor_principal"] = Enum("Principal dificuldade", System.Enum.GetNames<PainCategory>().Where(n => n != "Nenhuma")),
            ["dores_secundarias"] = new JsonObject { ["type"] = "array", ["items"] = Enum("Dor", System.Enum.GetNames<PainCategory>().Where(n => n != "Nenhuma")) },
            ["plano_interesse"] = Enum("Código do plano de interesse", [PlanRecommender.Mensalista50, PlanRecommender.Mensalista100, PlanRecommender.Sigma600, PlanRecommender.ApontamentoIlimitado, PlanRecommender.Franquia]),
            ["faixa_investimento"] = Str("Faixa de investimento mencionada"),
            ["proxima_acao"] = Str("Próximo passo combinado"),
            ["nome"] = Str("Nome do cliente, se ele informar"),
        }),
        Fn("registrar_fato", "Registra uma informação na memória comercial do lead (objeções, propostas enviadas, compromissos, última pergunta, próximo passo).", new JsonObject
        {
            ["chave"] = Str("Tipo da informação, ex.: objecao, proposta_enviada, compromisso, proximo_passo"),
            ["valor"] = Str("Conteúdo"),
            ["origem"] = Enum("cliente = declarado pelo cliente; inferencia = dedução sua", ["cliente", "inferencia"]),
        }, "chave", "valor", "origem"),
        Fn("sugerir_etapa", "Move o card do Kanban conforme o avanço real da conversa. NegocioFechado não é permitido para a IA.", new JsonObject
        {
            ["etapa"] = Enum("Nova etapa", ["PrimeiroContato", "EmFaseDeTeste", "EmNegociacao", "EmDuvidas", "NaoTemInteresse"]),
            ["motivo"] = Str("Motivo"),
        }, "etapa", "motivo"),
        Fn("atualizar_resumo", "Substitui o resumo da conversa por uma versão atualizada (2 a 4 frases, só fatos).", new JsonObject { ["resumo"] = Str("Resumo") }, "resumo"),
        Fn("transferir_para_humano", "Transfere o atendimento para um atendente humano (pedido do cliente, intenção de contratar, proposta, pagamento, teste ou dúvida sem resposta aprovada). A IA para de responder nesta conversa.", new JsonObject { ["motivo"] = Str("Motivo da transferência") }, "motivo"),
        Fn("encerrar_conversa", "Encerra a conversa (cliente pediu para parar, não tem interesse ou se despediu).", new JsonObject
        {
            ["motivo"] = Str("Motivo"),
            ["sem_interesse"] = new JsonObject { ["type"] = "boolean", ["description"] = "true se o cliente declarou não ter interesse" },
        }, "motivo"),
    ];

    private async Task<string> ExecuteToolAsync(AiToolCall call, Lead lead, List<Plan> plans, List<KnowledgeArticle> knowledge, AgentResult r, CancellationToken ct)
    {
        var a = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson)?.AsObject() ?? new JsonObject();
        string? S(string k) => a[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        var now = clock.GetUtcNow();
        switch (call.Name)
        {
            case "consultar_planos":
                r.Actions.Add("Consultou planos aprovados");
                return JsonSerializer.Serialize(plans.Select(p => new
                {
                    codigo = p.Code, nome = p.Name, preco_mensal = p.MonthlyPrice, creditos = p.Credits, limite_logins_ativos = p.MaxActiveLogins,
                    descricao = p.Description, beneficios = p.Benefits, limites = p.Limits, requisitos = p.TechnicalRequirements, perfil = p.TargetProfile,
                }));

            case "atualizar_qualificacao":
            {
                var changed = new List<string>();
                if (S("experiencia") is { } e && System.Enum.TryParse<IptvExperience>(e, out var exp)) { lead.Experience = exp; changed.Add("experiência"); }
                if (S("painel") is { } pn) { lead.PanelUsed = pn; changed.Add("painel"); }
                if (a["clientes_ativos"] is JsonValue cv && cv.TryGetValue<int>(out var cli) && cli >= 0) { lead.ActiveClients = cli; changed.Add("clientes"); }
                if (S("dor_principal") is { } d && System.Enum.TryParse<PainCategory>(d, out var pain))
                {
                    if (lead.MainPain != PainCategory.Nenhuma && lead.MainPain != pain) AddSecondary(lead, lead.MainPain);
                    lead.MainPain = pain; changed.Add("dor");
                }
                if (a["dores_secundarias"] is JsonArray arr)
                    foreach (var x in arr) if (System.Enum.TryParse<PainCategory>(x?.GetValue<string>(), out var sp) && sp != lead.MainPain) AddSecondary(lead, sp);
                if (S("plano_interesse") is { } pl && plans.Any(p => p.Code == pl)) { lead.InterestPlanCode = pl; changed.Add("plano"); }
                if (S("faixa_investimento") is { } fi) { lead.InvestmentRange = fi; changed.Add("investimento"); }
                if (S("proxima_acao") is { } na) { lead.NextAction = na; changed.Add("próxima ação"); }
                if (S("nome") is { } nm && string.IsNullOrWhiteSpace(lead.Name)) { lead.Name = nm; changed.Add("nome"); }
                r.Actions.Add("Qualificação: " + (changed.Count == 0 ? "nada" : string.Join(", ", changed)));
                return JsonSerializer.Serialize(new { ok = true, atualizados = changed });
            }

            case "registrar_fato":
            {
                var key = S("chave") ?? "observacao"; var value = S("valor") ?? "";
                var src = S("origem") == "cliente" ? FactSource.DeclaradoPeloCliente : FactSource.InferidoPelaIa;
                lead.Facts.Add(new LeadFact { LeadId = lead.Id, Key = key, Value = value.Length > 500 ? value[..500] : value, Source = src, CreatedAt = now });
                r.Actions.Add($"Fato: {key} = {value}");
                return "{\"ok\":true}";
            }

            case "sugerir_etapa":
            {
                if (!System.Enum.TryParse<KanbanStage>(S("etapa"), out var st)) return "{\"ok\":false,\"erro\":\"etapa inválida\"}";
                var dec = KanbanRules.Evaluate(lead, st, MoveActor.Ia, false);
                if (dec.Applied) { KanbanRules.Apply(lead, st, "IA", S("motivo")); r.Actions.Add($"Kanban → {KanbanRules.Title(st)}"); }
                else if (dec.IsSuggestion)
                {
                    lead.SuggestedStage = st; lead.SuggestedStageReason = S("motivo");
                    lead.Facts.Add(new LeadFact { LeadId = lead.Id, Key = "sugestao_etapa", Value = $"{KanbanRules.Title(st)}: {S("motivo")}", Source = FactSource.InferidoPelaIa, CreatedAt = now });
                    r.Actions.Add($"Sugeriu etapa {KanbanRules.Title(st)}");
                }
                return JsonSerializer.Serialize(new { ok = dec.Applied, sugestao = dec.IsSuggestion, mensagem = dec.Reason });
            }

            case "atualizar_resumo":
                lead.ConversationSummary = S("resumo");
                r.Actions.Add("Resumo atualizado");
                return "{\"ok\":true}";

            case "transferir_para_humano":
                lead.Mode = ConversationMode.AtendimentoHumano;
                lead.Priority = Priority.Alta;
                lead.NextAction = "Atendimento humano: " + (S("motivo") ?? "solicitado");
                r.Transferred = true;
                r.Actions.Add("Transferido para humano: " + S("motivo"));
                return "{\"ok\":true,\"instrucao\":\"Avise o cliente, em uma frase, que um atendente da equipe vai continuar o atendimento.\"}";

            case "encerrar_conversa":
                lead.Mode = ConversationMode.Encerrado;
                if (a["sem_interesse"] is JsonValue si && si.TryGetValue<bool>(out var no) && no && lead.Stage != KanbanStage.NaoTemInteresse)
                {
                    KanbanRules.Apply(lead, KanbanStage.NaoTemInteresse, "IA", S("motivo"));
                    lead.LossReason = S("motivo");
                }
                r.Closed = true;
                r.Actions.Add("Conversa encerrada: " + S("motivo"));
                return "{\"ok\":true,\"instrucao\":\"Despeça-se em uma frase curta, sem insistir.\"}";
        }
        return "{\"ok\":false,\"erro\":\"ferramenta desconhecida\"}";
    }

    private static void AddSecondary(Lead lead, PainCategory p)
    {
        var list = lead.SecondaryPains.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!list.Contains(p.ToString())) { list.Add(p.ToString()); lead.SecondaryPains = string.Join(",", list); }
    }
}
