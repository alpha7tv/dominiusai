using System.Collections.Concurrent;

namespace DominiusPlay.Server.Services;

/// <summary>
/// Bloqueio por conversa: garante que fila de envio, processamento de webhook e ações de atendentes
/// sobre o mesmo lead sejam serializados. Complementado pelo token de concorrência otimista (Lead.ConcurrencyStamp)
/// no banco, que protege contra conflitos entre múltiplos processos.
/// </summary>
public class ConversationLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(Guid leadId, CancellationToken ct = default)
    {
        var s = _locks.GetOrAdd(leadId, _ => new SemaphoreSlim(1, 1));
        await s.WaitAsync(ct);
        return new Releaser(s);
    }

    private sealed class Releaser(SemaphoreSlim s) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) s.Release(); }
    }
}
