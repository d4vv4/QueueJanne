using System.Globalization;
using QueueJanne.Core.Bodies;
using QueueJanne.Core.Messages;

namespace QueueJanne.App.ViewModels;

/// <summary>A row in the message grid. Display strings are computed once, when the row is created.</summary>
public sealed class MessageRowViewModel(QueueMessage message)
{
    private string? _searchText;

    public QueueMessage Message { get; } = message;

    /// <summary>The body as shown: decompressed when it was sent compressed.</summary>
    public DecodedBody Body { get; } = BodyDecoder.Decode(message.Body);

    public long SequenceNumber => Message.SequenceNumber;

    public string? MessageId => Message.MessageId;

    public string? Subject => Message.Subject;

    public DateTimeOffset EnqueuedTime => Message.EnqueuedTime;

    public string EnqueuedText { get; } = Formatting.Time(message.EnqueuedTime);

    public int Size => Message.Body.Length;

    public string SizeText { get; } = Formatting.Bytes(message.Body.Length);

    public int DeliveryCount => Message.DeliveryCount;

    public string? ContentType => Message.ContentType;

    public string? DeadLetterReason => Message.DeadLetterReason;

    public string? DeadLetterDescription => Message.DeadLetterErrorDescription;

    public string? SessionId => Message.SessionId;

    public string State => Message.State.ToString();

    public string? CorrelationId => Message.CorrelationId;

    public string? To => Message.To;

    public string? ReplyTo => Message.ReplyTo;

    public DateTimeOffset? ExpiresAt => Message.ExpiresAt;

    public string ExpiresText => Formatting.Time(Message.ExpiresAt);

    public string? ApplicationProperty(string name) =>
        Message.ApplicationProperties.TryGetValue(name, out var value) ? PropertyRow.FormatValue(value) : null;

    // Computed with the row, off the UI thread. Decoding again is only a header check for bodies that aren't compressed.
    public string Preview { get; } = BodyFormatter.Preview(BodyDecoder.Decode(message.Body).Bytes, 200);

    /// <summary>The value the grid groups by, set by the tab when grouping changes.</summary>
    public string GroupValue { get; set; } = string.Empty;

    // Screen readers and UI automation name the grid row after its item.
    public override string ToString() => $"#{SequenceNumber} {Subject}";

    public bool Matches(string filter)
    {
        _searchText ??= string.Join(
            '\n',
            Message.SequenceNumber.ToString(CultureInfo.InvariantCulture),
            Message.MessageId,
            Message.Subject,
            Message.CorrelationId,
            Message.SessionId,
            Message.ContentType,
            Message.DeadLetterReason,
            Message.DeadLetterErrorDescription,
            string.Join('\n', Message.ApplicationProperties.Select(p => $"{p.Key}={p.Value}")),
            BodyFormatter.LooksLikeText(Body.Bytes) ? BodyFormatter.DecodeText(Body.Bytes.AsSpan(0, Math.Min(Body.Bytes.Length, 64 * 1024))) : string.Empty);
        return _searchText.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record PropertyRow(string Group, string Name, string Value)
{
    public const string MessageGroup = "Message";
    public const string ApplicationGroup = "Application properties";

    public static IEnumerable<PropertyRow> For(QueueMessage message)
    {
        (string Name, string? Value)[] system =
        [
            ("Sequence number", message.SequenceNumber.ToString(CultureInfo.InvariantCulture)),
            ("Message ID", message.MessageId),
            ("Subject", message.Subject),
            ("Content type", message.ContentType),
            ("Correlation ID", message.CorrelationId),
            ("Session ID", message.SessionId),
            ("Reply-to session ID", message.ReplyToSessionId),
            ("Partition key", message.PartitionKey),
            ("To", message.To),
            ("Reply to", message.ReplyTo),
            ("Enqueued", Formatting.Time(message.EnqueuedTime)),
            ("Scheduled for", message.ScheduledEnqueueTime is null ? null : Formatting.Time(message.ScheduledEnqueueTime)),
            ("Expires", Formatting.Time(message.ExpiresAt)),
            ("Time to live", Formatting.Duration(message.TimeToLive)),
            ("Delivery count", message.DeliveryCount.ToString(CultureInfo.InvariantCulture)),
            ("State", message.State.ToString()),
            ("Dead-letter reason", message.DeadLetterReason),
            ("Dead-letter description", message.DeadLetterErrorDescription),
            ("Dead-letter source", message.DeadLetterSource),
            ("Body", $"{Formatting.Bytes(message.Body.Length)} ({message.BodyKind} body)"),
        ];

        foreach (var (name, value) in system)
        {
            if (!string.IsNullOrEmpty(value))
            {
                yield return new PropertyRow(MessageGroup, name, value);
            }
        }

        foreach (var (key, value) in message.ApplicationProperties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (key is not ("DeadLetterReason" or "DeadLetterErrorDescription"))
            {
                yield return new PropertyRow(ApplicationGroup, key, FormatValue(value));
            }
        }
    }

    internal static string FormatValue(object? value) => value switch
    {
        null => "(null)",
        DateTimeOffset time => Formatting.Time(time),
        DateTime time => time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture),
        _ => value.ToString() ?? string.Empty,
    };
}

public enum GroupKind
{
    None,
    DeadLetterReason,
    ErrorDescription,
    Subject,
    ContentType,
    ApplicationProperty,
}

public sealed record GroupOption(GroupKind Kind, string Label)
{
    public static IReadOnlyList<GroupOption> All { get; } =
    [
        new(GroupKind.None, "No grouping"),
        new(GroupKind.DeadLetterReason, "Dead-letter reason"),
        new(GroupKind.ErrorDescription, "Error description"),
        new(GroupKind.Subject, "Subject"),
        new(GroupKind.ContentType, "Content type"),
        new(GroupKind.ApplicationProperty, "Application property…"),
    ];

    public override string ToString() => Label;
}

public sealed record TopOption(int Value)
{
    public override string ToString() => Value == int.MaxValue ? "All messages" : $"Top {Value:N0}";
}
