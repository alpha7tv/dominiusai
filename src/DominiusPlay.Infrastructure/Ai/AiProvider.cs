using System.Text.Json.Nodes;

namespace DominiusPlay.Infrastructure.Ai;

/// <summary>Chamada de ferramenta pedida pelo modelo.</summary>
public sealed record AiToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>Definição de ferramenta: nome, descrição e JSON Schema (objeto) da entrada.</summary>
public sealed record AiToolDef(string Name, string Description, JsonObject InputSchema);

/// <summary>Um turno da conversa enviado ao modelo (o prompt de sistema vai à parte).</summary>
public abstract record AiTurn;
public sealed record AiUserText(string Text) : AiTurn;
public sealed record AiAssistantText(string Text) : AiTurn;
/// <summary>Resposta do modelo que pediu ferramentas. <see cref="ProviderState"/> guarda os blocos originais (inclusive raciocínio assinado) para reenviar sem alteração.</summary>
public sealed record AiAssistantToolCalls(string? Text, IReadOnlyList<AiToolCall> Calls, object? ProviderState) : AiTurn;
public sealed record AiToolResults(IReadOnlyList<AiToolResult> Results) : AiTurn;
public sealed record AiToolResult(string CallId, string Content);

public sealed record AiRequest(string ApiKey, string Model, string System, IReadOnlyList<AiTurn> Turns, IReadOnlyList<AiToolDef>? Tools,
    int MaxTokens, bool AllowTools = true);

public sealed record AiCompletion(bool Ok, string? Content, IReadOnlyList<AiToolCall> ToolCalls, object? ProviderState,
    int InputTokens, int OutputTokens, string? Error, int HttpStatus, string? StopReason = null)
{
    public AiAssistantToolCalls AsTurn() => new(Content, ToolCalls, ProviderState);
}

public interface IAiProvider
{
    Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken ct = default);
    Task<(bool Ok, IReadOnlyList<string> Models, string? Error)> ListModelsAsync(string apiKey, CancellationToken ct = default);
}
