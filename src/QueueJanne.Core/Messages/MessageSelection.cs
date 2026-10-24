using System.ComponentModel;
using System.Globalization;
using QueueJanne.Core.Bodies;
using QueueJanne.Core.ServiceBus;

namespace QueueJanne.Core.Messages;

/// <summary>Which messages to act on. Every condition that is set must match; text comparisons ignore case.</summary>
public sealed record MessageFilter
{
    [Description("Exact dead-letter reason, for example MaxDeliveryCountExceeded.")]
    public string? DeadLetterReason { get; init; }

    /// <summary>Text the dead-letter error description must contain.</summary>
    [Description("Text the dead-letter error description contains.")]
    public string? ErrorContains { get; init; }

    [Description("Exact subject (label).")]
    public string? Subject { get; init; }

    [Description("Application property that must exist.")]
    public string? PropertyName { get; init; }

    /// <summary>With <see cref="PropertyName"/>: the property's value as text. Without a value, the property only has to exist.</summary>
    [Description("Value that application property must have, compared as text.")]
    public string? PropertyValue { get; init; }

    [Description("Text the body contains.")]
    public string? BodyContains { get; init; }

    [Description("Only messages enqueued at or after this time (ISO 8601).")]
    public DateTimeOffset? EnqueuedAfter { get; init; }

    [Description("Only messages enqueued at or before this time (ISO 8601).")]
    public DateTimeOffset? EnqueuedBefore { get; init; }

    /// <summary>
    /// When true, the dead-letter reason, subject and property value match when they contain the text, so <c>ie</c> finds
    /// <c>Invoice</c>. A value in double quotes still has to match exactly.
    /// </summary>
    [Description("When true, deadLetterReason, subject and propertyValue match when they contain the text instead of exactly; a value in double quotes still matches exactly.")]
    public bool Partial { get; init; }

    public bool IsEmpty => this with { Partial = false } == new MessageFilter();

    public bool Matches(QueueMessage message)
    {
        if (DeadLetterReason is { } reason && !TextMatches(message.DeadLetterReason, reason))
        {
            return false;
        }

        if (ErrorContains is { } error && message.DeadLetterErrorDescription?.Contains(error, StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        if (Subject is { } subject && !TextMatches(message.Subject, subject))
        {
            return false;
        }

        if (PropertyName is { } name)
        {
            var found = message.ApplicationProperties.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
            if (found.Key is null)
            {
                return false;
            }

            if (PropertyValue is { } expected
                && !TextMatches(Convert.ToString(found.Value, CultureInfo.InvariantCulture), expected))
            {
                return false;
            }
        }

        if (EnqueuedAfter is { } after && message.EnqueuedTime < after)
        {
            return false;
        }

        if (EnqueuedBefore is { } before && message.EnqueuedTime > before)
        {
            return false;
        }

        return BodyContains is not { } text
            || (BodyFormatter.LooksLikeText(message.Body) && BodyFormatter.DecodeText(message.Body).Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private bool TextMatches(string? actual, string expected)
    {
        if (actual is null)
        {
            return false;
        }

        if (Partial && expected is not ['"', .., '"'])
        {
            return actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
        }

        var exact = Partial ? expected[1..^1] : expected;
        return string.Equals(actual, exact, StringComparison.OrdinalIgnoreCase);
    }
}

/// <param name="Scanned">How many messages were peeked to find the matches.</param>
/// <param name="ReachedEnd">True when every message in the sub-queue was scanned.</param>
public sealed record SelectionResult(IReadOnlyList<QueueMessage> Messages, int Scanned, bool ReachedEnd);

public static class MessageSelection
{
    /// <summary>Peeks up to <paramref name="scanLimit"/> messages (never locking them) and keeps the ones that match.</summary>
    public static async Task<SelectionResult> FindAsync(
        INamespaceClient client,
        EntityAddress address,
        MessageFilter filter,
        int maxMatches,
        int scanLimit,
        long? fromSequenceNumber = null,
        CancellationToken cancellationToken = default)
    {
        var matches = new List<QueueMessage>();
        var scanned = 0;
        var options = new PeekOptions { MaxMessages = scanLimit, PageSize = 250, FromSequenceNumber = fromSequenceNumber };
        await foreach (var page in client.PeekAsync(address, options, cancellationToken).ConfigureAwait(false))
        {
            foreach (var message in page)
            {
                scanned++;
                if (filter.Matches(message))
                {
                    matches.Add(message);
                    if (matches.Count >= maxMatches)
                    {
                        return new SelectionResult(matches, scanned, ReachedEnd: false);
                    }
                }
            }
        }

        return new SelectionResult(matches, scanned, ReachedEnd: scanned < scanLimit);
    }
}

/// <summary>A message to send. Copies of existing messages keep their body bytes and properties.</summary>
public sealed record OutgoingMessage
{
    public byte[] Body { get; init; } = [];

    public string? ContentType { get; init; }

    public string? Subject { get; init; }

    public string? MessageId { get; init; }

    public string? CorrelationId { get; init; }

    public string? SessionId { get; init; }

    public string? PartitionKey { get; init; }

    public string? To { get; init; }

    public string? ReplyTo { get; init; }

    public TimeSpan? TimeToLive { get; init; }

    public DateTimeOffset? ScheduledEnqueueTime { get; init; }

    public IReadOnlyDictionary<string, object?> ApplicationProperties { get; init; } = new Dictionary<string, object?>();

    /// <summary>A copy of a peeked message, without its dead-letter details.</summary>
    public static OutgoingMessage CopyOf(QueueMessage message, bool newMessageId) => new()
    {
        Body = message.Body,
        ContentType = message.ContentType,
        Subject = message.Subject,
        MessageId = newMessageId ? Guid.NewGuid().ToString("N") : message.MessageId,
        CorrelationId = message.CorrelationId,
        SessionId = message.SessionId,
        PartitionKey = message.PartitionKey,
        To = message.To,
        ReplyTo = message.ReplyTo,
        TimeToLive = message.TimeToLive is { } ttl && ttl < TimeSpan.FromDays(3650) ? ttl : null,
        ApplicationProperties = message.ApplicationProperties
            .Where(p => p.Key is not ("DeadLetterReason" or "DeadLetterErrorDescription"))
            .Concat(newMessageId ? [new KeyValuePair<string, object?>(ResubmitAdvice.OriginalMessageIdProperty, message.MessageId)] : [])
            .ToDictionary(p => p.Key, p => p.Value),
    };
}

/// <summary>A subscription rule: its filter and optional action, as text.</summary>
public sealed record RuleInfo(string Name, string Filter, string? Action);

public sealed record TransferResult(int Sent, int Removed, int NotFound, IReadOnlyList<string> Errors);

public static class MessageTransfer
{
    /// <summary>
    /// Sends copies of the chosen messages to another queue or topic, in this namespace or another one. With
    /// <paramref name="move"/>, the originals are deleted after the copies were sent, so a failure can leave a duplicate but never loses a message.
    /// </summary>
    public static async Task<TransferResult> CopyAsync(
        INamespaceClient source,
        EntityAddress from,
        IReadOnlyCollection<long> sequenceNumbers,
        INamespaceClient target,
        string targetEntity,
        bool move,
        bool newMessageId,
        CancellationToken cancellationToken = default)
    {
        var wanted = sequenceNumbers.ToHashSet();
        var found = new List<QueueMessage>();
        var options = new PeekOptions { MaxMessages = int.MaxValue, FromSequenceNumber = wanted.Count > 0 ? wanted.Min() : null };
        await foreach (var page in source.PeekAsync(from, options, cancellationToken).ConfigureAwait(false))
        {
            found.AddRange(page.Where(m => wanted.Contains(m.SequenceNumber)));
            if (found.Count == wanted.Count)
            {
                break;
            }
        }

        if (found.Count == 0)
        {
            return new TransferResult(0, 0, wanted.Count, []);
        }

        try
        {
            await target.SendAsync(targetEntity, found.Select(m => OutgoingMessage.CopyOf(m, newMessageId)).ToList(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new TransferResult(0, 0, wanted.Count - found.Count, [$"Couldn't send to '{targetEntity}': {ConnectionErrors.Explain(ex)}"]);
        }

        if (!move)
        {
            return new TransferResult(found.Count, 0, wanted.Count - found.Count, []);
        }

        var deleted = await source.DeleteAsync(from, new DeleteRequest
        {
            SequenceNumbers = found.Select(m => m.SequenceNumber).ToList(),
            ScheduledSequenceNumbers = found.Where(m => m.State == MessageState.Scheduled).Select(m => m.SequenceNumber).ToList(),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new TransferResult(found.Count, deleted.Count, wanted.Count - found.Count, deleted.Errors);
    }
}
