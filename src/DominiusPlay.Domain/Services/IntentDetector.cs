using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Domain.Services;

/// <summary>
/// Identificação determinística da intenção da mensagem recebida (roda antes da IA, sem custo).
/// Pedidos de descadastro e de atendimento humano agem na hora, sem depender do modelo; as demais intenções
/// orientam a IA e os relatórios. A IA pode refinar com a ferramenta classificar_intencao.
/// </summary>
public static class IntentDetector
{
    private static readonly (IntentCategory Intent, string[] Phrases)[] Rules =
    [
        (IntentCategory.PedidoHumano, ["falar com uma pessoa", "falar com alguem", "falar com atendente", "falar com um atendente", "quero um atendente",
            "atendente humano", "pessoa de verdade", "falar com humano", "falar com um humano", "falar com o dono", "falar com o responsavel",
            "falar com vendedor", "falar com um vendedor", "me liga", "pode me ligar", "quero falar com voce pessoalmente"]),
        (IntentCategory.SemInteresse, ["nao tenho interesse", "sem interesse", "nao me interessa", "nao preciso", "agora nao", "nao obrigado", "nao, obrigado"]),
        (IntentCategory.Reclamacao, ["reclamacao", "absurdo", "pessimo", "horrivel", "nao funciona", "parou de funcionar", "estou insatisfeito", "procon", "golpe", "enganado"]),
        (IntentCategory.IntencaoCompra, ["quero contratar", "quero assinar", "vou contratar", "quero fechar", "vamos fechar", "como faco para pagar", "como pago",
            "manda o pix", "me passa o pix", "chave pix", "quero comprar", "pode gerar", "quero o plano", "bora fechar"]),
        (IntentCategory.Negociacao, ["desconto", "faz por", "consegue fazer por", "melhorar o preco", "melhor preco", "abaixar", "parcelar", "condicao especial", "cupom"]),
        (IntentCategory.SolicitacaoPreco, ["quanto custa", "qual o valor", "qual valor", "preco", "valores", "quanto e", "quanto fica", "tabela", "mensalidade", "planos"]),
        (IntentCategory.Objecao, ["muito caro", "ta caro", "esta caro", "caro demais", "vou pensar", "nao confio", "ja tenho fornecedor", "ja uso outro", "depois eu vejo", "sem dinheiro"]),
        (IntentCategory.DuvidaTecnica, ["como funciona", "dns", "painel", "aplicativo", "app", "servidor", "travando", "trava", "configurar", "instalar", "credito", "creditos", "login", "sigma"]),
        (IntentCategory.InteresseComercial, ["tenho interesse", "gostaria de saber mais", "quero saber mais", "me explica", "me fala mais", "pode explicar", "interessado", "interessada", "quero conhecer"]),
    ];

    private static readonly string[] Greetings = ["oi", "ola", "bom dia", "boa tarde", "boa noite", "opa", "e ai", "eai", "quem e", "quem fala", "quem e voce"];

    /// <summary>Retorna a intenção mais relevante da mensagem (ordem de prioridade: descadastro, humano, sem interesse, reclamação, compra...).</summary>
    public static IntentCategory Detect(string? text, bool isFirstInbound = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return IntentCategory.Nenhuma;
        if (OptOutDetector.IsOptOut(text) && !IsOnlyNoInterest(text)) return IntentCategory.Descadastro;
        var t = Normalize(text);
        foreach (var (intent, phrases) in Rules)
            if (phrases.Any(p => Contains(t, p))) return intent;
        if (isFirstInbound || Greetings.Any(g => t.StartsWith(" " + g + " "))) return IntentCategory.PrimeiroContato;
        return IntentCategory.Nenhuma;
    }

    /// <summary>"Não tenho interesse" sem pedido de parar = SemInteresse (o descadastro continua sendo aplicado pelo OptOutDetector).</summary>
    private static bool IsOnlyNoInterest(string text)
    {
        var t = Normalize(text);
        var stop = new[] { "sair da lista", "parar de mandar", "pare de mandar", "nao quero receber", "nao me mande", "nao mande mais", "descadastr",
            "remover meu numero", "remova meu numero", "me tira da lista", "me tire da lista", "stop", "cancelar inscricao", "bloquear" };
        return (Contains(t, "nao tenho interesse") || Contains(t, "sem interesse")) && !stop.Any(s => t.Contains(s)) && t.Trim() is not ("sair" or "parar" or "pare");
    }

    /// <summary>Minúsculas, sem acentos, pontuação vira espaço, espaços simples, com espaço nas pontas (busca por palavra inteira).</summary>
    private static string Normalize(string s)
    {
        var chars = PainClassifier.Strip(s.ToLowerInvariant()).Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return " " + string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries)) + " ";
    }

    private static bool Contains(string normalized, string phrase) => normalized.Contains(" " + phrase + " ");

    public static string Title(IntentCategory i) => i switch
    {
        IntentCategory.PrimeiroContato => "Primeiro contato",
        IntentCategory.InteresseComercial => "Interesse comercial",
        IntentCategory.SolicitacaoPreco => "Solicitação de preços",
        IntentCategory.DuvidaTecnica => "Dúvida técnica",
        IntentCategory.Objecao => "Objeção",
        IntentCategory.Negociacao => "Negociação",
        IntentCategory.PedidoHumano => "Pedido de atendimento humano",
        IntentCategory.SemInteresse => "Cliente sem interesse",
        IntentCategory.Reclamacao => "Reclamação",
        IntentCategory.Descadastro => "Pedido de descadastramento",
        IntentCategory.IntencaoCompra => "Intenção de compra",
        _ => "Não identificada",
    };

    /// <summary>Ações padrão por intenção (o agente pode sobrescrever em AgentProfile.IntentActions).</summary>
    public static IntentAction DefaultAction(IntentCategory i) => i switch
    {
        IntentCategory.PedidoHumano => IntentAction.TransferirHumano,
        IntentCategory.IntencaoCompra => IntentAction.PriorizarEContinuar,
        IntentCategory.Reclamacao => IntentAction.PriorizarEContinuar,
        IntentCategory.Negociacao => IntentAction.PriorizarEContinuar,
        _ => IntentAction.Continuar,
    };

    /// <summary>Lê "Intencao=Acao;Intencao=Acao". Entradas inválidas são ignoradas.</summary>
    public static Dictionary<IntentCategory, IntentAction> ParseActions(string? config)
    {
        var map = Enum.GetValues<IntentCategory>().Where(i => i != IntentCategory.Nenhuma).ToDictionary(i => i, DefaultAction);
        foreach (var part in (config ?? "").Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2 && Enum.TryParse<IntentCategory>(kv[0], true, out var i) && Enum.TryParse<IntentAction>(kv[1], true, out var a)
                && i is not (IntentCategory.Nenhuma or IntentCategory.Descadastro)) // descadastro sempre encerra (regra fixa)
                map[i] = a;
        }
        map[IntentCategory.Descadastro] = IntentAction.Encerrar;
        return map;
    }

    public static string FormatActions(IReadOnlyDictionary<IntentCategory, IntentAction> map) =>
        string.Join(";", map.Where(kv => kv.Key is not (IntentCategory.Nenhuma or IntentCategory.Descadastro)).Select(kv => $"{kv.Key}={kv.Value}"));
}
