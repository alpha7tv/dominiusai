using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DominiusPlay.Server.Hubs;

/// <summary>Canal em tempo real para os aplicativos Windows conectados (atualização de cards, mensagens e conexão).</summary>
[Authorize]
public class CrmHub : Hub;
