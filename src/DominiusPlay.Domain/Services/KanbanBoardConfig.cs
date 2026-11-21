using System.Text.Json;
using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Domain.Services;

public sealed record KanbanStageConfig(KanbanStage Stage, string Title, int Order, bool Visible);

/// <summary>Etapas do Kanban configuráveis (nome, ordem e visibilidade), guardadas em Configurações. Etapas ocultas com cards continuam aparecendo.</summary>
public static class KanbanBoardConfig
{
    public const string SettingKey = "kanban.stages";

    /// <summary>Funil padrão: as 9 etapas do projeto de prospecção (com os nomes que o CRM já usava) + "Em fase de teste" (IPTV). "Em dúvidas" fica oculta até ter card. Nomes editáveis.</summary>
    public static IReadOnlyList<KanbanStageConfig> Default { get; } =
    [
        new(KanbanStage.PrimeiroContato, KanbanRules.Title(KanbanStage.PrimeiroContato), 1, true),
        new(KanbanStage.AbordagemRealizada, KanbanRules.Title(KanbanStage.AbordagemRealizada), 2, true),
        new(KanbanStage.Respondeu, KanbanRules.Title(KanbanStage.Respondeu), 3, true),
        new(KanbanStage.EmAtendimento, KanbanRules.Title(KanbanStage.EmAtendimento), 4, true),
        new(KanbanStage.Qualificado, KanbanRules.Title(KanbanStage.Qualificado), 5, true),
        new(KanbanStage.EmFaseDeTeste, KanbanRules.Title(KanbanStage.EmFaseDeTeste), 6, true),
        new(KanbanStage.PropostaEnviada, KanbanRules.Title(KanbanStage.PropostaEnviada), 7, true),
        new(KanbanStage.EmNegociacao, KanbanRules.Title(KanbanStage.EmNegociacao), 8, true),
        new(KanbanStage.NegocioFechado, KanbanRules.Title(KanbanStage.NegocioFechado), 9, true),
        new(KanbanStage.NaoTemInteresse, KanbanRules.Title(KanbanStage.NaoTemInteresse), 10, true),
        new(KanbanStage.EmDuvidas, KanbanRules.Title(KanbanStage.EmDuvidas), 11, false),
    ];

    public static IReadOnlyList<KanbanStageConfig> Parse(string? json)
    {
        List<KanbanStageConfig>? saved = null;
        if (!string.IsNullOrWhiteSpace(json))
            try { saved = JsonSerializer.Deserialize<List<KanbanStageConfig>>(json); } catch (JsonException) { }
        var map = (saved ?? []).Where(s => Enum.IsDefined(s.Stage)).GroupBy(s => s.Stage).ToDictionary(g => g.Key, g => g.First());
        // etapa nova (versão futura) entra com o padrão
        return Default.Select(d => map.TryGetValue(d.Stage, out var s) ? s with { Title = string.IsNullOrWhiteSpace(s.Title) ? d.Title : s.Title.Trim() } : d)
            .OrderBy(s => s.Order).ThenBy(s => (int)s.Stage).ToList();
    }

    /// <summary>Valida o que o administrador enviou. Retorna erro ou null.</summary>
    public static string? Validate(IReadOnlyList<KanbanStageConfig> stages)
    {
        if (stages.Select(s => s.Stage).Distinct().Count() != stages.Count) return "Etapa repetida";
        if (stages.Any(s => !Enum.IsDefined(s.Stage))) return "Etapa desconhecida";
        if (stages.Any(s => string.IsNullOrWhiteSpace(s.Title) || s.Title.Trim().Length > 40)) return "Cada etapa precisa de um nome com até 40 caracteres";
        if (stages.Where(s => s.Visible).Select(s => s.Title.Trim().ToLowerInvariant()).Distinct().Count() != stages.Count(s => s.Visible)) return "Duas etapas visíveis com o mesmo nome";
        if (stages.Count(s => s.Visible) < 2) return "Deixe ao menos duas etapas visíveis";
        foreach (var must in new[] { KanbanStage.PrimeiroContato, KanbanStage.NegocioFechado, KanbanStage.NaoTemInteresse })
            if (stages.FirstOrDefault(s => s.Stage == must) is { Visible: false }) return $"A etapa \"{KanbanRules.Title(must)}\" é usada pelas regras do sistema e não pode ser ocultada";
        return null;
    }
}
