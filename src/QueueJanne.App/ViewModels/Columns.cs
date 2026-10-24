namespace QueueJanne.App.ViewModels;

/// <summary>A column in the message grid, as saved: its key, width and whether it's shown. List order is display order.</summary>
public sealed record ColumnSetting(string Key, double Width, bool Visible);

/// <param name="Path">Property of <see cref="MessageRowViewModel"/> to show; null for application property columns.</param>
public sealed record MessageColumn(string Key, string Header, string? Path, string? SortPath, double DefaultWidth)
{
    public bool IsApplicationProperty => Key.StartsWith(ColumnCatalog.PropertyPrefix, StringComparison.Ordinal);

    public string PropertyName => Key[ColumnCatalog.PropertyPrefix.Length..];
}

public static class ColumnCatalog
{
    public const string PropertyPrefix = "prop:";

    public static IReadOnlyList<MessageColumn> BuiltIn { get; } =
    [
        new("SequenceNumber", "Seq #", "SequenceNumber", null, 70),
        new("Enqueued", "Enqueued", "EnqueuedText", "EnqueuedTime", 150),
        new("Subject", "Subject", "Subject", null, 140),
        new("Size", "Size", "SizeText", "Size", 72),
        new("DeliveryCount", "Deliveries", "DeliveryCount", null, 100),
        new("DeadLetterReason", "Dead-letter reason", "DeadLetterReason", null, 170),
        new("DeadLetterDescription", "Error description", "DeadLetterDescription", null, 230),
        new("MessageId", "Message ID", "MessageId", null, 120),
        new("ContentType", "Content type", "ContentType", null, 110),
        new("SessionId", "Session", "SessionId", null, 80),
        new("CorrelationId", "Correlation ID", "CorrelationId", null, 130),
        new("State", "State", "State", null, 80),
        new("To", "To", "To", null, 110),
        new("ReplyTo", "Reply to", "ReplyTo", null, 110),
        new("Expires", "Expires", "ExpiresText", "ExpiresAt", 150),
        new("Body", "Body", "Preview", null, 520),
    ];

    private static readonly HashSet<string> HiddenByDefault = ["CorrelationId", "State", "To", "ReplyTo", "Expires"];

    public static IReadOnlyList<ColumnSetting> Defaults { get; } =
        BuiltIn.Select(c => new ColumnSetting(c.Key, c.DefaultWidth, !HiddenByDefault.Contains(c.Key))).ToList();

    public static MessageColumn Find(string key) =>
        BuiltIn.FirstOrDefault(c => c.Key == key)
        ?? (key.StartsWith(PropertyPrefix, StringComparison.Ordinal)
            ? new MessageColumn(key, key[PropertyPrefix.Length..], null, null, 120)
            : throw new ArgumentException($"Unknown column '{key}'.", nameof(key)));

    public static string PropertyKey(string propertyName) => PropertyPrefix + propertyName;

    /// <summary>Drops unknown built-in keys from saved settings and adds built-ins that are new since they were saved.</summary>
    public static IReadOnlyList<ColumnSetting> Normalize(IEnumerable<ColumnSetting>? saved)
    {
        if (saved is null)
        {
            return Defaults;
        }

        var result = saved
            .Where(c => c.Key.StartsWith(PropertyPrefix, StringComparison.Ordinal) || BuiltIn.Any(b => b.Key == c.Key))
            .DistinctBy(c => c.Key)
            .Select(c => c with { Width = Math.Clamp(c.Width, 30, 2000) })
            .ToList();
        result.AddRange(BuiltIn.Where(b => result.All(c => c.Key != b.Key)).Select(b => new ColumnSetting(b.Key, b.DefaultWidth, false)));
        return result;
    }
}
