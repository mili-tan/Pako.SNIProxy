using System.Collections.Concurrent;
using Pako.SNIProxy.Core;

namespace Pako.SNIProxy.Infrastructure;

public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<long, ConnectionContext> _connections = new();

    public void Add(ConnectionContext context) => _connections[context.Id] = context;

    public void Remove(long id) => _connections.TryRemove(id, out _);

    public int Count => _connections.Count;

    public IReadOnlyList<ConnectionContext> Snapshot() => _connections.Values.ToList();

    public bool TryDisconnect(long id)
    {
        if (_connections.TryRemove(id, out var context))
        {
            context.Dispose();
            return true;
        }
        return false;
    }
}
