namespace QueueJanne.Core.Messages;

public sealed record CountedValue(string Value, int Count);

public sealed record DeadLetterReasonGroup(
    string Reason,
    int Count,
    DateTimeOffset OldestEnqueued,
    DateTimeOffset NewestEnqueued,
    IReadOnlyList<CountedValue> TopDescriptions,
    IReadOnlyList<CountedValue> TopSubjects);

/// <summary>Groups dead-lettered messages so a large dead-letter queue can be triaged by cause.</summary>
public static class DeadLetterSummary
{
    public static IReadOnlyList<DeadLetterReasonGroup> ByReason(IEnumerable<QueueMessage> messages, int topValues = 5) =>
        messages
            .GroupBy(m => string.IsNullOrWhiteSpace(m.DeadLetterReason) ? "(no reason)" : m.DeadLetterReason)
            .Select(g => new DeadLetterReasonGroup(
                g.Key,
                g.Count(),
                g.Min(m => m.EnqueuedTime),
                g.Max(m => m.EnqueuedTime),
                Top(g.Select(m => m.DeadLetterErrorDescription), topValues),
                Top(g.Select(m => m.Subject), topValues)))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Reason, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<CountedValue> Top(IEnumerable<string?> values, int take) =>
        values
            .Select(v => string.IsNullOrWhiteSpace(v) ? "(none)" : Truncate(v.Trim(), 300))
            .GroupBy(v => v, StringComparer.Ordinal)
            .Select(g => new CountedValue(g.Key, g.Count()))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Value, StringComparer.Ordinal)
            .Take(take)
            .ToList();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
