using QueueJanne.Core.Entities;

namespace QueueJanne.Core.Messages;

public enum SubQueueKind
{
    Active,
    DeadLetter,
    TransferDeadLetter,
}

/// <summary>A queue or subscription, plus which of its sub-queues to read.</summary>
public sealed record EntityAddress(EntityKind Kind, string Name, string? TopicName = null, SubQueueKind SubQueue = SubQueueKind.Active)
{
    public string EntityPath => Kind == EntityKind.Subscription ? $"{TopicName}/subscriptions/{Name}" : Name;

    public string DisplayName => SubQueue switch
    {
        SubQueueKind.DeadLetter => $"{EntityPath} (dead-letter)",
        SubQueueKind.TransferDeadLetter => $"{EntityPath} (transfer dead-letter)",
        _ => EntityPath,
    };

    public static EntityAddress Queue(string name, SubQueueKind subQueue = SubQueueKind.Active) =>
        new(EntityKind.Queue, name, null, subQueue);

    public static EntityAddress Subscription(string topicName, string subscriptionName, SubQueueKind subQueue = SubQueueKind.Active) =>
        new(EntityKind.Subscription, subscriptionName, topicName, subQueue);

    public static EntityAddress For(EntityInfo entity, SubQueueKind subQueue = SubQueueKind.Active) => entity.Kind switch
    {
        EntityKind.Queue => Queue(entity.Name, subQueue),
        EntityKind.Subscription => Subscription(entity.TopicName!, entity.Name, subQueue),
        _ => throw new ArgumentException($"'{entity.Name}' is a topic. Messages are read from its subscriptions.", nameof(entity)),
    };

    /// <summary>
    /// Parses <c>queue</c>, <c>topic/subscription</c> or <c>topic/subscriptions/subscription</c>,
    /// optionally ending in <c>/$deadletterqueue</c> or <c>/$transferdeadletterqueue</c>.
    /// </summary>
    public static EntityAddress Parse(string path, SubQueueKind subQueue = SubQueueKind.Active)
    {
        var parts = path.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count > 1 && parts[^1].Equals("$deadletterqueue", StringComparison.OrdinalIgnoreCase))
        {
            subQueue = SubQueueKind.DeadLetter;
            parts.RemoveAt(parts.Count - 1);
        }
        else if (parts.Count > 1 && parts[^1].Equals("$transferdeadletterqueue", StringComparison.OrdinalIgnoreCase))
        {
            subQueue = SubQueueKind.TransferDeadLetter;
            parts.RemoveAt(parts.Count - 1);
        }

        return parts.Count switch
        {
            1 => Queue(parts[0], subQueue),
            2 => Subscription(parts[0], parts[1], subQueue),
            3 when parts[1].Equals("subscriptions", StringComparison.OrdinalIgnoreCase) => Subscription(parts[0], parts[2], subQueue),
            _ => throw new FormatException($"'{path}' isn't a queue or subscription. Use 'queue', 'topic/subscription' or 'topic/subscriptions/subscription'."),
        };
    }
}
