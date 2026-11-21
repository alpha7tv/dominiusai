using System.Globalization;
using System.Text.Json;
using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Domain.Services;

public enum AutomationBlockType
{
    Inicio, EventoRecebido, VerificarConsentimento, VerificarCondicao, ExecutarIa, ConsultarBase, EnviarMensagem,
    AguardarResposta, AnalisarIntencao, AtualizarCrm, AdicionarTag, TransferirAtendente, Encerrar,
}

/// <summary>Quando o fluxo roda. Cada mensagem recebida dispara os fluxos ativos do gatilho correspondente.</summary>
public enum AutomationTrigger { MensagemRecebida = 0, RespostaACampanha = 1 }

/// <summary>Bloco do fluxo. <see cref="Next"/> é o próximo bloco; em blocos de decisão, <see cref="Next"/> = "sim" e <see cref="NextNo"/> = "não".</summary>
public sealed record AutomationBlock(string Id, AutomationBlockType Type, string? Param = null, string? Next = null, string? NextNo = null);

/// <summary>Dados do contato que o fluxo consegue ver (cópia: o motor não altera nada).</summary>
public sealed record AutomationInput(string Message, IntentCategory Intent, bool ConsentValid, bool OptedOut, KanbanStage Stage, string Tags,
    int Score, ConversationMode Mode, string? Segment, string? Campaign, bool RepliedToCampaign);

public enum AutomationActionType { AdicionarTag, AtualizarEtapa, Transferir, EnviarModelo, DeixarIaResponder, Encerrar }

public sealed record AutomationAction(AutomationActionType Type, string? Value);

public sealed record AutomationRun(IReadOnlyList<string> Trace, IReadOnlyList<AutomationAction> Actions, bool SuppressAi, string? Error);

/// <summary>Validação (ciclos, ações conflitantes) e execução pura dos fluxos de automação. Quem aplica as ações é o servidor.</summary>
public static class AutomationEngine
{
    public const int MaxBlocks = 60;
    public const int MaxSteps = 200;

    public static bool IsDecision(AutomationBlockType t) => t is AutomationBlockType.VerificarConsentimento or AutomationBlockType.VerificarCondicao or AutomationBlockType.AnalisarIntencao;

    public static string Title(AutomationBlockType t) => t switch
    {
        AutomationBlockType.Inicio => "Início",
        AutomationBlockType.EventoRecebido => "Evento recebido",
        AutomationBlockType.VerificarConsentimento => "Verificar consentimento",
        AutomationBlockType.VerificarCondicao => "Verificar condição",
        AutomationBlockType.ExecutarIa => "Executar IA",
        AutomationBlockType.ConsultarBase => "Consultar base de conhecimento",
        AutomationBlockType.EnviarMensagem => "Enviar mensagem permitida",
        AutomationBlockType.AguardarResposta => "Aguardar resposta",
        AutomationBlockType.AnalisarIntencao => "Analisar intenção",
        AutomationBlockType.AtualizarCrm => "Atualizar CRM",
        AutomationBlockType.AdicionarTag => "Adicionar tag",
        AutomationBlockType.TransferirAtendente => "Transferir atendente",
        AutomationBlockType.Encerrar => "Encerrar fluxo",
        _ => t.ToString(),
    };

    public static string ParamHint(AutomationBlockType t) => t switch
    {
        AutomationBlockType.VerificarCondicao => "campo operador valor. Campos: etapa, tag, pontuacao, modo, mensagem, segmento, campanha. Operadores: = != > >= < <= contem. Ex.: pontuacao >= 60",
        AutomationBlockType.AnalisarIntencao => "nome da intenção. Ex.: SolicitacaoPreco, PedidoHumano, IntencaoCompra",
        AutomationBlockType.EnviarMensagem => "nome de um modelo APROVADO em Mensagens",
        AutomationBlockType.AtualizarCrm => "etapa do Kanban. Ex.: Qualificado, PropostaEnviada, EmNegociacao",
        AutomationBlockType.AdicionarTag => "tag a adicionar. Ex.: quer-preco",
        AutomationBlockType.TransferirAtendente => "motivo (aparece na ficha). Ex.: pediu proposta",
        AutomationBlockType.ConsultarBase => "categoria da base (opcional). Ex.: Planos",
        AutomationBlockType.EventoRecebido => "opcional: texto que precisa estar na mensagem",
        _ => "",
    };

    public static List<AutomationBlock> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<AutomationBlock>>(json) ?? []; } catch (JsonException) { return []; }
    }

    public static string Serialize(IEnumerable<AutomationBlock> blocks) => JsonSerializer.Serialize(blocks);

    /// <summary>Lista de problemas que impedem salvar/ativar o fluxo (vazia = válido).</summary>
    public static List<string> Validate(IReadOnlyList<AutomationBlock> blocks, Func<string, bool>? templateApproved = null)
    {
        var p = new List<string>();
        if (blocks.Count == 0) { p.Add("O fluxo está vazio"); return p; }
        if (blocks.Count > MaxBlocks) p.Add($"Máximo de {MaxBlocks} blocos");
        var dupes = blocks.GroupBy(b => b.Id).Where(g => g.Count() > 1 || string.IsNullOrWhiteSpace(g.Key)).Select(g => g.Key).ToList();
        if (dupes.Count > 0) { p.Add("Identificador de bloco vazio ou repetido: " + string.Join(", ", dupes)); return p; }
        var byId = blocks.ToDictionary(b => b.Id);
        var starts = blocks.Where(b => b.Type == AutomationBlockType.Inicio).ToList();
        if (starts.Count != 1) { p.Add("O fluxo precisa de exatamente um bloco Início"); return p; }

        foreach (var b in blocks)
        {
            foreach (var n in new[] { b.Next, b.NextNo }.Where(n => !string.IsNullOrWhiteSpace(n)))
                if (!byId.ContainsKey(n!)) p.Add($"Bloco {b.Id} aponta para {n}, que não existe");
            if (b.Next == b.Id || b.NextNo == b.Id) p.Add($"Bloco {b.Id} aponta para ele mesmo");
            if (b.Type == AutomationBlockType.Encerrar && (b.Next is not null || b.NextNo is not null)) p.Add($"Bloco {b.Id} (Encerrar) não pode ter próximo bloco");
            if (!IsDecision(b.Type) && !string.IsNullOrWhiteSpace(b.NextNo)) p.Add($"Bloco {b.Id} não é de decisão e não pode ter saída 'não'");
            if (IsDecision(b.Type) && (string.IsNullOrWhiteSpace(b.Next) || string.IsNullOrWhiteSpace(b.NextNo))) p.Add($"Bloco {b.Id} ({Title(b.Type)}) precisa das saídas 'sim' e 'não'");
            var param = b.Param?.Trim() ?? "";
            switch (b.Type)
            {
                case AutomationBlockType.VerificarCondicao when ParseCondition(param) is null:
                    p.Add($"Bloco {b.Id}: condição inválida \"{param}\" ({ParamHint(b.Type)})"); break;
                case AutomationBlockType.AnalisarIntencao when !Enum.TryParse<IntentCategory>(param, true, out _):
                    p.Add($"Bloco {b.Id}: intenção desconhecida \"{param}\""); break;
                case AutomationBlockType.AtualizarCrm when !Enum.TryParse<KanbanStage>(param, true, out var st) || !Enum.IsDefined(st):
                    p.Add($"Bloco {b.Id}: etapa desconhecida \"{param}\""); break;
                case AutomationBlockType.AtualizarCrm when Enum.TryParse<KanbanStage>(param, true, out var st2) && st2 == KanbanStage.NegocioFechado:
                    p.Add($"Bloco {b.Id}: automação não pode marcar 'Negócio fechado' (exige confirmação comercial humana)"); break;
                case AutomationBlockType.AdicionarTag when param.Length is 0 or > 40 || param.Contains(','):
                    p.Add($"Bloco {b.Id}: informe uma tag (até 40 caracteres, sem vírgula)"); break;
                case AutomationBlockType.EnviarMensagem when param.Length == 0:
                    p.Add($"Bloco {b.Id}: informe o modelo de mensagem aprovado"); break;
                case AutomationBlockType.EnviarMensagem when templateApproved is not null && !templateApproved(param):
                    p.Add($"Bloco {b.Id}: o modelo \"{param}\" não existe ou não tem versão aprovada"); break;
            }
        }
        if (p.Count > 0) return p;

        var reachable = new HashSet<string>();
        var stack = new Stack<string>([starts[0].Id]);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!reachable.Add(id)) continue;
            foreach (var n in new[] { byId[id].Next, byId[id].NextNo }.Where(n => n is not null)) stack.Push(n!);
        }
        var orphans = blocks.Where(b => !reachable.Contains(b.Id)).Select(b => b.Id).ToList();
        if (orphans.Count > 0) p.Add("Blocos que nunca são alcançados a partir do Início: " + string.Join(", ", orphans));

        // ciclos: só são aceitos se passarem por "Aguardar resposta" (o fluxo para e espera o contato)
        foreach (var cycle in Cycles(byId))
            if (!cycle.Any(id => byId[id].Type == AutomationBlockType.AguardarResposta))
                p.Add("Fluxo circular infinito: " + string.Join(" → ", cycle.Append(cycle[0])) + " (inclua um bloco Aguardar resposta no caminho)");

        // ações conflitantes no mesmo caminho
        foreach (var path in Paths(byId, starts[0].Id))
        {
            var types = path.Select(id => byId[id].Type).ToList();
            var transfer = types.IndexOf(AutomationBlockType.TransferirAtendente);
            if (transfer >= 0 && types.Skip(transfer + 1).Any(t => t is AutomationBlockType.ExecutarIa or AutomationBlockType.EnviarMensagem))
                p.Add("Conflito: depois de Transferir atendente o fluxo não pode executar IA nem enviar mensagem (" + string.Join(" → ", path) + ")");
            var ai = types.IndexOf(AutomationBlockType.ExecutarIa);
            var send = types.IndexOf(AutomationBlockType.EnviarMensagem);
            if (ai >= 0 && send >= 0 && ai < send && !types.Skip(ai).Take(send - ai).Contains(AutomationBlockType.AguardarResposta))
                p.Add("Conflito: Executar IA e Enviar mensagem na mesma resposta mandam duas mensagens (" + string.Join(" → ", path) + ")");
        }
        return p.Distinct().ToList();
    }

    /// <summary>Executa o fluxo sobre uma cópia dos dados. Nada é enviado ou gravado: devolve o caminho e as ações.</summary>
    public static AutomationRun Run(IReadOnlyList<AutomationBlock> blocks, AutomationInput input)
    {
        var problems = Validate(blocks);
        if (problems.Count > 0) return new([], [], false, problems[0]);
        var byId = blocks.ToDictionary(b => b.Id);
        var trace = new List<string>();
        var actions = new List<AutomationAction>();
        var tags = input.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var stage = input.Stage;
        string? id = blocks.Single(b => b.Type == AutomationBlockType.Inicio).Id;
        for (var steps = 0; id is not null; steps++)
        {
            if (steps >= MaxSteps) return new(trace, actions, false, "Limite de passos atingido");
            var b = byId[id];
            string? next = b.Next;
            var param = b.Param?.Trim() ?? "";
            string note;
            switch (b.Type)
            {
                case AutomationBlockType.Inicio: note = "Início"; break;
                case AutomationBlockType.EventoRecebido:
                    if (param.Length > 0 && !Norm(input.Message).Contains(Norm(param))) { trace.Add($"{b.Id} Evento recebido: mensagem não contém \"{param}\" → fluxo não se aplica"); return new(trace, [], false, null); }
                    note = "Evento recebido: " + Preview(input.Message); break;
                case AutomationBlockType.VerificarConsentimento:
                    var ok = input.ConsentValid && !input.OptedOut;
                    next = ok ? b.Next : b.NextNo;
                    note = ok ? "Consentimento válido → sim" : "Sem consentimento válido ou descadastrado → não"; break;
                case AutomationBlockType.VerificarCondicao:
                    var res = Evaluate(ParseCondition(param)!.Value, input with { Tags = string.Join(',', tags), Stage = stage });
                    next = res ? b.Next : b.NextNo;
                    note = $"Condição \"{param}\" → {(res ? "sim" : "não")}"; break;
                case AutomationBlockType.AnalisarIntencao:
                    var want = Enum.Parse<IntentCategory>(param, true);
                    next = input.Intent == want ? b.Next : b.NextNo;
                    note = $"Intenção detectada: {IntentDetector.Title(input.Intent)} → {(input.Intent == want ? "sim" : "não")}"; break;
                case AutomationBlockType.ExecutarIa:
                    actions.Add(new(AutomationActionType.DeixarIaResponder, null)); note = "Agente de Atendimento IA responde"; break;
                case AutomationBlockType.ConsultarBase:
                    note = "IA consulta a base de conhecimento" + (param.Length > 0 ? $" ({param})" : "") + " (só itens aprovados)"; break;
                case AutomationBlockType.EnviarMensagem:
                    actions.Add(new(AutomationActionType.EnviarModelo, param)); note = $"Enviar modelo aprovado \"{param}\" (passa pelas regras de envio)"; break;
                case AutomationBlockType.AguardarResposta:
                    trace.Add($"{b.Id} Aguardar resposta: fluxo para aqui até a próxima mensagem");
                    return Finish(trace, actions);
                case AutomationBlockType.AtualizarCrm:
                    stage = Enum.Parse<KanbanStage>(param, true);
                    actions.Add(new(AutomationActionType.AtualizarEtapa, stage.ToString())); note = "Mover card para " + KanbanRules.Title(stage); break;
                case AutomationBlockType.AdicionarTag:
                    if (!tags.Contains(param, StringComparer.OrdinalIgnoreCase)) { tags.Add(param); actions.Add(new(AutomationActionType.AdicionarTag, param)); }
                    note = "Tag: " + param; break;
                case AutomationBlockType.TransferirAtendente:
                    actions.Add(new(AutomationActionType.Transferir, param.Length > 0 ? param : "Regra de automação")); note = "Transferir para atendente humano (IA pausa)"; break;
                case AutomationBlockType.Encerrar:
                    trace.Add($"{b.Id} Encerrar fluxo"); return Finish(trace, actions);
                default: note = b.Type.ToString(); break;
            }
            trace.Add($"{b.Id} {note}");
            id = next;
        }
        return Finish(trace, actions);
    }

    private static AutomationRun Finish(List<string> trace, List<AutomationAction> actions)
    {
        // a IA não responde se o fluxo já respondeu (modelo) ou transferiu
        var suppress = actions.Any(a => a.Type is AutomationActionType.EnviarModelo or AutomationActionType.Transferir);
        return new(trace, actions, suppress, null);
    }

    public static (string Field, string Op, string Value)? ParseCondition(string text)
    {
        var parts = text.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3) return null;
        var field = parts[0].ToLowerInvariant();
        var op = parts[1].ToLowerInvariant();
        if (field is not ("etapa" or "tag" or "pontuacao" or "modo" or "mensagem" or "segmento" or "campanha")) return null;
        if (op is not ("=" or "!=" or ">" or ">=" or "<" or "<=" or "contem")) return null;
        if (field == "pontuacao" && !int.TryParse(parts[2], out _)) return null;
        if (field != "pontuacao" && op is ">" or ">=" or "<" or "<=") return null;
        if (field == "etapa" && !Enum.TryParse<KanbanStage>(parts[2], true, out _)) return null;
        if (field == "modo" && !Enum.TryParse<ConversationMode>(parts[2], true, out _)) return null;
        return (field, op, parts[2]);
    }

    private static bool Evaluate((string Field, string Op, string Value) c, AutomationInput i)
    {
        if (c.Field == "pontuacao")
        {
            var v = int.Parse(c.Value, CultureInfo.InvariantCulture);
            return c.Op switch { "=" => i.Score == v, "!=" => i.Score != v, ">" => i.Score > v, ">=" => i.Score >= v, "<" => i.Score < v, "<=" => i.Score <= v, _ => false };
        }
        if (c.Field == "tag")
        {
            var has = i.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(t => t.Equals(c.Value, StringComparison.OrdinalIgnoreCase));
            return c.Op == "!=" ? !has : has;
        }
        var actual = c.Field switch
        {
            "etapa" => i.Stage.ToString(),
            "modo" => i.Mode.ToString(),
            "mensagem" => i.Message,
            "segmento" => i.Segment ?? "",
            "campanha" => i.Campaign ?? "",
            _ => "",
        };
        return c.Op switch
        {
            "=" => Norm(actual) == Norm(c.Value),
            "!=" => Norm(actual) != Norm(c.Value),
            "contem" => Norm(actual).Contains(Norm(c.Value)),
            _ => false,
        };
    }

    private static string Norm(string s) => PainClassifier.Strip(s.Trim().ToLowerInvariant());
    private static string Preview(string s) => s.Length > 60 ? s[..60] + "…" : s;

    private static IEnumerable<List<string>> Cycles(Dictionary<string, AutomationBlock> byId)
    {
        var found = new List<List<string>>();
        var state = new Dictionary<string, int>(); // 0 novo, 1 na pilha, 2 visitado
        var path = new List<string>();
        void Dfs(string id)
        {
            state[id] = 1; path.Add(id);
            foreach (var n in new[] { byId[id].Next, byId[id].NextNo }.Where(n => n is not null && byId.ContainsKey(n)))
            {
                if (state.GetValueOrDefault(n!) == 1) found.Add(path.Skip(path.IndexOf(n!)).ToList());
                else if (state.GetValueOrDefault(n!) == 0) Dfs(n!);
            }
            path.RemoveAt(path.Count - 1); state[id] = 2;
        }
        foreach (var id in byId.Keys) if (state.GetValueOrDefault(id) == 0) Dfs(id);
        return found;
    }

    /// <summary>Caminhos simples a partir do início (até 200), parando em Aguardar resposta/Encerrar.</summary>
    private static IEnumerable<List<string>> Paths(Dictionary<string, AutomationBlock> byId, string start)
    {
        var result = new List<List<string>>();
        void Walk(string id, List<string> path)
        {
            if (result.Count >= 200 || path.Contains(id)) { if (result.Count < 200) result.Add(path); return; }
            var p = new List<string>(path) { id };
            var b = byId[id];
            var nexts = new[] { b.Next, b.NextNo }.Where(n => n is not null).ToList();
            if (b.Type is AutomationBlockType.AguardarResposta or AutomationBlockType.Encerrar || nexts.Count == 0) { result.Add(p); return; }
            foreach (var n in nexts) Walk(n!, p);
        }
        Walk(start, []);
        return result;
    }
}
