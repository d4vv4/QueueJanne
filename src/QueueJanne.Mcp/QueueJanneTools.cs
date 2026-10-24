using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using QueueJanne.Core.Connections;
using QueueJanne.Core.Entities;
using QueueJanne.Core.Messages;
using QueueJanne.Core.ServiceBus;

namespace QueueJanne.Mcp;

/// <summary>
/// Service Bus tools. Reading tools peek, which never locks messages or changes delivery counts.
/// Tools that change messages refuse connections marked read-only, and nothing here can turn read-only off.
/// </summary>
[McpServerToolType]
public sealed class QueueJanneTools(ConnectionStore store, NamespaceClientCache clients)
{
    private const int MaxPeek = 500;
    private const int MaxScan = 20_000;
    private const int DefaultScan = 5_000;

    // Keeps a single peek result well inside a model's context.
    private const int BodyBudget = 60_000;

    [McpServerTool(Name = "list_connections", ReadOnly = true, Idempotent = true)]
    [Description("Lists the Service Bus namespaces saved in QueueJanne. The desktop app and this server share the same list.")]
    public IReadOnlyList<ConnectionSummary> ListConnections() =>
        store.Load().Select(ConnectionSummary.From).ToList();

    [McpServerTool(Name = "add_entra_connection", Idempotent = true)]
    [Description("Saves a namespace reached with the signed-in Azure identity (Entra ID, for example from 'az login'). "
        + "Connection strings can only be added in the desktop app, so secrets never pass through chat. Saving the same name again updates it, "
        + "but this tool can't turn read-only off: only the user can, in the desktop app.")]
    public ConnectionSummary AddEntraConnection(
        [Description("Display name, for example 'queuejanne-test'.")] string name,
        [Description("Namespace short name or host name, for example 'my-ns' or 'my-ns.servicebus.windows.net'.")] string @namespace,
        [Description("Environment tag such as dev, test or prod.")] string? environment = null,
        [Description("When true (the default), QueueJanne refuses operations that change messages on this connection.")] bool readOnly = true,
        [Description("Entra ID tenant of the namespace, only when it differs from the signed-in tenant.")] string? tenantId = null)
    {
        var existing = store.Load().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is { Auth: not ConnectionAuth.EntraId })
        {
            throw new McpException($"'{existing.Name}' is a {existing.Auth} connection. Pick another name; it can only be changed in the desktop app.");
        }

        if (existing is { ReadOnly: true } && !readOnly)
        {
            throw new McpException($"'{existing.Name}' is read-only. Only the user can turn that off, in the desktop app.");
        }

        var profile = store.Save(new ConnectionProfile
        {
            Id = existing?.Id ?? ConnectionProfile.NewId(),
            Name = name,
            Auth = ConnectionAuth.EntraId,
            Namespace = ConnectionProfile.NormalizeNamespace(@namespace),
            Environment = environment,
            ReadOnly = readOnly,
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim(),
        });
        clients.Forget(profile.Id);
        return ConnectionSummary.From(profile);
    }

    [McpServerTool(Name = "list_entities", ReadOnly = true, Idempotent = true)]
    [Description("Lists queues, topics and subscriptions with active, dead-letter, transfer dead-letter and scheduled counts, "
        + "plus the settings that decide how operations behave (max delivery count, lock duration, sessions, forwarding).")]
    public async Task<EntityListing> ListEntities(
        [Description("Connection name or id from list_connections.")] string connection,
        [Description("Only entities whose path contains this text (case-insensitive).")] string? nameFilter = null,
        [Description("Only entities that have dead-lettered messages.")] bool onlyWithDeadLetters = false,
        CancellationToken cancellationToken = default)
    {
        var (profile, client) = clients.Get(connection);
        var snapshot = await clients.GetSnapshotAsync(profile, client, refresh: true, cancellationToken).ConfigureAwait(false);

        bool Matches(string path) => nameFilter is null || path.Contains(nameFilter, StringComparison.OrdinalIgnoreCase);
        bool Include(EntityInfo entity) =>
            Matches(entity.Path) && (!onlyWithDeadLetters || entity.Counts.DeadLetter + entity.Counts.TransferDeadLetter > 0);

        var subscriptions = snapshot.Topics.SelectMany(t => t.Subscriptions).Where(Include).ToList();
        var topics = snapshot.Topics
            .Where(t => subscriptions.Any(s => s.TopicName == t.Topic.Name) || (!onlyWithDeadLetters && Matches(t.Topic.Name)))
            .Select(t => new TopicSummary(t.Topic.Name, t.Subscriptions.Count, t.Topic.Counts.Scheduled, t.Topic.Settings.RequiresDuplicateDetection, t.Topic.Settings.EnablePartitioning))
            .ToList();

        return new EntityListing(
            profile.Name,
            client.Namespace,
            snapshot.LoadedAt,
            snapshot.TotalDeadLetters,
            snapshot.Queues.Where(Include).Select(EntitySummary.From).ToList(),
            topics,
            subscriptions.Select(EntitySummary.From).ToList());
    }

    [McpServerTool(Name = "get_entity", ReadOnly = true, Idempotent = true)]
    [Description("Full settings and counts of one queue, topic or subscription. For a subscription it includes its rules (filters and actions); "
        + "for a topic, every subscription with its rules. Use it to see which subscriptions a message sent to a topic will reach.")]
    public async Task<EntityDetails> GetEntity(
        [Description("Connection name or id from list_connections.")] string connection,
        [Description("Queue or topic name, or 'topic/subscription'.")] string entity,
        CancellationToken cancellationToken = default)
    {
        var (profile, client) = clients.Get(connection);
        var snapshot = await clients.GetSnapshotAsync(profile, client, refresh: true, cancellationToken).ConfigureAwait(false);
        var topic = snapshot.Topics.FirstOrDefault(t => t.Topic.Name.Equals(entity.Trim(), StringComparison.OrdinalIgnoreCase));
        if (topic is not null && !snapshot.Queues.Any(q => q.Name.Equals(entity.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            var subscriptions = new List<SubscriptionRules>();
            foreach (var subscription in topic.Subscriptions)
            {
                subscriptions.Add(new SubscriptionRules(subscription.Name, await client.GetRulesAsync(topic.Topic.Name, subscription.Name, cancellationToken).ConfigureAwait(false)));
            }

            return new EntityDetails(profile.Name, topic.Topic.Path, "Topic", topic.Topic.Counts, topic.Topic.Settings, null, subscriptions);
        }

        var address = await ResolveAsync(profile, client, entity, SubQueueKind.Active, cancellationToken).ConfigureAwait(false);
        var info = snapshot.ReadableEntities.First(e => e.Path == address.EntityPath);
        var rules = info.Kind == EntityKind.Subscription
            ? await client.GetRulesAsync(info.TopicName!, info.Name, cancellationToken).ConfigureAwait(false)
            : null;
        return new EntityDetails(profile.Name, info.Path, info.Kind.ToString(), info.Counts, info.Settings, rules, null);
    }

    [McpServerTool(Name = "peek_messages", ReadOnly = true, Idempotent = true)]
    [Description("Reads messages without locking or removing them (peek), so consumers and delivery counts are unaffected. "
        + "Use subQueue 'deadletter' for the dead-letter queue. With a filter, up to scanLimit messages are scanned and only matches returned. "
        + "To page, pass nextFromSequenceNumber from the previous result as fromSequenceNumber. Large results get shorter bodies; see 'note'.")]
    public async Task<PeekResult> PeekMessages(
        [Description("Connection name or id from list_connections.")] string connection,
        [Description("Queue name, or 'topic/subscription'.")] string entity,
        [Description("'active' (default), 'deadletter' or 'transferdeadletter'.")] string subQueue = "active",
        [Description("How many messages to return, 1-500. Default 20.")] int maxMessages = 20,
        [Description("Start at this sequence number.")] long? fromSequenceNumber = null,
        [Description("Cut each body after this many characters. Default 4000; 0 leaves bodies out.")] int bodyMaxChars = 4000,
        [Description("Only return messages that match all of these conditions.")] MessageFilter? filter = null,
        [Description("With a filter: how many messages to scan, up to 20000. Default 5000.")] int scanLimit = DefaultScan,
        CancellationToken cancellationToken = default)
    {
        var (profile, client) = clients.Get(connection);
        var address = await ResolveAsync(profile, client, entity, ParseSubQueue(subQueue), cancellationToken).ConfigureAwait(false);
        var take = Math.Clamp(maxMessages, 1, MaxPeek);
        var active = filter is { IsEmpty: false } ? filter : new MessageFilter();
        var selection = await MessageSelection.FindAsync(
            client,
            address,
            active,
            take,
            active.IsEmpty ? take : Math.Clamp(scanLimit, 1, MaxScan),
            fromSequenceNumber,
            cancellationToken).ConfigureAwait(false);
        var messages = selection.Messages;

        var perBody = Math.Max(0, bodyMaxChars);
        string? note = null;
        if (messages.Count > 0 && perBody * messages.Count > BodyBudget)
        {
            perBody = Math.Max(120, BodyBudget / messages.Count);
            note = $"Bodies were cut to {perBody} characters to keep the result small. Ask for fewer messages to see more of each body.";
        }

        if (!active.IsEmpty && !selection.ReachedEnd && messages.Count < take)
        {
            note = (note is null ? string.Empty : note + " ")
                + $"Scanned {selection.Scanned} messages without reaching the end; raise scanLimit or page with fromSequenceNumber to search further.";
        }

        var next = messages.Count == take || (!active.IsEmpty && !selection.ReachedEnd)
            ? (messages.Count > 0 ? messages[^1].SequenceNumber : fromSequenceNumber ?? 0) + 1
            : (long?)null;
        return new PeekResult(
            profile.Name,
            address.EntityPath,
            address.SubQueue.ToString(),
            messages.Count,
            selection.Scanned,
            next,
            messages.Select(m => MessageSummary.From(m, perBody)).ToList(),
            note);
    }

    [McpServerTool(Name = "summarize_dead_letters", ReadOnly = true, Idempotent = true)]
    [Description("Peeks up to maxMessages dead-lettered messages of one queue or subscription and groups them by dead-letter reason, "
        + "with the most common error descriptions and subjects for each. Start here when investigating a dead-letter queue.")]
    public async Task<DeadLetterReport> SummarizeDeadLetters(
        [Description("Connection name or id from list_connections.")] string connection,
        [Description("Queue name, or 'topic/subscription'.")] string entity,
        [Description("How many dead-lettered messages to examine, 1-20000. Default 1000.")] int maxMessages = 1000,
        CancellationToken cancellationToken = default)
    {
        var (profile, client) = clients.Get(connection);
        var address = await ResolveAsync(profile, client, entity, SubQueueKind.DeadLetter, cancellationToken).ConfigureAwait(false);
        var messages = await PeekAllAsync(client, address, Math.Clamp(maxMessages, 1, MaxScan), cancellationToken).ConfigureAwait(false);
        var snapshot = await clients.GetSnapshotAsync(profile, client, refresh: false, cancellationToken).ConfigureAwait(false);
        var counts = snapshot.ReadableEntities.FirstOrDefault(e => e.Path == address.EntityPath)?.Counts;

        return new DeadLetterReport(
            profile.Name,
            address.EntityPath,
            counts?.DeadLetter ?? messages.Count,
            messages.Count,
            DeadLetterSummary.ByReason(messages));
    }

    [McpServerTool(Name = "send_message", Destructive = false, Idempotent = false)]
    [Description("Sends a message to a queue or topic, for testing or reproducing a problem. Refuses read-only connections.")]
    public async Task<SendReport> SendMessage(
        [Description("Connection name or id from list_connections.")] string connection,
        [Description("Queue or topic name.")] string entity,
        [Description("Message body as text, usually JSON.")] string body,
        [Description("Content type, for example application/json.")] string? contentType = "application/json",
        [Description("Subject (label).")] string? subject = null,
        [Description("Message ID. Left out, Service Bus generates one.")] string? messageId = null,
        [Description("Correlation ID.")] string? correlationId = null,
        [Description("Session ID; required for session-enabled entities.")] string? sessionId = null,
        [Description("Application properties as a JSON object of names to string, number or boolean values.")] Dictionary<string, JsonElement>? properties = null,
        [Description("Deliver at this time instead of now (ISO 8601).")] DateTimeOffset? scheduledEnqueueTime = null,
        [Description("How many copies to send, 1-100. Default 1.")] int count = 1,
        CancellationToken cancellationToken = default)
    {
        var (profile, client) = WritableClient(connection);
        var snapshot = await clients.GetSnapshotAsync(profile, client, refresh: false, cancellationToken).ConfigureAwait(false);
        var target = SendTarget(snapshot, entity, profile);
        var copies = Math.Clamp(count, 1, 100);
        var message = new OutgoingMessage
        {
            Body = Encoding.UTF8.GetBytes(body),
            ContentType = contentType,
            Subject = subject,
            MessageId = messageId,
            CorrelationId = correlationId,
            SessionId = sessionId,
            ScheduledEnqueueTime = scheduledEnqueueTime,
            ApplicationProperties = properties?.ToDictionary(p => p.Key, p => ToValue(p.Value)) ?? [],
        };
        await client.SendAsync(target, Enumerable.Repeat(message, copies).ToList(), cancellationToken).ConfigureAwait(false);
        return new SendReport(profile.Name, target, copies, scheduledEnqueueTime);
    }

    [McpServerTool(Name = "resubmit_dead_letters", Destructive = true, Idempotent = false)]
    [Description("Sends dead-lettered messages back to their queue (or to the topic, for a subscription) and removes them from the dead-letter queue. "
        + "Choose messages by sequenceNumbers, by a filter (for example one dead-letter reason), or all=true. "
        + "A copy is sent before the original is removed, so a failure can leave a duplicate but never loses a message. "
        + "Refuses read-only connections. Check summarize_dead_letters or peek_messages first, and read the warnings in the result.")]
    public async Task<ResubmitReport> ResubmitDeadLetters(
        [Description("Connection name or id from list_connections.")] string connection,
        [Description("Queue name, or 'topic/subscription'.")] string entity,
        [Description("Sequence numbers of the messages to resubmit, from peek_messages.")] long[]? sequenceNumbers = null,
        [Description("Resubmit every dead-lettered message that matches all of these conditions.")] MessageFilter? filter = null,
        [Description("Set to true to resubmit every message in the dead-letter queue.")] bool all = false,
        [Description("Give each copy a new message ID (the old one goes in the OriginalMessageId property). Needed when the target has duplicate detection.")] bool newMessageId = false,
        [Description("'deadletter' (default) or 'transferdeadletter'.")] string subQueue = "deadletter",
        [Description("With a filter: how many messages to scan, up to 20000. Default 5000.")] int scanLimit = DefaultScan,
        CancellationToken cancellationToken = default)
    {
        var (profile, client) = WritableClient(connection);
        var kind = ParseSubQueue(subQueue);
        if (kind == SubQueueKind.Active)
        {
            throw new McpException("Resubmit reads from 'deadletter' or 'transferdeadletter', not from the active queue.");
        }

        var address = await ResolveAsync(profile, client, entity, kind, cancellationToken).ConfigureAwait(false);
        var chosen = all ? null : await ChooseAsync(client, address, sequenceNumbers, filter, scanLimit, "resubmit", cancellationToken).ConfigureAwait(false);
        var snapshot = await clients.GetSnapshotAsync(profile, client, refresh: true, cancellationToken).ConfigureAwait(false);
        var request = new ResubmitRequest { SequenceNumbers = chosen?.Select(m => m.SequenceNumber).ToList(), NewMessageId = newMessageId };
        var result = chosen is { Count: 0 }
            ? new ResubmitResult(ResubmitAdvice.TargetOf(address), 0, 0, [])
            : await OperationJournal.Default.RecordAsync(
                Entry(profile, "resubmit", ResubmitAdvice.TargetOf(address)),
                client,
                address,
                chosen,
                request.SequenceNumbers,
                () => client.ResubmitAsync(address, request, cancellationToken: cancellationToken),
                r => ($"Resubmitted {r.Resubmitted} to '{r.Target}', {r.NotFound} already gone", r.Errors),
                cancellationToken).ConfigureAwait(false);

        return new ResubmitReport(
            profile.Name,
            address.DisplayName,
            result.Target,
            result.Resubmitted,
            result.NotFound,
            result.Errors,
            ResubmitAdvice.Warnings(snapshot, address, newMessageId));
    }

    [McpServerTool(Name = "delete_messages", Destructive = true, Idempotent = true)]
    [Description("Deletes messages chosen by sequenceNumbers or by a filter. Scheduled messages are cancelled. In a dead-letter queue this is harmless to other messages. "
        + "In an active queue, finding them means receiving (locking) the messages in front of them, which raises their delivery count; read the warnings. "
        + "Refuses read-only connections. This can't be undone.")]
    public async Task<OperationReport> DeleteMessages(
        [Description("Connection name or id from list_connections.")] string connection,
        [Description("Queue name, or 'topic/subscription'.")] string entity,
        [Description("Sequence numbers to delete, from peek_messages.")] long[]? sequenceNumbers = null,
        [Description("Delete every message that matches all of these conditions.")] MessageFilter? filter = null,
        [Description("'deadletter' (default), 'active' or 'transferdeadletter'.")] string subQueue = "deadletter",
        [Description("With a filter: how many messages to scan, up to 20000. Default 5000.")] int scanLimit = DefaultScan,
        CancellationToken cancellationToken = default)
    {
        var (profile, client) = WritableClient(connection);
        var address = await ResolveAsync(profile, client, entity, ParseSubQueue(subQueue), cancellationToken).ConfigureAwait(false);
        var chosen = await ChooseAsync(client, address, sequenceNumbers, filter, scanLimit, "delete", cancellationToken).ConfigureAwait(false);
        var wanted = sequenceNumbers is { Length: > 0 } && filter is null or { IsEmpty: true }
            ? sequenceNumbers
            : chosen.Select(m => m.SequenceNumber).ToArray();
        var snapshot = await clients.GetSnapshotAsync(profile, client, refresh: true, cancellationToken).ConfigureAwait(false);
        var info = snapshot.ReadableEntities.FirstOrDefault(e => e.Path == address.EntityPath);
        var request = new DeleteRequest
        {
            SequenceNumbers = wanted,
            ScheduledSequenceNumbers = chosen.Where(m => m.State == MessageState.Scheduled).Select(m => m.SequenceNumber).ToList(),
        };
        var result = wanted.Length == 0
            ? new MessageOperationResult(0, 0, [])
            : await OperationJournal.Default.RecordAsync(
                Entry(profile, "delete"),
                client,
                address,
                chosen,
                wanted,
                () => client.DeleteAsync(address, request, cancellationToken: cancellationToken),
                r => ($"Deleted {r.Count}, {r.NotFound} already gone", r.Errors),
                cancellationToken).ConfigureAwait(false);
        return new OperationReport(profile.Name, address.DisplayName, result.Count, result.NotFound, result.Errors, DeleteAdvice.ForDelete(info, address.SubQueue));
    }

    [McpServerTool(Name = "copy_messages", Destructive = true, Idempotent = false)]
    [Description("Copies messages chosen by sequenceNumbers or a filter to another queue or topic, in the same namespace or another saved connection. "
        + "With move=true the originals are deleted after the copies were sent (a failure can duplicate but never lose a message). "
        + "The target connection must not be read-only, and neither may the source when moving.")]
    public async Task<TransferReport> CopyMessages(
        [Description("Source connection name or id.")] string connection,
        [Description("Source queue name, or 'topic/subscription'.")] string entity,
        [Description("Queue or topic to send the copies to.")] string targetEntity,
        [Description("Target connection; defaults to the source connection.")] string? targetConnection = null,
        [Description("Sequence numbers to copy, from peek_messages.")] long[]? sequenceNumbers = null,
        [Description("Copy every message that matches all of these conditions.")] MessageFilter? filter = null,
        [Description("'deadletter' (default), 'active' or 'transferdeadletter'.")] string subQueue = "deadletter",
        [Description("Delete the originals after copying.")] bool move = false,
        [Description("Give each copy a new message ID (the old one goes in the OriginalMessageId property).")] bool newMessageId = false,
        [Description("With a filter: how many messages to scan, up to 20000. Default 5000.")] int scanLimit = DefaultScan,
        CancellationToken cancellationToken = default)
    {
        var (profile, client) = move ? WritableClient(connection) : clients.Get(connection);
        var (targetProfile, targetClient) = WritableClient(targetConnection ?? connection);
        var address = await ResolveAsync(profile, client, entity, ParseSubQueue(subQueue), cancellationToken).ConfigureAwait(false);
        var targetSnapshot = await clients.GetSnapshotAsync(targetProfile, targetClient, refresh: false, cancellationToken).ConfigureAwait(false);
        var target = SendTarget(targetSnapshot, targetEntity, targetProfile);
        var chosen = await ChooseAsync(client, address, sequenceNumbers, filter, scanLimit, move ? "move" : "copy", cancellationToken).ConfigureAwait(false);
        var numbers = sequenceNumbers is { Length: > 0 } && filter is null or { IsEmpty: true }
            ? sequenceNumbers
            : chosen.Select(m => m.SequenceNumber).ToArray();
        var result = numbers.Length == 0
            ? new TransferResult(0, 0, 0, [])
            : move
                ? await OperationJournal.Default.RecordAsync(
                    Entry(profile, "move", $"{targetProfile.Name}/{target}"),
                    client,
                    address,
                    chosen,
                    numbers,
                    () => MessageTransfer.CopyAsync(client, address, numbers, targetClient, target, move, newMessageId, cancellationToken),
                    r => ($"Sent {r.Sent}, removed {r.Removed}, {r.NotFound} not found", r.Errors),
                    cancellationToken).ConfigureAwait(false)
                : await MessageTransfer.CopyAsync(client, address, numbers, targetClient, target, move, newMessageId, cancellationToken).ConfigureAwait(false);
        return new TransferReport(profile.Name, address.DisplayName, targetProfile.Name, target, result.Sent, result.Removed, result.NotFound, result.Errors);
    }

    [McpServerTool(Name = "purge", Destructive = true, Idempotent = true)]
    [Description("Removes every message from one sub-queue of a queue or subscription. Only messages enqueued before the purge starts are removed from an active queue. "
        + "Refuses read-only connections. This can't be undone: confirm with the user first, and pass the entity path again as confirmEntity.")]
    public async Task<OperationReport> Purge(
        [Description("Connection name or id from list_connections.")] string connection,
        [Description("Queue name, or 'topic/subscription'.")] string entity,
        [Description("'deadletter', 'active' or 'transferdeadletter'.")] string subQueue,
        [Description("The same entity path again, as a guard against purging the wrong thing.")] string confirmEntity,
        CancellationToken cancellationToken = default)
    {
        var (profile, client) = WritableClient(connection);
        if (!string.Equals(entity.Trim(), confirmEntity.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException("confirmEntity must repeat the entity path exactly.");
        }

        var address = await ResolveAsync(profile, client, entity, ParseSubQueue(subQueue), cancellationToken).ConfigureAwait(false);
        var snapshot = await clients.GetSnapshotAsync(profile, client, refresh: true, cancellationToken).ConfigureAwait(false);
        var info = snapshot.ReadableEntities.FirstOrDefault(e => e.Path == address.EntityPath);
        var result = await OperationJournal.Default.RecordAsync(
            Entry(profile, "purge"),
            client,
            address,
            known: null,
            sequenceNumbers: null,
            () => client.PurgeAsync(address, cancellationToken: cancellationToken),
            r => ($"Purged {r.Count}", r.Errors),
            cancellationToken).ConfigureAwait(false);
        return new OperationReport(profile.Name, address.DisplayName, result.Count, result.NotFound, result.Errors, DeleteAdvice.ForPurge(info, [address.SubQueue]));
    }

    internal static SubQueueKind ParseSubQueue(string value) =>
        value.Trim().Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant() switch
        {
            "" or "active" or "main" => SubQueueKind.Active,
            "deadletter" or "dlq" or "deadletterqueue" or "$deadletterqueue" => SubQueueKind.DeadLetter,
            "transferdeadletter" or "tdlq" or "transferdeadletterqueue" or "$transferdeadletterqueue" => SubQueueKind.TransferDeadLetter,
            _ => throw new McpException($"Unknown subQueue '{value}'. Use 'active', 'deadletter' or 'transferdeadletter'."),
        };

    // Every change is journalled in ~/.queuejanne/journal with a backup of the messages, like changes made in the app.
    private static JournalEntry Entry(ConnectionProfile profile, string operation, string? target = null) => new()
    {
        Source = "mcp",
        Operation = operation,
        Connection = profile.Name,
        Namespace = profile.Namespace ?? string.Empty,
        Target = target,
    };

    private (ConnectionProfile Profile, INamespaceClient Client) WritableClient(string connection)
    {
        var (profile, client) = clients.Get(connection);
        return profile.ReadOnly
            ? throw new McpException($"'{profile.Name}' is read-only, so QueueJanne won't change its messages. The user can turn read-only off in the desktop app.")
            : (profile, client);
    }

    /// <summary>
    /// The messages an operation should act on. With only sequence numbers, a short scan finds their state (to cancel scheduled ones);
    /// with a filter, matches are found by peeking.
    /// </summary>
    private static async Task<IReadOnlyList<QueueMessage>> ChooseAsync(
        INamespaceClient client,
        EntityAddress address,
        long[]? sequenceNumbers,
        MessageFilter? filter,
        int scanLimit,
        string verb,
        CancellationToken cancellationToken)
    {
        var hasFilter = filter is { IsEmpty: false };
        if (!hasFilter && sequenceNumbers is not { Length: > 0 })
        {
            throw new McpException($"Say which messages to {verb}: pass sequenceNumbers or a filter.");
        }

        var scan = Math.Clamp(scanLimit, 1, MaxScan);
        if (hasFilter)
        {
            var selection = await MessageSelection.FindAsync(client, address, filter!, MaxScan, scan, cancellationToken: cancellationToken).ConfigureAwait(false);
            var wanted = sequenceNumbers is { Length: > 0 } ? sequenceNumbers.ToHashSet() : null;
            return wanted is null ? selection.Messages : selection.Messages.Where(m => wanted.Contains(m.SequenceNumber)).ToList();
        }

        var numbers = sequenceNumbers!.ToHashSet();
        var found = new List<QueueMessage>();
        var options = new PeekOptions { MaxMessages = Math.Min(scan, DefaultScan), FromSequenceNumber = numbers.Min() };
        await foreach (var page in client.PeekAsync(address, options, cancellationToken).ConfigureAwait(false))
        {
            found.AddRange(page.Where(m => numbers.Contains(m.SequenceNumber)));
            if (found.Count == numbers.Count)
            {
                break;
            }
        }

        return found;
    }

    private static string SendTarget(NamespaceSnapshot snapshot, string entity, ConnectionProfile profile)
    {
        var name = entity.Trim();
        var match = snapshot.Queues.Select(q => q.Name).Concat(snapshot.Topics.Select(t => t.Topic.Name))
            .FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new McpException(name.Contains('/', StringComparison.Ordinal)
            ? "Messages are sent to a queue or a topic, not to a subscription. Send to the topic; its subscription rules decide who gets a copy."
            : $"No queue or topic called '{name}' in {profile.Name}. Call list_entities to see what exists.");
    }

    private static object? ToValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var whole) => whole,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.Null => null,
        _ => value.GetRawText(),
    };

    private async Task<EntityAddress> ResolveAsync(ConnectionProfile profile, INamespaceClient client, string entity, SubQueueKind subQueue, CancellationToken cancellationToken)
    {
        // Queue names may contain '/', so an exact queue name wins over the topic/subscription reading.
        var snapshot = await clients.GetSnapshotAsync(profile, client, refresh: false, cancellationToken).ConfigureAwait(false);
        var queue = snapshot.Queues.FirstOrDefault(q => q.Name.Equals(entity.Trim(), StringComparison.OrdinalIgnoreCase));
        if (queue is not null)
        {
            return EntityAddress.For(queue, subQueue);
        }

        EntityAddress address;
        try
        {
            address = EntityAddress.Parse(entity, subQueue);
        }
        catch (FormatException ex)
        {
            throw new McpException(ex.Message);
        }

        var known = snapshot.ReadableEntities.FirstOrDefault(e => e.Path.Equals(address.EntityPath, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            var topic = snapshot.Topics.FirstOrDefault(t => t.Topic.Name.Equals(entity.Trim(), StringComparison.OrdinalIgnoreCase));
            throw new McpException(topic is not null
                ? $"'{topic.Topic.Name}' is a topic; read one of its subscriptions: {string.Join(", ", topic.Subscriptions.Select(s => s.Path))}."
                : $"No queue or subscription called '{entity}' in {profile.Name}. Call list_entities to see what exists.");
        }

        return EntityAddress.For(known, address.SubQueue);
    }

    private static async Task<List<QueueMessage>> PeekAllAsync(INamespaceClient client, EntityAddress address, int take, CancellationToken cancellationToken)
    {
        var messages = new List<QueueMessage>(Math.Min(take, 1000));
        var options = new PeekOptions { MaxMessages = take, PageSize = Math.Min(take, 250) };
        await foreach (var page in client.PeekAsync(address, options, cancellationToken).ConfigureAwait(false))
        {
            messages.AddRange(page);
        }

        return messages;
    }
}
