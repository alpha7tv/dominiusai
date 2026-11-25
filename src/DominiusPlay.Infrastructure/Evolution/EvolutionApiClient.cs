using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DominiusPlay.Infrastructure.Evolution;

public sealed record EvolutionConfig(string BaseUrl, string InstanceName, string ApiKey);

public sealed record EvolutionSendResult(bool Success, string? MessageId, string? Status, string? Error, int HttpStatus);

public sealed record EvolutionConnectionState(bool Reachable, string? State, string? Error)
{
    public bool IsOpen => string.Equals(State, "open", StringComparison.OrdinalIgnoreCase);
}

public interface IEvolutionApi
{
    Task<EvolutionConnectionState> GetConnectionStateAsync(EvolutionConfig cfg, CancellationToken ct = default);
    Task<EvolutionSendResult> SendTextAsync(EvolutionConfig cfg, string number, string text, CancellationToken ct = default);
    Task<bool> SendPresenceAsync(EvolutionConfig cfg, string number, string presence, int delayMs, CancellationToken ct = default);
    Task<(bool Ok, string Body)> SetWebhookAsync(EvolutionConfig cfg, string url, IDictionary<string, string> headers, IEnumerable<string> events, CancellationToken ct = default);
    Task<(bool Ok, string Body)> FindWebhookAsync(EvolutionConfig cfg, CancellationToken ct = default);
    Task<(bool Ok, string Body)> FetchInstancesAsync(EvolutionConfig cfg, CancellationToken ct = default);
    Task<IReadOnlyList<(string Number, bool Exists, string? Jid)>> CheckWhatsAppNumbersAsync(EvolutionConfig cfg, IEnumerable<string> numbers, CancellationToken ct = default);
}

/// <summary>
/// Cliente da Evolution API v2 (verificado contra o código-fonte EvolutionAPI/evolution-api 2.3.x):
///   GET  /instance/connectionState/{instance}         → { instance: { instanceName, state } }
///   GET  /instance/fetchInstances?instanceName=...
///   POST /message/sendText/{instance}  { number, text } → { key: { id, remoteJid, fromMe }, status, ... }
///   POST /chat/sendPresence/{instance} { number, presence: "composing", delay }
///   POST /chat/whatsappNumbers/{instance} { numbers: [...] }
///   POST /webhook/set/{instance}  { webhook: { enabled, url, headers, byEvents, base64, events } }
///   GET  /webhook/find/{instance}
/// Autenticação: header "apikey". Se a URL tiver usuário e senha (https://usuario:senha@host), eles são
/// enviados como autenticação básica (Authorization: Basic) para passar por um nginx protegido com senha
/// na frente da Evolution; o usuário e a senha saem da URL e nunca vão no caminho da requisição.
/// </summary>
public class EvolutionApiClient(HttpClient http) : IEvolutionApi
{
    public static readonly string[] DefaultWebhookEvents =
        ["MESSAGES_UPSERT", "MESSAGES_UPDATE", "SEND_MESSAGE", "CONNECTION_UPDATE"];

    private HttpRequestMessage Req(HttpMethod m, EvolutionConfig cfg, string path, object? body = null)
    {
        var (baseUrl, basic) = SplitCredentials(cfg.BaseUrl);
        var r = new HttpRequestMessage(m, baseUrl.TrimEnd('/') + path);
        r.Headers.Add("apikey", cfg.ApiKey);
        if (basic is not null) r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    private static string Inst(EvolutionConfig cfg) => Uri.EscapeDataString(cfg.InstanceName);

    public async Task<EvolutionConnectionState> GetConnectionStateAsync(EvolutionConfig cfg, CancellationToken ct = default)
    {
        try
        {
            using var res = await http.SendAsync(Req(HttpMethod.Get, cfg, $"/instance/connectionState/{Inst(cfg)}"), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return new(true, null, HttpError(res, body));
            var node = JsonNode.Parse(body);
            var state = node?["instance"]?["state"]?.GetValue<string>() ?? node?["state"]?.GetValue<string>();
            return new(true, state, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(false, null, ex.Message);
        }
    }

    public async Task<EvolutionSendResult> SendTextAsync(EvolutionConfig cfg, string number, string text, CancellationToken ct = default)
    {
        try
        {
            using var res = await http.SendAsync(Req(HttpMethod.Post, cfg, $"/message/sendText/{Inst(cfg)}", new { number, text }), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return new(false, null, null, HttpError(res, body), (int)res.StatusCode);
            var node = JsonNode.Parse(body);
            return new(true, node?["key"]?["id"]?.GetValue<string>(), node?["status"]?.GetValue<string>(), null, (int)res.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(false, null, null, ex.Message, 0);
        }
    }

    public async Task<bool> SendPresenceAsync(EvolutionConfig cfg, string number, string presence, int delayMs, CancellationToken ct = default)
    {
        try
        {
            using var res = await http.SendAsync(Req(HttpMethod.Post, cfg, $"/chat/sendPresence/{Inst(cfg)}", new { number, presence, delay = delayMs }), ct);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }

    public async Task<(bool Ok, string Body)> SetWebhookAsync(EvolutionConfig cfg, string url, IDictionary<string, string> headers, IEnumerable<string> events, CancellationToken ct = default)
    {
        var payload = new { webhook = new { enabled = true, url, headers, byEvents = false, base64 = false, events = events.ToArray() } };
        return await Raw(Req(HttpMethod.Post, cfg, $"/webhook/set/{Inst(cfg)}", payload), ct);
    }

    public Task<(bool Ok, string Body)> FindWebhookAsync(EvolutionConfig cfg, CancellationToken ct = default) =>
        Raw(Req(HttpMethod.Get, cfg, $"/webhook/find/{Inst(cfg)}"), ct);

    public Task<(bool Ok, string Body)> FetchInstancesAsync(EvolutionConfig cfg, CancellationToken ct = default) =>
        Raw(Req(HttpMethod.Get, cfg, $"/instance/fetchInstances?instanceName={Inst(cfg)}"), ct);

    public async Task<IReadOnlyList<(string Number, bool Exists, string? Jid)>> CheckWhatsAppNumbersAsync(EvolutionConfig cfg, IEnumerable<string> numbers, CancellationToken ct = default)
    {
        var (ok, body) = await Raw(Req(HttpMethod.Post, cfg, $"/chat/whatsappNumbers/{Inst(cfg)}", new { numbers = numbers.ToArray() }), ct);
        if (!ok) return [];
        var arr = JsonNode.Parse(body)?.AsArray();
        return arr?.Select(n => (n?["number"]?.GetValue<string>() ?? "", n?["exists"]?.GetValue<bool>() ?? false, n?["jid"]?.GetValue<string>())).ToList()
               ?? [];
    }

    private async Task<(bool, string)> Raw(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            return (res.IsSuccessStatusCode, res.IsSuccessStatusCode ? body : HttpError(res, body));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (false, ex.Message); }
    }

    /// <summary>Separa "https://usuario:senha@host" em URL sem credenciais + valor Base64 da autenticação básica.</summary>
    public static (string Url, string? Basic) SplitCredentials(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.UserInfo)) return (url, null);
        var clean = new UriBuilder(u) { UserName = "", Password = "" }.Uri.GetLeftPart(UriPartial.Path);
        var raw = Uri.UnescapeDataString(u.UserInfo);
        return (clean, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw)));
    }

    /// <summary>Monta a mensagem de erro HTTP, explicando em português os 401 mais comuns.</summary>
    private static string HttpError(HttpResponseMessage res, string body)
    {
        var code = (int)res.StatusCode;
        if (code == 401 && body.Contains("<html", StringComparison.OrdinalIgnoreCase))
            return "HTTP 401: o servidor web (nginx) na frente da Evolution API pediu usuário e senha antes de chegar na Evolution. " +
                   "Confira a URL do servidor em Integrações (deve ser o endereço da Evolution API) ou, se esse nginx tem senha de propósito, " +
                   "informe-a na própria URL: https://usuario:senha@seu-servidor.";
        if (code == 401)
            return $"HTTP 401: a Evolution API recusou a API Key. Confira a API Key em Integrações. ({Trim(body)})";
        return $"HTTP {code}: {Trim(body)}";
    }

    private static string Trim(string s) => s.Length > 500 ? s[..500] + "…" : s;
}
