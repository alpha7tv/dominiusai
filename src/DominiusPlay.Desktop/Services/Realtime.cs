using DominiusPlay.Domain.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace DominiusPlay.Desktop.Services;

/// <summary>Recebe eventos em tempo real do servidor (novas mensagens, cards alterados, estado do WhatsApp).</summary>
public class Realtime : IAsyncDisposable
{
    private HubConnection? _conn;
    public event Action<Guid>? LeadChanged;
    public event Action<Guid>? MessageAdded;
    public event Action<string?>? ConnectionChanged;
    public bool IsConnected => _conn?.State == HubConnectionState.Connected;

    public async Task StartAsync(ApiClient api)
    {
        _conn = new HubConnectionBuilder()
            .WithUrl(api.BaseUrl + "/hubs/crm", o => o.AccessTokenProvider = () => Task.FromResult(api.Token))
            .WithAutomaticReconnect()
            .Build();
        _conn.On<Guid>(HubEvents.LeadChanged, id => LeadChanged?.Invoke(id));
        _conn.On<Guid>(HubEvents.MessageAdded, id => MessageAdded?.Invoke(id));
        _conn.On<string?>(HubEvents.ConnectionChanged, s => ConnectionChanged?.Invoke(s));
        try { await _conn.StartAsync(); } catch { /* reconecta automaticamente; telas continuam com atualização manual */ }
    }

    public async ValueTask DisposeAsync() { if (_conn is not null) await _conn.DisposeAsync(); }
}
