using QueueJanne.Core.Entities;
using QueueJanne.Core.Messages;

namespace QueueJanne.Core.ServiceBus;

/// <summary>One namespace. Callers check the connection's read-only flag before calling operations that change messages.</summary>
public interface INamespaceClient : IAsyncDisposable
{
    /// <summary>Host name, for example <c>my-namespace.servicebus.windows.net</c>.</summary>
    string Namespace { get; }

    Task<NamespaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Peeks messages page by page. Peeking never locks messages or changes delivery counts.</summary>
    IAsyncEnumerable<IReadOnlyList<QueueMessage>> PeekAsync(EntityAddress address, PeekOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends copies of dead-lettered messages back to their queue (or topic) and then removes them from the dead-letter queue.
    /// A message is only removed after its copy was sent, so a failure can leave a duplicate but never loses a message.
    /// </summary>
    Task<ResubmitResult> ResubmitAsync(EntityAddress source, ResubmitRequest request, IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Removes specific messages. On an active queue this receives (and so locks) the messages in front of them.</summary>
    Task<MessageOperationResult> DeleteAsync(EntityAddress address, DeleteRequest request, IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Removes every message enqueued before the purge started, without receiving them where the service allows it.</summary>
    Task<MessageOperationResult> PurgeAsync(EntityAddress address, IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Sends messages to a queue or topic.</summary>
    Task SendAsync(string entityName, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>The rules (filters and actions) of a subscription.</summary>
    Task<IReadOnlyList<RuleInfo>> GetRulesAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default);
}

public sealed record PeekOptions
{
    public int MaxMessages { get; init; } = 1000;

    public int PageSize { get; init; } = 250;

    public long? FromSequenceNumber { get; init; }
}
