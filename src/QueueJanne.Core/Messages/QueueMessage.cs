namespace QueueJanne.Core.Messages;

public enum MessageState
{
    Active,
    Deferred,
    Scheduled,
}

/// <summary>How the AMQP body was encoded. Most messages use Data; other AMQP clients can send Value or Sequence bodies.</summary>
public enum BodyKind
{
    Data,
    Value,
    Sequence,
}

/// <summary>A snapshot of a peeked message, independent of the Service Bus SDK.</summary>
public sealed class QueueMessage
{
    public long SequenceNumber { get; init; }

    public string? MessageId { get; init; }

    public string? Subject { get; init; }

    public string? ContentType { get; init; }

    public string? CorrelationId { get; init; }

    public string? SessionId { get; init; }

    public string? ReplyToSessionId { get; init; }

    public string? PartitionKey { get; init; }

    public string? To { get; init; }

    public string? ReplyTo { get; init; }

    public DateTimeOffset EnqueuedTime { get; init; }

    public DateTimeOffset? ScheduledEnqueueTime { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public TimeSpan? TimeToLive { get; init; }

    public int DeliveryCount { get; init; }

    public string? DeadLetterReason { get; init; }

    public string? DeadLetterErrorDescription { get; init; }

    public string? DeadLetterSource { get; init; }

    public MessageState State { get; init; }

    public IReadOnlyDictionary<string, object?> ApplicationProperties { get; init; } = new Dictionary<string, object?>();

    public byte[] Body { get; init; } = [];

    public BodyKind BodyKind { get; init; }
}
