using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using QueueJanne.Core.Bodies;

namespace QueueJanne.Core.Messages;

/// <summary>The JSON shape QueueJanne writes when exporting messages, and will read when importing them.</summary>
public sealed record ExportedMessage
{
    public long SequenceNumber { get; init; }

    public string? MessageId { get; init; }

    public string? Subject { get; init; }

    public string? ContentType { get; init; }

    public string? CorrelationId { get; init; }

    public string? SessionId { get; init; }

    public string? PartitionKey { get; init; }

    public string? To { get; init; }

    public string? ReplyTo { get; init; }

    public string? ReplyToSessionId { get; init; }

    public DateTimeOffset EnqueuedTime { get; init; }

    public TimeSpan? TimeToLive { get; init; }

    public int DeliveryCount { get; init; }

    public string? DeadLetterReason { get; init; }

    public string? DeadLetterErrorDescription { get; init; }

    public string? DeadLetterSource { get; init; }

    public MessageState State { get; init; }

    public IReadOnlyDictionary<string, object?> ApplicationProperties { get; init; } = new Dictionary<string, object?>();

    public string Body { get; init; } = string.Empty;

    /// <summary><c>utf8</c> for text bodies, <c>base64</c> for binary ones.</summary>
    public string BodyEncoding { get; init; } = "utf8";
}

public static class MessageExport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ExportedMessage ToExported(QueueMessage message)
    {
        var isText = BodyFormatter.LooksLikeText(message.Body);
        return new ExportedMessage
        {
            SequenceNumber = message.SequenceNumber,
            MessageId = message.MessageId,
            Subject = message.Subject,
            ContentType = message.ContentType,
            CorrelationId = message.CorrelationId,
            SessionId = message.SessionId,
            PartitionKey = message.PartitionKey,
            To = message.To,
            ReplyTo = message.ReplyTo,
            ReplyToSessionId = message.ReplyToSessionId,
            EnqueuedTime = message.EnqueuedTime,
            TimeToLive = message.TimeToLive == TimeSpan.MaxValue ? null : message.TimeToLive,
            DeliveryCount = message.DeliveryCount,
            DeadLetterReason = message.DeadLetterReason,
            DeadLetterErrorDescription = message.DeadLetterErrorDescription,
            DeadLetterSource = message.DeadLetterSource,
            State = message.State,
            ApplicationProperties = message.ApplicationProperties,
            Body = isText ? BodyFormatter.DecodeText(message.Body) : Convert.ToBase64String(message.Body),
            BodyEncoding = isText ? "utf8" : "base64",
        };
    }

    public static string ToJson(IEnumerable<QueueMessage> messages) =>
        JsonSerializer.Serialize(messages.Select(ToExported).ToList(), Options);
}
