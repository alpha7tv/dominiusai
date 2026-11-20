namespace DominiusPlay.Domain.Services;

/// <summary>Detecta pedidos explícitos de descadastro/parada nas mensagens recebidas (Regra 8).</summary>
public static class OptOutDetector
{
    private static readonly string[] Phrases =
    [
        "sair da lista", "parar de mandar", "pare de mandar", "nao quero receber", "nao me mande", "nao mande mais",
        "descadastrar", "descadastro", "remover meu numero", "remova meu numero", "me tira da lista", "me tire da lista",
        "nao tenho interesse", "stop", "cancelar inscricao", "bloquear",
    ];

    public static bool IsOptOut(string text)
    {
        var t = PainClassifier.Strip(text.Trim().ToLowerInvariant());
        if (t is "sair" or "parar" or "pare" or "stop") return true;
        return Phrases.Any(p => t.Contains(p));
    }
}
