using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace DominiusPlay.Infrastructure.Ai;

public sealed record AiToolCall(string Id, string Name, string ArgumentsJson);

public sealed record AiCompletion(bool Ok, string? Content, IReadOnlyList<AiToolCall> ToolCalls, JsonObject? RawAssistantMessage,
    int PromptTokens, int CompletionTokens, string? Error, int HttpStatus);

public interface IAiProvider
{
    Task<AiCompletion> CompleteAsync(string apiKey, string model, JsonArray messages, JsonArray? tools, double? temperature, int maxTokens, CancellationToken ct = default);
    Task<(bool Ok, IReadOnlyList<string> Models, string? Error)> ListModelsAsync(string apiKey, CancellationToken ct = default);
}

/// <summary>
/// Cliente da API de Chat Completions da OpenAI (POST /v1/chat/completions) com chamada de ferramentas (function calling).
/// A chave é recebida a cada chamada (vinda do armazenamento criptografado) e nunca fica em memória estática.
/// </summary>
public class OpenAiClient(HttpClient http) : IAiProvider
{
    public const string BaseUrl = "https://api.openai.com/v1/";

    /// <summary>Modelos de raciocínio (gpt-5*, o*) não aceitam temperatura diferente da padrão.</summary>
    public static bool SupportsTemperature(string model) =>
        !(model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) || model.StartsWith("o1") || model.StartsWith("o3") || model.StartsWith("o4"));

    public async Task<AiCompletion> CompleteAsync(string apiKey, string model, JsonArray messages, JsonArray? tools, double? temperature, int maxTokens, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages.DeepClone(),
            ["max_completion_tokens"] = maxTokens,
        };
        if (tools is { Count: > 0 }) { body["tools"] = tools.DeepClone(); body["tool_choice"] = "auto"; }
        if (temperature is { } t && SupportsTemperature(model)) body["temperature"] = t;

        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "chat/completions")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                var msg = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? text;
                return new(false, null, [], null, 0, 0, $"OpenAI HTTP {(int)res.StatusCode}: {Trunc(msg)}", (int)res.StatusCode);
            }
            var root = JsonNode.Parse(text)!;
            var msgNode = root["choices"]?[0]?["message"]?.AsObject();
            var calls = new List<AiToolCall>();
            if (msgNode?["tool_calls"] is JsonArray tc)
                foreach (var c in tc)
                    calls.Add(new(c!["id"]!.GetValue<string>(), c["function"]!["name"]!.GetValue<string>(), c["function"]!["arguments"]?.GetValue<string>() ?? "{}"));
            return new(true, msgNode?["content"]?.GetValue<string>(), calls, msgNode?.DeepClone().AsObject(),
                root["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0, root["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0, null, (int)res.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new(false, null, [], null, 0, 0, "Falha ao chamar a OpenAI: " + ex.Message, 0);
        }
    }

    public async Task<(bool Ok, IReadOnlyList<string> Models, string? Error)> ListModelsAsync(string apiKey, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return (false, [], $"OpenAI HTTP {(int)res.StatusCode}: {Trunc(JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? text)}");
            var ids = JsonNode.Parse(text)?["data"]?.AsArray().Select(m => m!["id"]!.GetValue<string>())
                .Where(id => (id.StartsWith("gpt-") || id.StartsWith("o3") || id.StartsWith("o4"))
                             && !id.Contains("audio") && !id.Contains("tts") && !id.Contains("transcribe") && !id.Contains("realtime") && !id.Contains("image") && !id.Contains("search"))
                .OrderBy(x => x).ToList() ?? [];
            return (true, ids, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (false, [], ex.Message); }
    }

    private static string Trunc(string s) => s.Length > 400 ? s[..400] + "…" : s;
}
