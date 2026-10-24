namespace QueueJanne.Core.Entities;

public enum EntityKind
{
    Queue,
    Topic,
    Subscription,
}

public sealed record EntityCounts(long Active, long DeadLetter, long TransferDeadLetter, long Scheduled, long Transfer, long SizeInBytes)
{
    public static readonly EntityCounts Empty = new(0, 0, 0, 0, 0, 0);
}

/// <summary>The settings that decide how operations behave. Always read live, never assumed.</summary>
public sealed record EntitySettings
{
    public int? MaxDeliveryCount { get; init; }

    public TimeSpan? LockDuration { get; init; }

    public bool RequiresSession { get; init; }

    public bool RequiresDuplicateDetection { get; init; }

    public TimeSpan? DuplicateDetectionWindow { get; init; }

    public bool EnablePartitioning { get; init; }

    public bool DeadLetteringOnMessageExpiration { get; init; }

    public TimeSpan? DefaultMessageTimeToLive { get; init; }

    public string? ForwardTo { get; init; }

    public string? ForwardDeadLetteredMessagesTo { get; init; }

    public string Status { get; init; } = "Active";

    public int? SubscriptionCount { get; init; }
}

public sealed record EntityInfo(EntityKind Kind, string Name, string? TopicName, EntityCounts Counts, EntitySettings Settings)
{
    /// <summary><c>queue</c>, <c>topic</c> or <c>topic/subscriptions/subscription</c>.</summary>
    public string Path => Kind == EntityKind.Subscription ? $"{TopicName}/subscriptions/{Name}" : Name;
}

public sealed record TopicInfo(EntityInfo Topic, IReadOnlyList<EntityInfo> Subscriptions);

public sealed record NamespaceSnapshot(IReadOnlyList<EntityInfo> Queues, IReadOnlyList<TopicInfo> Topics, DateTimeOffset LoadedAt)
{
    /// <summary>Queues and subscriptions: the entities messages can be read from.</summary>
    public IEnumerable<EntityInfo> ReadableEntities => Queues.Concat(Topics.SelectMany(t => t.Subscriptions));

    public long TotalDeadLetters => ReadableEntities.Sum(e => e.Counts.DeadLetter + e.Counts.TransferDeadLetter);
}
