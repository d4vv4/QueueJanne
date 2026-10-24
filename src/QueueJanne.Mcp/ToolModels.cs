using QueueJanne.Core.Bodies;
using QueueJanne.Core.Connections;
using QueueJanne.Core.Entities;
using QueueJanne.Core.Messages;

namespace QueueJanne.Mcp;

public sealed record ConnectionSummary(string Id, string Name, string? Namespace, string Auth, string? Environment, bool ReadOnly)
{
    public static ConnectionSummary From(ConnectionProfile profile) =>
        new(profile.Id, profile.Name, profile.Namespace, profile.Auth.ToString(), profile.Environment, profile.ReadOnly);
}

public sealed record EntitySummary(
    string Path,
    string Kind,
    long Active,
    long DeadLetter,
    long TransferDeadLetter,
    long Scheduled,
    int? MaxDeliveryCount,
    string? LockDuration,
    bool RequiresSession,
    string? ForwardTo,
    string? ForwardDeadLetteredMessagesTo,
    string Status)
{
    public static EntitySummary From(EntityInfo entity) => new(
        entity.Path,
        entity.Kind.ToString(),
        entity.Counts.Active,
        entity.Counts.DeadLetter,
        entity.Counts.TransferDeadLetter,
        entity.Counts.Scheduled,
        entity.Settings.MaxDeliveryCount,
        entity.Settings.LockDuration?.ToString(),
        entity.Settings.RequiresSession,
        entity.Settings.ForwardTo,
        entity.Settings.ForwardDeadLetteredMessagesTo,
        entity.Settings.Status);
}

public sealed record TopicSummary(string Name, int Subscriptions, long Scheduled, bool RequiresDuplicateDetection, bool EnablePartitioning);

public sealed record EntityListing(
    string Connection,
    string Namespace,
    DateTimeOffset LoadedAt,
    long TotalDeadLetters,
    IReadOnlyList<EntitySummary> Queues,
    IReadOnlyList<TopicSummary> Topics,
    IReadOnlyList<EntitySummary> Subscriptions);

public sealed record MessageSummary(
    long SequenceNumber,
    string? MessageId,
    string? Subject,
    string? ContentType,
    DateTimeOffset EnqueuedTime,
    int DeliveryCount,
    string State,
    string? DeadLetterReason,
    string? DeadLetterErrorDescription,
    string? DeadLetterSource,
    string? SessionId,
    string? CorrelationId,
    string? To,
    string? ReplyTo,
    IReadOnlyDictionary<string, object?> ApplicationProperties,
    int BodySize,
    string BodyFormat,
    string? Body,
    bool BodyTruncated)
{
    public static MessageSummary From(QueueMessage message, int bodyMaxChars)
    {
        var format = BodyFormatter.Detect(message.Body, message.ContentType);
        string? body = null;
        var truncated = false;
        if (bodyMaxChars > 0)
        {
            // Bodies go out as sent (compact), not pretty-printed, to keep tool results small.
            body = format == Core.Bodies.BodyFormat.Hex ? Convert.ToBase64String(message.Body) : BodyFormatter.DecodeText(message.Body);
            if (body.Length > bodyMaxChars)
            {
                body = body[..bodyMaxChars];
                truncated = true;
            }
        }

        return new MessageSummary(
            message.SequenceNumber,
            message.MessageId,
            message.Subject,
            message.ContentType,
            message.EnqueuedTime,
            message.DeliveryCount,
            message.State.ToString(),
            message.DeadLetterReason,
            message.DeadLetterErrorDescription,
            message.DeadLetterSource,
            message.SessionId,
            message.CorrelationId,
            message.To,
            message.ReplyTo,
            message.ApplicationProperties
                .Where(p => p.Key is not ("DeadLetterReason" or "DeadLetterErrorDescription"))
                .ToDictionary(p => p.Key, p => p.Value),
            message.Body.Length,
            format == Core.Bodies.BodyFormat.Hex ? "binary (base64)" : format.ToString().ToLowerInvariant(),
            body,
            truncated);
    }
}

public sealed record PeekResult(
    string Connection,
    string Entity,
    string SubQueue,
    int Count,
    int Scanned,
    long? NextFromSequenceNumber,
    IReadOnlyList<MessageSummary> Messages,
    string? Note);

public sealed record SubscriptionRules(string Subscription, IReadOnlyList<RuleInfo> Rules);

public sealed record EntityDetails(
    string Connection,
    string Path,
    string Kind,
    EntityCounts Counts,
    EntitySettings Settings,
    IReadOnlyList<RuleInfo>? Rules,
    IReadOnlyList<SubscriptionRules>? Subscriptions);

public sealed record SendReport(string Connection, string Entity, int Sent, DateTimeOffset? ScheduledFor);

public sealed record TransferReport(
    string Connection,
    string Source,
    string TargetConnection,
    string Target,
    int Sent,
    int RemovedFromSource,
    int NotFound,
    IReadOnlyList<string> Errors);

public sealed record ResubmitReport(
    string Connection,
    string Source,
    string Target,
    int Resubmitted,
    int NotFound,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record OperationReport(
    string Connection,
    string Entity,
    int Count,
    int NotFound,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record DeadLetterReport(
    string Connection,
    string Entity,
    long DeadLetterCount,
    int MessagesExamined,
    IReadOnlyList<DeadLetterReasonGroup> Reasons);
