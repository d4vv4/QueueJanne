using QueueJanne.Core.Entities;

namespace QueueJanne.Core.Messages;

public sealed record ResubmitRequest
{
    /// <summary>The messages to resubmit. <c>null</c> means every message, up to <see cref="MaxMessages"/>.</summary>
    public IReadOnlyCollection<long>? SequenceNumbers { get; init; }

    public int MaxMessages { get; init; } = int.MaxValue;

    /// <summary>Give each copy a new message ID and keep the old one in the <c>OriginalMessageId</c> property.</summary>
    public bool NewMessageId { get; init; }
}

/// <param name="NotFound">Requested messages that were no longer in the dead-letter queue.</param>
public sealed record ResubmitResult(string Target, int Resubmitted, int NotFound, IReadOnlyList<string> Errors);

/// <summary>What a person or agent should know before resubmitting, worked out from the entity's live settings.</summary>
public static class ResubmitAdvice
{
    public const string OriginalMessageIdProperty = "OriginalMessageId";

    /// <summary>Messages go back to the queue, or to the topic for a subscription, since subscriptions can't be sent to.</summary>
    public static string TargetOf(EntityAddress source) => source.Kind == EntityKind.Subscription ? source.TopicName! : source.Name;

    public static bool TargetDetectsDuplicates(NamespaceSnapshot snapshot, EntityAddress source) =>
        TargetSettings(snapshot, source)?.RequiresDuplicateDetection == true;

    public static IReadOnlyList<string> Warnings(NamespaceSnapshot snapshot, EntityAddress source, bool newMessageId)
    {
        var warnings = new List<string>();
        var target = TargetOf(source);
        if (source.Kind == EntityKind.Subscription)
        {
            var others = snapshot.Topics
                .FirstOrDefault(t => t.Topic.Name.Equals(target, StringComparison.OrdinalIgnoreCase))?
                .Subscriptions.Where(s => !s.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Name)
                .ToList() ?? [];
            warnings.Add(others.Count == 0
                ? $"Subscriptions can't be sent to directly, so the messages go to topic '{target}'."
                : $"Subscriptions can't be sent to directly, so the messages go to topic '{target}'. "
                  + $"Every other subscription whose rules match gets a copy too: {string.Join(", ", others)}.");
        }

        if (TargetSettings(snapshot, source) is { RequiresDuplicateDetection: true } settings && !newMessageId)
        {
            warnings.Add($"'{target}' has duplicate detection (window {settings.DuplicateDetectionWindow}). "
                + "Copies with a message ID it has already seen in that window are dropped without an error. Use a new message ID to avoid that.");
        }

        return warnings;
    }

    private static EntitySettings? TargetSettings(NamespaceSnapshot snapshot, EntityAddress source)
    {
        var target = TargetOf(source);
        return source.Kind == EntityKind.Subscription
            ? snapshot.Topics.FirstOrDefault(t => t.Topic.Name.Equals(target, StringComparison.OrdinalIgnoreCase))?.Topic.Settings
            : snapshot.Queues.FirstOrDefault(q => q.Name.Equals(target, StringComparison.OrdinalIgnoreCase))?.Settings;
    }
}
