using System.Text.Json.Nodes;

namespace DominiusPlay.Infrastructure.Evolution;

public enum WebhookKind { MessageUpsert, MessageStatusUpdate, SendMessage, ConnectionUpdate, Other }

public sealed record ParsedWebhook(
    WebhookKind Kind,
    string EventName,
    string Instance,
    string DedupKey,
    string? MessageId = null,
    string? RemoteJid = null,
    string? Phone = null,
    bool FromMe = false,
    string? Text = null,
    string? PushName = null,
    string? Status = null,
    string? ConnectionState = null,
    DateTimeOffset? Timestamp = null,
    bool IsGroup = false,
    string? MessageType = null);

/// <summary>
/// Interpreta o corpo enviado pela Evolution API v2 ao webhook:
/// { event: "messages.upsert", instance, data: { key: { id, remoteJid, remoteJidAlt?, fromMe }, pushName, message: { conversation | extendedTextMessage.text | ... }, messageType, messageTimestamp, status }, ... }
/// messages.update: data = { keyId, remoteJid, fromMe, status }
/// connection.update: data = { instance, state, statusReason }
/// </summary>
public static class EvolutionWebhookParser
{
    public static ParsedWebhook Parse(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new FormatException("Webhook vazio");
        var ev = (root["event"]?.GetValue<string>() ?? "").ToLowerInvariant().Replace('_', '.');
        var instance = root["instance"]?.GetValue<string>() ?? "";
        var data = root["data"];
        // Alguns envios (messages.set) trazem array; tratamos só o primeiro item quando aplicável.
        if (data is JsonArray arr) data = arr.FirstOrDefault();

        switch (ev)
        {
            case "messages.upsert":
            case "send.message":
            {
                var key = data?["key"];
                var id = key?["id"]?.GetValue<string>() ?? "";
                var jid = key?["remoteJid"]?.GetValue<string>() ?? "";
                if (jid.EndsWith("@lid") && key?["remoteJidAlt"]?.GetValue<string>() is { Length: > 0 } alt) jid = alt;
                var fromMe = key?["fromMe"]?.GetValue<bool>() ?? false;
                var msg = data?["message"];
                var text = msg?["conversation"]?.GetValue<string>()
                           ?? msg?["extendedTextMessage"]?["text"]?.GetValue<string>()
                           ?? msg?["imageMessage"]?["caption"]?.GetValue<string>()
                           ?? msg?["videoMessage"]?["caption"]?.GetValue<string>()
                           ?? msg?["buttonsResponseMessage"]?["selectedDisplayText"]?.GetValue<string>()
                           ?? msg?["listResponseMessage"]?["title"]?.GetValue<string>();
                var type = data?["messageType"]?.GetValue<string>();
                if (text is null && type is not null) text = $"[{type}]";
                DateTimeOffset? ts = null;
                if (data?["messageTimestamp"] is JsonValue tv && tv.TryGetValue<long>(out var unix)) ts = DateTimeOffset.FromUnixTimeSeconds(unix);
                var kind = ev == "send.message" ? WebhookKind.SendMessage : WebhookKind.MessageUpsert;
                return new(kind, ev, instance, $"{instance}:msg:{id}", id, jid, JidToPhone(jid), fromMe, text,
                    data?["pushName"]?.GetValue<string>(), data?["status"]?.GetValue<string>(), null, ts, jid.EndsWith("@g.us"), type);
            }
            case "messages.update":
            {
                var id = data?["keyId"]?.GetValue<string>() ?? data?["key"]?["id"]?.GetValue<string>() ?? "";
                var status = data?["status"]?.ToString() ?? "";
                var jid = data?["remoteJid"]?.GetValue<string>() ?? "";
                return new(WebhookKind.MessageStatusUpdate, ev, instance, $"{instance}:status:{id}:{status}", id, jid, JidToPhone(jid),
                    data?["fromMe"]?.GetValue<bool>() ?? false, Status: status);
            }
            case "connection.update":
            {
                var state = data?["state"]?.GetValue<string>();
                var stamp = root["date_time"]?.GetValue<string>() ?? DateTimeOffset.UtcNow.ToString("O");
                return new(WebhookKind.ConnectionUpdate, ev, instance, $"{instance}:conn:{state}:{stamp}", ConnectionState: state);
            }
            default:
                return new(WebhookKind.Other, ev, instance, $"{instance}:{ev}:{Guid.NewGuid():N}");
        }
    }

    public static string? JidToPhone(string? jid)
    {
        if (string.IsNullOrEmpty(jid) || !jid.EndsWith("@s.whatsapp.net")) return null;
        var p = jid[..jid.IndexOf('@')];
        var colon = p.IndexOf(':');
        return colon > 0 ? p[..colon] : p;
    }

    /// <summary>Converte o status textual da Evolution (ERROR, PENDING, SERVER_ACK, DELIVERY_ACK, READ, PLAYED).</summary>
    public static Domain.Enums.MessageStatus? MapStatus(string? s) => s?.ToUpperInvariant() switch
    {
        "ERROR" => Domain.Enums.MessageStatus.Falhou,
        "PENDING" => Domain.Enums.MessageStatus.Pendente,
        "SERVER_ACK" => Domain.Enums.MessageStatus.EnviadaServidor,
        "DELIVERY_ACK" => Domain.Enums.MessageStatus.Entregue,
        "READ" or "PLAYED" => Domain.Enums.MessageStatus.Lida,
        _ => null,
    };
}
