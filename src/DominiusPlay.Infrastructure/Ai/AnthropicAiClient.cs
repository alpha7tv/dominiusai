using System.Collections.Concurrent;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace DominiusPlay.Infrastructure.Ai;

/// <summary>
/// Provedor de IA da Anthropic (Claude) usando o SDK oficial. A chave vem das Configurações (guardada criptografada no servidor).
/// </summary>
public sealed class AnthropicAiClient(HttpClient http) : IAiProvider
{
    public const string DefaultModel = "claude-haiku-5-5";
    public const string DefaultAdvancedModel = "claude-sonnet-5-5";

    private readonly ConcurrentDictionary<string, AnthropicClient> _clients = new();

    private AnthropicClient ClientFor(string apiKey) => _clients.GetOrAdd(apiKey, k => new AnthropicClient
    {
        ApiKey = k,
        HttpClient = http,
        MaxRetries = 2,
        BaseUrl = "https://api.anthropic.com",
    });

    /// <summary>Esforço de raciocínio: baixo no modelo rápido (atendimento), médio nos modelos avançados (negociação).</summary>
    public static Effort EffortFor(string model) =>
        model.Contains("sonnet", StringComparison.OrdinalIgnoreCase) || model.Contains("opus", StringComparison.OrdinalIgnoreCase) ? Effort.Medium : Effort.Low;

    public async Task<AiCompletion> CompleteAsync(AiRequest r, CancellationToken ct = default)
    {
        try
        {
            var p = new MessageCreateParams
            {
                Model = r.Model,
                MaxTokens = r.MaxTokens,
                System = new List<TextBlockParam> { new() { Text = r.System, CacheControl = new CacheControlEphemeral() } },
                Messages = BuildMessages(r.Turns),
                OutputConfig = new OutputConfig { Effort = EffortFor(r.Model) },
            };
            if (r.Tools is { Count: > 0 })
            {
                p = p with
                {
                    Tools = r.Tools.Select(t => (ToolUnion)new Tool
                    {
                        Name = t.Name,
                        Description = t.Description,
                        InputSchema = new()
                        {
                            Properties = t.InputSchema["properties"] is { } props
                                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(props.ToJsonString())!
                                : new Dictionary<string, JsonElement>(),
                            Required = t.InputSchema["required"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [],
                        },
                    }).ToList(),
                };
                if (!r.AllowTools) p = p with { ToolChoice = new ToolChoiceNone() };
            }

            var msg = await ClientFor(r.ApiKey).Messages.Create(p, ct);

            var echo = new List<ContentBlockParam>();
            var calls = new List<AiToolCall>();
            var text = new List<string>();
            foreach (var b in msg.Content)
            {
                if (b.TryPickText(out var t)) { text.Add(t.Text); echo.Add(new TextBlockParam { Text = t.Text }); }
                else if (b.TryPickThinking(out var th)) echo.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                else if (b.TryPickRedactedThinking(out var rt)) echo.Add(new RedactedThinkingBlockParam { Data = rt.Data });
                else if (b.TryPickToolUse(out var tu))
                {
                    echo.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                    calls.Add(new AiToolCall(tu.ID, tu.Name, JsonSerializer.Serialize(tu.Input)));
                }
            }
            var u = msg.Usage;
            var input = (int)(u.InputTokens + (u.CacheReadInputTokens ?? 0) + (u.CacheCreationInputTokens ?? 0));
            var output = (int)u.OutputTokens;
            var stop = msg.StopReason?.Raw();
            if (stop == "refusal")
                return new AiCompletion(false, null, [], null, input, output, "Claude recusou responder esta mensagem", 200, stop);
            var content = text.Count == 0 ? null : string.Join("\n", text).Trim();
            return new AiCompletion(true, content, calls, echo, input, output, null, 200, stop);
        }
        catch (AnthropicApiException ex)
        {
            return new AiCompletion(false, null, [], null, 0, 0, $"Anthropic HTTP {(int)ex.StatusCode}: {ex.Message}", (int)ex.StatusCode);
        }
        catch (AnthropicException ex)
        {
            return new AiCompletion(false, null, [], null, 0, 0, "Anthropic: " + ex.Message, 0);
        }
        catch (HttpRequestException ex)
        {
            return new AiCompletion(false, null, [], null, 0, 0, "Falha de rede ao chamar a Anthropic: " + ex.Message, 0);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AiCompletion(false, null, [], null, 0, 0, "Tempo esgotado ao chamar a Anthropic", 0);
        }
    }

    public async Task<(bool Ok, IReadOnlyList<string> Models, string? Error)> ListModelsAsync(string apiKey, CancellationToken ct = default)
    {
        try
        {
            var page = await ClientFor(apiKey).Models.List(cancellationToken: ct);
            return (true, page.Items.Select(m => m.ID).Where(id => id.StartsWith("claude-")).OrderBy(x => x).ToList(), null);
        }
        catch (AnthropicApiException ex) { return (false, [], $"Anthropic HTTP {(int)ex.StatusCode}: {ex.Message}"); }
        catch (Exception ex) when (ex is AnthropicException or HttpRequestException or TaskCanceledException) { return (false, [], ex.Message); }
    }

    /// <summary>Converte os turnos para mensagens da API, juntando turnos seguidos do mesmo papel. A conversa sempre começa e termina com o cliente (sem prefill).</summary>
    public static List<MessageParam> BuildMessages(IReadOnlyList<AiTurn> turns)
    {
        var groups = new List<(bool User, List<ContentBlockParam> Blocks)>();
        void Add(bool user, IEnumerable<ContentBlockParam> blocks)
        {
            if (groups.Count == 0 || groups[^1].User != user) groups.Add((user, []));
            groups[^1].Blocks.AddRange(blocks);
        }
        foreach (var t in turns)
            switch (t)
            {
                case AiUserText u: Add(true, [new TextBlockParam { Text = u.Text }]); break;
                case AiAssistantText a: Add(false, [new TextBlockParam { Text = a.Text }]); break;
                case AiToolResults tr: Add(true, tr.Results.Select(x => (ContentBlockParam)new ToolResultBlockParam { ToolUseID = x.CallId, Content = x.Content })); break;
                case AiAssistantToolCalls c when c.ProviderState is List<ContentBlockParam> echo:
                    // blocos de raciocínio assinados só valem com exatamente o mesmo conteúdo antes deles: nunca juntar a outra fala do assistente
                    if (groups.Count == 0 || !groups[^1].User) Add(true, [new TextBlockParam { Text = "[Continue]" }]);
                    Add(false, echo);
                    break;
                case AiAssistantToolCalls c:
                    var blocks = new List<ContentBlockParam>();
                    if (!string.IsNullOrWhiteSpace(c.Text)) blocks.Add(new TextBlockParam { Text = c.Text });
                    blocks.AddRange(c.Calls.Select(x => (ContentBlockParam)new ToolUseBlockParam
                    {
                        ID = x.Id, Name = x.Name,
                        Input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(string.IsNullOrWhiteSpace(x.ArgumentsJson) ? "{}" : x.ArgumentsJson)!,
                    }));
                    Add(false, blocks);
                    break;
            }
        if (groups.Count == 0 || !groups[0].User) groups.Insert(0, (true, [new TextBlockParam { Text = "[Início da conversa]" }]));
        if (!groups[^1].User) groups.Add((true, [new TextBlockParam { Text = "[Sistema: escreva agora a próxima mensagem para o cliente, continuando a conversa.]" }]));
        return groups.Select(g => g.User
            ? (MessageParam)new UserMessageParam { Content = g.Blocks }
            : new AssistantMessageParam { Content = g.Blocks }).ToList();
    }
}
