using QueueJanne.Core.Entities;

namespace QueueJanne.Core.Messages;

public sealed record DeleteRequest
{
    public required IReadOnlyCollection<long> SequenceNumbers { get; init; }

    /// <summary>Messages known to be scheduled. They can't be received, so they're cancelled instead (queues only).</summary>
    public IReadOnlyCollection<long> ScheduledSequenceNumbers { get; init; } = [];
}

/// <param name="NotFound">Requested messages that were no longer there.</param>
public sealed record MessageOperationResult(int Count, int NotFound, IReadOnlyList<string> Errors);

/// <summary>What to know before deleting or purging, worked out from the entity's live settings.</summary>
public static class DeleteAdvice
{
    public static IReadOnlyList<string> ForDelete(EntityInfo? entity, SubQueueKind subQueue)
    {
        if (subQueue != SubQueueKind.Active || entity is null)
        {
            return [];
        }

        var settings = entity.Settings;
        var warnings = new List<string>
        {
            "Finding these messages means receiving the messages in front of them. Consumers can't see a message while it's held "
                + $"(up to the lock duration, {settings.LockDuration?.ToString() ?? "unknown"}), and each one held gets its delivery count increased.",
        };
        if (settings.MaxDeliveryCount is { } max)
        {
            warnings.Add($"This entity dead-letters a message after {max} deliveries, so held messages that are already at {max - 1} move to the dead-letter queue.");
        }

        return warnings;
    }

    public static IReadOnlyList<string> ForPurge(EntityInfo? entity, IReadOnlyCollection<SubQueueKind> subQueues)
    {
        var warnings = new List<string>();
        if (subQueues.Contains(SubQueueKind.Active))
        {
            warnings.Add("Purging active messages removes work that consumers haven't processed yet. Messages a consumer has locked right now are skipped.");
        }

        if (subQueues.Any(s => s != SubQueueKind.Active))
        {
            warnings.Add("Dead letters that arrive while the purge runs are removed too. Export them first if you might need them.");
        }

        return warnings;
    }
}
