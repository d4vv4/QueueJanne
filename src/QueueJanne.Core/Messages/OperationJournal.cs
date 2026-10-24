using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using QueueJanne.Core.Connections;
using QueueJanne.Core.ServiceBus;

namespace QueueJanne.Core.Messages;

/// <summary>One resubmit, delete, move or purge, with a copy of the messages it was about to change.</summary>
public sealed record JournalEntry
{
    public DateTimeOffset Time { get; init; }

    public string User { get; init; } = string.Empty;

    public string Machine { get; init; } = string.Empty;

    /// <summary><c>app</c> or <c>mcp</c>.</summary>
    public string Source { get; init; } = string.Empty;

    public string Operation { get; init; } = string.Empty;

    public string Connection { get; init; } = string.Empty;

    public string Namespace { get; init; } = string.Empty;

    public string Entity { get; init; } = string.Empty;

    public SubQueueKind SubQueue { get; init; }

    /// <summary>Where copies were sent, for resubmit and move.</summary>
    public string? Target { get; init; }

    /// <summary>How many messages were asked for, or null for a whole sub-queue.</summary>
    public int? Requested { get; init; }

    /// <summary>False when the sub-queue had more messages than were backed up.</summary>
    public bool BackupComplete { get; init; }

    /// <summary>Null while the operation runs; if a journal entry stays without an outcome, the operation didn't finish.</summary>
    public string? Outcome { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public IReadOnlyList<ExportedMessage> Messages { get; init; } = [];
}

/// <summary>
/// A local log of operations that change messages, kept in <c>~/.queuejanne/journal</c> with one JSON file per operation.
/// The messages are saved before the operation starts, so they can be recovered from the file if something goes wrong.
/// Entries older than <see cref="KeepDays"/> days are removed.
/// </summary>
public sealed class OperationJournal(string folder)
{
    public const int MaxBackup = 5_000;
    public const int KeepDays = 90;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static OperationJournal Default => new(Path.Combine(ConnectionStore.DefaultFolder(), "journal"));

    public string Folder { get; } = folder;

    /// <summary>Writes the entry before the operation runs. Returns the file, to pass to <see cref="Complete"/>.</summary>
    public string Begin(JournalEntry entry)
    {
        var time = entry.Time == default ? DateTimeOffset.Now : entry.Time;
        entry = entry with
        {
            Time = time,
            User = string.IsNullOrEmpty(entry.User) ? System.Environment.UserName : entry.User,
            Machine = string.IsNullOrEmpty(entry.Machine) ? System.Environment.MachineName : entry.Machine,
        };
        var day = Path.Combine(Folder, time.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(day);
        var name = $"{time:HHmmss-fff}-{entry.Operation}-{Safe(entry.Entity)}-{entry.SubQueue}.json".ToLowerInvariant();
        var path = Path.Combine(day, name);
        File.WriteAllText(path, JsonSerializer.Serialize(entry, Options));
        RemoveOld();
        return path;
    }

    /// <summary>Adds the outcome to an entry written by <see cref="Begin"/>.</summary>
    public void Complete(string path, string outcome, IReadOnlyList<string> errors) =>
        File.WriteAllText(path, JsonSerializer.Serialize(Load(path) with { Outcome = outcome, Errors = errors }, Options));

    public static JournalEntry Load(string path) => JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(path), Options)!;

    /// <summary>
    /// Backs up the messages, writes the entry, runs the operation and records how it went, also when it throws.
    /// <paramref name="known"/> are the messages when the caller already peeked them; otherwise they're collected here.
    /// </summary>
    public async Task<T> RecordAsync<T>(
        JournalEntry entry,
        INamespaceClient client,
        EntityAddress address,
        IReadOnlyList<QueueMessage>? known,
        IReadOnlyCollection<long>? sequenceNumbers,
        Func<Task<T>> run,
        Func<T, (string Outcome, IReadOnlyList<string> Errors)> describe,
        CancellationToken cancellationToken = default)
    {
        var (messages, complete) = known is not null && (sequenceNumbers is null || known.Count >= sequenceNumbers.Count)
            ? (known, true)
            : await CollectAsync(client, address, sequenceNumbers, cancellationToken).ConfigureAwait(false);
        var path = Begin(entry with
        {
            Entity = address.EntityPath,
            SubQueue = address.SubQueue,
            Requested = sequenceNumbers?.Count,
            BackupComplete = complete,
            Messages = messages.Select(MessageExport.ToExported).ToList(),
        });

        T result;
        try
        {
            result = await run().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Complete(path, "Failed: " + ex.Message, []);
            throw;
        }

        var (outcome, errors) = describe(result);
        Complete(path, outcome, errors);
        return result;
    }

    /// <summary>
    /// The messages to back up: the given ones when the caller already has them, else up to <see cref="MaxBackup"/>
    /// peeked from the sub-queue (or only those with the given sequence numbers). Peeking never locks.
    /// </summary>
    public static async Task<(IReadOnlyList<QueueMessage> Messages, bool Complete)> CollectAsync(
        INamespaceClient client,
        EntityAddress address,
        IReadOnlyCollection<long>? sequenceNumbers,
        CancellationToken cancellationToken = default)
    {
        var wanted = sequenceNumbers?.ToHashSet();
        var found = new List<QueueMessage>();
        var options = new PeekOptions
        {
            MaxMessages = MaxBackup,
            FromSequenceNumber = wanted is { Count: > 0 } ? wanted.Min() : null,
        };
        var scanned = 0;
        await foreach (var page in client.PeekAsync(address, options, cancellationToken).ConfigureAwait(false))
        {
            scanned += page.Count;
            found.AddRange(wanted is null ? page : page.Where(m => wanted.Contains(m.SequenceNumber)));
            if (wanted is not null && found.Count == wanted.Count)
            {
                break;
            }
        }

        var complete = wanted is null ? scanned < MaxBackup : found.Count == wanted.Count;
        return (found, complete);
    }

    private static string Safe(string path) =>
        string.Concat(path.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    private void RemoveOld()
    {
        var cutoff = DateTime.Today.AddDays(-KeepDays);
        foreach (var day in Directory.EnumerateDirectories(Folder))
        {
            if (DateTime.TryParseExact(Path.GetFileName(day), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
                && date < cutoff)
            {
                try
                {
                    Directory.Delete(day, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
