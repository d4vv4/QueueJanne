using System.Collections.Concurrent;
using ModelContextProtocol;
using QueueJanne.Core.Connections;
using QueueJanne.Core.Entities;
using QueueJanne.Core.ServiceBus;

namespace QueueJanne.Mcp;

/// <summary>One client per saved connection, plus a short-lived entity snapshot for resolving entity names.</summary>
public sealed class NamespaceClientCache(ConnectionStore store) : IAsyncDisposable
{
    private static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<string, INamespaceClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (NamespaceSnapshot Snapshot, DateTimeOffset LoadedAt)> _snapshots = new(StringComparer.Ordinal);

    public (ConnectionProfile Profile, INamespaceClient Client) Get(string connection)
    {
        var profile = store.Find(connection)
            ?? throw new McpException($"No saved connection called '{connection}'. Call list_connections, or add one with add_entra_connection.");
        var client = _clients.GetOrAdd(profile.Id, _ => NamespaceClientFactory.Create(profile, store, allowInteractiveSignIn: false));
        return (profile, client);
    }

    public async Task<NamespaceSnapshot> GetSnapshotAsync(ConnectionProfile profile, INamespaceClient client, bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && _snapshots.TryGetValue(profile.Id, out var cached) && DateTimeOffset.UtcNow - cached.LoadedAt < SnapshotMaxAge)
        {
            return cached.Snapshot;
        }

        var snapshot = await client.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        _snapshots[profile.Id] = (snapshot, DateTimeOffset.UtcNow);
        return snapshot;
    }

    public void Forget(string connectionId)
    {
        _snapshots.TryRemove(connectionId, out _);
        if (_clients.TryRemove(connectionId, out var client))
        {
            _ = client.DisposeAsync().AsTask();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _clients.Clear();
    }
}
