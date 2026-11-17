using System.Runtime.CompilerServices;
using Azure;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using QueueJanne.Core.Connections;
using QueueJanne.Core.Entities;
using QueueJanne.Core.Messages;

namespace QueueJanne.Core.ServiceBus;

/// <summary>A real namespace: the administration API for entities and counts, AMQP for messages.</summary>
public sealed class ServiceBusNamespaceClient : INamespaceClient
{
    private const int MaxParallelTopics = 8;
    private const int ReceiveBatch = 100;

    private readonly ServiceBusClient _client;
    private readonly ServiceBusAdministrationClient _administration;

    private ServiceBusNamespaceClient(string hostName, ServiceBusClient client, ServiceBusAdministrationClient administration)
    {
        Namespace = hostName;
        _client = client;
        _administration = administration;
    }

    public string Namespace { get; }

    public static ServiceBusNamespaceClient FromConnectionString(string connectionString, bool useWebSockets = false)
    {
        var hostName = ConnectionProfile.NamespaceFromConnectionString(connectionString)
            ?? throw new FormatException("The connection string has no Endpoint=sb://… part.");
        return new ServiceBusNamespaceClient(
            hostName,
            new ServiceBusClient(connectionString, CreateOptions(useWebSockets)),
            new ServiceBusAdministrationClient(connectionString));
    }

    public static ServiceBusNamespaceClient FromCredential(string fullyQualifiedNamespace, TokenCredential credential, bool useWebSockets = false) =>
        new(
            fullyQualifiedNamespace,
            new ServiceBusClient(fullyQualifiedNamespace, credential, CreateOptions(useWebSockets)),
            new ServiceBusAdministrationClient(fullyQualifiedNamespace, credential));

    public async Task<NamespaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var queues = LoadQueuesAsync(cancellationToken);
        var topics = LoadTopicsAsync(cancellationToken);
        await Task.WhenAll(queues, topics).ConfigureAwait(false);
        return new NamespaceSnapshot(await queues.ConfigureAwait(false), await topics.ConfigureAwait(false), DateTimeOffset.UtcNow);
    }

    public async IAsyncEnumerable<IReadOnlyList<QueueMessage>> PeekAsync(
        EntityAddress address,
        PeekOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var receiverOptions = new ServiceBusReceiverOptions { SubQueue = ToSdk(address.SubQueue) };
        await using var receiver = address.Kind == EntityKind.Subscription
            ? _client.CreateReceiver(address.TopicName!, address.Name, receiverOptions)
            : _client.CreateReceiver(address.Name, receiverOptions);

        var next = options.FromSequenceNumber;
        var remaining = options.MaxMessages;
        while (remaining > 0)
        {
            var batch = await receiver
                .PeekMessagesAsync(Math.Min(options.PageSize, remaining), next, cancellationToken)
                .ConfigureAwait(false);
            if (batch.Count == 0)
            {
                yield break;
            }

            remaining -= batch.Count;
            next = batch[^1].SequenceNumber + 1;
            yield return batch.Select(MessageMapper.Map).ToList();
        }
    }

    public async Task<ResubmitResult> ResubmitAsync(EntityAddress source, ResubmitRequest request, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (source.SubQueue == SubQueueKind.Active)
        {
            throw new InvalidOperationException("Resubmit reads from a dead-letter queue, not from the active queue.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var target = ResubmitAdvice.TargetOf(source);
        await using var receiver = CreateLockingReceiver(source);
        await using var sender = _client.CreateSender(target);
        var wanted = request.SequenceNumbers is null ? null : new HashSet<long>(request.SequenceNumbers);
        var expected = await CountAsync(source, cancellationToken).ConfigureAwait(false);
        var (count, errors) = await ProcessSelectedAsync(
            receiver,
            expected,
            wanted,
            wanted is null && expected is { } held ? (int)Math.Min(request.MaxMessages, held) : request.MaxMessages,
            async (selected, ct) =>
            {
                try
                {
                    await SendCopiesAsync(sender, selected, request.NewMessageId, ct).ConfigureAwait(false);
                    return null;
                }
                catch (Exception ex) when (ex is ServiceBusException or InvalidOperationException)
                {
                    return $"Couldn't send to '{target}': {ex.Message}";
                }
            },
            progress,
            cancellationToken,
            take: wanted is null ? message => message.EnqueuedTime < startedAt : null).ConfigureAwait(false);
        return new ResubmitResult(target, count, wanted?.Count ?? 0, errors);
    }

    public async Task<MessageOperationResult> DeleteAsync(EntityAddress address, DeleteRequest request, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var scheduled = request.ScheduledSequenceNumbers.Intersect(request.SequenceNumbers).ToList();
        var cancelled = 0;
        if (scheduled.Count > 0 && address is { Kind: EntityKind.Queue, SubQueue: SubQueueKind.Active })
        {
            await using var sender = _client.CreateSender(address.Name);
            try
            {
                await sender.CancelScheduledMessagesAsync(scheduled, cancellationToken).ConfigureAwait(false);
                cancelled = scheduled.Count;
            }
            catch (ServiceBusException ex)
            {
                errors.Add($"Couldn't cancel the scheduled messages: {ex.Message}");
            }
        }

        var wanted = new HashSet<long>(request.SequenceNumbers.Except(cancelled > 0 ? scheduled : []));
        if (wanted.Count == 0)
        {
            return new MessageOperationResult(cancelled, 0, errors);
        }

        await using var receiver = CreateLockingReceiver(address);
        var expected = await CountAsync(address, cancellationToken).ConfigureAwait(false);
        var (count, receiveErrors) = await ProcessSelectedAsync(receiver, expected, wanted, int.MaxValue, (_, _) => Task.FromResult<string?>(null), progress, cancellationToken).ConfigureAwait(false);
        errors.AddRange(receiveErrors);
        return new MessageOperationResult(cancelled + count, wanted.Count, errors);
    }

    public async Task<MessageOperationResult> PurgeAsync(
        EntityAddress address,
        IProgress<int>? progress = null,
        Func<IReadOnlyList<QueueMessage>, Task>? beforeRemove = null,
        CancellationToken cancellationToken = default)
    {
        // Messages that arrive while purging stay; only those enqueued before it started are removed. A dead-letter
        // queue gets nothing new from senders, so it is emptied completely.
        var startedAt = DateTimeOffset.UtcNow;
        var expected = await CountAsync(address, cancellationToken).ConfigureAwait(false);
        if (beforeRemove is null && address.SubQueue != SubQueueKind.Active)
        {
            return await ReceiveAndDeleteAllAsync(address, expected, progress, cancellationToken).ConfigureAwait(false);
        }

        await using var receiver = CreateLockingReceiver(address);
        var (count, errors) = await ProcessSelectedAsync(
            receiver,
            expected,
            null,
            int.MaxValue,
            async (selected, _) =>
            {
                if (beforeRemove is not null)
                {
                    await beforeRemove(selected.Select(MessageMapper.Map).ToList()).ConfigureAwait(false);
                }

                return null;
            },
            progress,
            cancellationToken,
            take: address.SubQueue == SubQueueKind.Active ? message => message.EnqueuedTime < startedAt : null).ConfigureAwait(false);
        return new MessageOperationResult(count, 0, errors);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    /// <summary>Empties a dead-letter sub-queue without locking: nothing else reads it, and nothing needs backing up first.</summary>
    private async Task<MessageOperationResult> ReceiveAndDeleteAllAsync(EntityAddress address, long? expected, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var options = new ServiceBusReceiverOptions { SubQueue = ToSdk(address.SubQueue), ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete, PrefetchCount = 500 };
        await using var receiver = address.Kind == EntityKind.Subscription
            ? _client.CreateReceiver(address.TopicName!, address.Name, options)
            : _client.CreateReceiver(address.Name, options);
        var deleted = 0;
        while (true)
        {
            var wait = deleted >= expected ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(3);
            var batch = await receiver.ReceiveMessagesAsync(500, wait, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                return new MessageOperationResult(deleted, 0, []);
            }

            deleted += batch.Count;
            progress?.Report(deleted);
        }
    }

    /// <summary>
    /// A receiver that locks. Nothing else reads a dead-letter queue, so there it prefetches the next batch; on an active
    /// queue prefetched messages would sit locked away from the consumers, so it doesn't.
    /// </summary>
    private ServiceBusReceiver CreateLockingReceiver(EntityAddress address)
    {
        var options = new ServiceBusReceiverOptions { SubQueue = ToSdk(address.SubQueue), PrefetchCount = address.SubQueue == SubQueueKind.Active ? 0 : ReceiveBatch };
        return address.Kind == EntityKind.Subscription
            ? _client.CreateReceiver(address.TopicName!, address.Name, options)
            : _client.CreateReceiver(address.Name, options);
    }

    /// <summary>
    /// Receives with a lock, hands the chosen messages to <paramref name="beforeComplete"/> and removes them when it succeeds,
    /// and releases every other message it received at the end. With <paramref name="wanted"/> null, every message counts up to <paramref name="max"/>.
    /// </summary>
    /// <summary>How many messages the sub-queue holds right now, or null when the count can't be read.</summary>
    private async Task<long?> CountAsync(EntityAddress address, CancellationToken cancellationToken)
    {
        try
        {
            if (address.Kind == EntityKind.Subscription)
            {
                var subscription = (await _administration.GetSubscriptionRuntimePropertiesAsync(address.TopicName!, address.Name, cancellationToken).ConfigureAwait(false)).Value;
                return Pick(subscription.ActiveMessageCount, subscription.DeadLetterMessageCount, subscription.TransferDeadLetterMessageCount);
            }

            var queue = (await _administration.GetQueueRuntimePropertiesAsync(address.Name, cancellationToken).ConfigureAwait(false)).Value;
            return Pick(queue.ActiveMessageCount, queue.DeadLetterMessageCount, queue.TransferDeadLetterMessageCount);
        }
        catch (Exception ex) when (ex is RequestFailedException or UnauthorizedAccessException or ServiceBusException)
        {
            return null;
        }

        long Pick(long active, long deadLetter, long transferDeadLetter) => address.SubQueue switch
        {
            SubQueueKind.DeadLetter => deadLetter,
            SubQueueKind.TransferDeadLetter => transferDeadLetter,
            _ => active,
        };
    }

    private static async Task<(int Count, List<string> Errors)> ProcessSelectedAsync(
        ServiceBusReceiver receiver,
        long? expected,
        HashSet<long>? wanted,
        int max,
        Func<IReadOnlyList<ServiceBusReceivedMessage>, CancellationToken, Task<string?>> beforeComplete,
        IProgress<int>? progress,
        CancellationToken cancellationToken,
        Func<ServiceBusReceivedMessage, bool>? take = null)
    {
        var seen = new HashSet<long>();
        var passedOver = new List<ServiceBusReceivedMessage>();
        var errors = new List<string>();
        var done = 0;
        try
        {
            while ((wanted is null ? done < max : wanted.Count > 0) && errors.Count == 0)
            {
                // Once every message the sub-queue held has come by, an empty receive only has to rule out stragglers.
                var wait = seen.Count >= expected ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(5);
                var batch = await receiver.ReceiveMessagesAsync(ReceiveBatch, wait, cancellationToken).ConfigureAwait(false);
                var selected = new List<ServiceBusReceivedMessage>();
                var anyNew = false;
                var reachedEnd = false;
                foreach (var message in batch)
                {
                    // A message seen twice had its lock expire while we held it, so it isn't one we want.
                    var isNew = seen.Add(message.SequenceNumber);
                    anyNew |= isNew;
                    var chosen = isNew
                        && (take?.Invoke(message) ?? true)
                        && (wanted is null ? done + selected.Count < max : wanted.Remove(message.SequenceNumber));
                    reachedEnd |= isNew && take is not null && !take(message);
                    (chosen ? selected : passedOver).Add(message);
                }

                if (!anyNew)
                {
                    break;
                }

                if (selected.Count > 0)
                {
                    if (await beforeComplete(selected, cancellationToken).ConfigureAwait(false) is { } error)
                    {
                        errors.Add(error);
                        passedOver.AddRange(selected);
                        break;
                    }

                    var completions = await Task.WhenAll(selected.Select(async message =>
                    {
                        try
                        {
                            await receiver.CompleteMessageAsync(message, cancellationToken).ConfigureAwait(false);
                            return null;
                        }
                        catch (ServiceBusException ex)
                        {
                            return $"Message {message.SequenceNumber} couldn't be removed: {ex.Message}";
                        }
                    })).ConfigureAwait(false);
                    done += completions.Count(e => e is null);
                    errors.AddRange(completions.OfType<string>());
                    progress?.Report(done);
                }

                if (reachedEnd)
                {
                    break;
                }
            }
        }
        finally
        {
            await Task.WhenAll(passedOver.Select(async message =>
            {
                try
                {
                    await receiver.AbandonMessageAsync(message, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                catch (ServiceBusException)
                {
                    // The lock already expired, so the message is available again anyway.
                }
            })).ConfigureAwait(false);
        }

        return (done, errors);
    }

    public async Task SendAsync(string entityName, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default)
    {
        await using var sender = _client.CreateSender(entityName);
        await SendBatchedAsync(sender, messages.Select(ToSdk), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RuleInfo>> GetRulesAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default)
    {
        var rules = new List<RuleInfo>();
        await foreach (var rule in _administration.GetRulesAsync(topicName, subscriptionName, cancellationToken).ConfigureAwait(false))
        {
            var filter = rule.Filter switch
            {
                TrueRuleFilter => "1=1 (every message)",
                FalseRuleFilter => "1=0 (no messages)",
                SqlRuleFilter sql => sql.SqlExpression,
                CorrelationRuleFilter correlation => DescribeCorrelation(correlation),
                var other => other?.ToString() ?? string.Empty,
            };
            rules.Add(new RuleInfo(rule.Name, filter, (rule.Action as SqlRuleAction)?.SqlExpression));
        }

        return rules;
    }

    private static string DescribeCorrelation(CorrelationRuleFilter filter)
    {
        var parts = new List<string>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parts.Add($"{name} = '{value}'");
            }
        }

        Add("CorrelationId", filter.CorrelationId);
        Add("MessageId", filter.MessageId);
        Add("To", filter.To);
        Add("ReplyTo", filter.ReplyTo);
        Add("Subject", filter.Subject);
        Add("SessionId", filter.SessionId);
        Add("ReplyToSessionId", filter.ReplyToSessionId);
        Add("ContentType", filter.ContentType);
        foreach (var (key, value) in filter.ApplicationProperties)
        {
            parts.Add($"{key} = '{value}'");
        }

        return "Correlation: " + (parts.Count == 0 ? "(empty)" : string.Join(" AND ", parts));
    }

    private static ServiceBusMessage ToSdk(OutgoingMessage message)
    {
        var sdk = new ServiceBusMessage(message.Body)
        {
            ContentType = message.ContentType,
            Subject = message.Subject,
            CorrelationId = message.CorrelationId,
            SessionId = message.SessionId,
            To = message.To,
            ReplyTo = message.ReplyTo,
        };
        if (message.MessageId is { Length: > 0 } id)
        {
            sdk.MessageId = id;
        }

        if (message.PartitionKey is { Length: > 0 } partitionKey && message.SessionId is null)
        {
            sdk.PartitionKey = partitionKey;
        }

        if (message.TimeToLive is { } ttl)
        {
            sdk.TimeToLive = ttl;
        }

        if (message.ScheduledEnqueueTime is { } scheduled)
        {
            sdk.ScheduledEnqueueTime = scheduled;
        }

        foreach (var (key, value) in message.ApplicationProperties)
        {
            sdk.ApplicationProperties[key] = value;
        }

        return sdk;
    }

    private static Task SendCopiesAsync(ServiceBusSender sender, IReadOnlyList<ServiceBusReceivedMessage> messages, bool newMessageId, CancellationToken cancellationToken) =>
        SendBatchedAsync(sender, messages.Select(m => Copy(m, newMessageId)), cancellationToken);

    private static async Task SendBatchedAsync(ServiceBusSender sender, IEnumerable<ServiceBusMessage> messages, CancellationToken cancellationToken)
    {
        var batch = await sender.CreateMessageBatchAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var message in messages)
            {
                if (batch.TryAddMessage(message))
                {
                    continue;
                }

                if (batch.Count == 0)
                {
                    throw new InvalidOperationException($"Message '{message.MessageId}' is too large to send.");
                }

                await sender.SendMessagesAsync(batch, cancellationToken).ConfigureAwait(false);
                batch.Dispose();
                batch = await sender.CreateMessageBatchAsync(cancellationToken).ConfigureAwait(false);
                if (!batch.TryAddMessage(message))
                {
                    throw new InvalidOperationException($"Message '{message.MessageId}' is too large to send.");
                }
            }

            if (batch.Count > 0)
            {
                await sender.SendMessagesAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            batch.Dispose();
        }
    }

    private static ServiceBusMessage Copy(ServiceBusReceivedMessage message, bool newMessageId)
    {
        var copy = new ServiceBusMessage(message);
        copy.ApplicationProperties.Remove("DeadLetterReason");
        copy.ApplicationProperties.Remove("DeadLetterErrorDescription");
        if (newMessageId)
        {
            copy.ApplicationProperties[ResubmitAdvice.OriginalMessageIdProperty] = message.MessageId;
            copy.MessageId = Guid.NewGuid().ToString("N");
        }

        return copy;
    }

    private async Task<IReadOnlyList<EntityInfo>> LoadQueuesAsync(CancellationToken cancellationToken)
    {
        var settings = ToListAsync(_administration.GetQueuesAsync(cancellationToken));
        var runtime = ToListAsync(_administration.GetQueuesRuntimePropertiesAsync(cancellationToken));
        await Task.WhenAll(settings, runtime).ConfigureAwait(false);

        var counts = (await runtime.ConfigureAwait(false)).ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        return (await settings.ConfigureAwait(false))
            .OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
            .Select(q => new EntityInfo(EntityKind.Queue, q.Name, null, ToCounts(counts.GetValueOrDefault(q.Name)), new EntitySettings
            {
                MaxDeliveryCount = q.MaxDeliveryCount,
                LockDuration = q.LockDuration,
                RequiresSession = q.RequiresSession,
                RequiresDuplicateDetection = q.RequiresDuplicateDetection,
                DuplicateDetectionWindow = q.RequiresDuplicateDetection ? q.DuplicateDetectionHistoryTimeWindow : null,
                EnablePartitioning = q.EnablePartitioning,
                DeadLetteringOnMessageExpiration = q.DeadLetteringOnMessageExpiration,
                DefaultMessageTimeToLive = q.DefaultMessageTimeToLive,
                ForwardTo = q.ForwardTo,
                ForwardDeadLetteredMessagesTo = q.ForwardDeadLetteredMessagesTo,
                Status = q.Status.ToString(),
            }))
            .ToList();
    }

    private async Task<IReadOnlyList<TopicInfo>> LoadTopicsAsync(CancellationToken cancellationToken)
    {
        var settings = ToListAsync(_administration.GetTopicsAsync(cancellationToken));
        var runtime = ToListAsync(_administration.GetTopicsRuntimePropertiesAsync(cancellationToken));
        await Task.WhenAll(settings, runtime).ConfigureAwait(false);

        var counts = (await runtime.ConfigureAwait(false)).ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(MaxParallelTopics);
        var topics = (await settings.ConfigureAwait(false))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(async topic =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var subscriptions = await LoadSubscriptionsAsync(topic.Name, cancellationToken).ConfigureAwait(false);
                    var topicRuntime = counts.GetValueOrDefault(topic.Name);
                    var topicCounts = topicRuntime is null
                        ? EntityCounts.Empty
                        : new EntityCounts(0, 0, 0, topicRuntime.ScheduledMessageCount, 0, topicRuntime.SizeInBytes);
                    var topicSettings = new EntitySettings
                    {
                        RequiresDuplicateDetection = topic.RequiresDuplicateDetection,
                        DuplicateDetectionWindow = topic.RequiresDuplicateDetection ? topic.DuplicateDetectionHistoryTimeWindow : null,
                        EnablePartitioning = topic.EnablePartitioning,
                        DefaultMessageTimeToLive = topic.DefaultMessageTimeToLive,
                        Status = topic.Status.ToString(),
                        SubscriptionCount = topicRuntime?.SubscriptionCount ?? subscriptions.Count,
                    };
                    return new TopicInfo(new EntityInfo(EntityKind.Topic, topic.Name, null, topicCounts, topicSettings), subscriptions);
                }
                finally
                {
                    gate.Release();
                }
            })
            .ToList();

        return await Task.WhenAll(topics).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<EntityInfo>> LoadSubscriptionsAsync(string topicName, CancellationToken cancellationToken)
    {
        var settings = ToListAsync(_administration.GetSubscriptionsAsync(topicName, cancellationToken));
        var runtime = ToListAsync(_administration.GetSubscriptionsRuntimePropertiesAsync(topicName, cancellationToken));
        await Task.WhenAll(settings, runtime).ConfigureAwait(false);

        var counts = (await runtime.ConfigureAwait(false)).ToDictionary(r => r.SubscriptionName, StringComparer.OrdinalIgnoreCase);
        return (await settings.ConfigureAwait(false))
            .OrderBy(s => s.SubscriptionName, StringComparer.OrdinalIgnoreCase)
            .Select(s =>
            {
                var subscriptionRuntime = counts.GetValueOrDefault(s.SubscriptionName);
                var subscriptionCounts = subscriptionRuntime is null
                    ? EntityCounts.Empty
                    : new EntityCounts(
                        subscriptionRuntime.ActiveMessageCount,
                        subscriptionRuntime.DeadLetterMessageCount,
                        subscriptionRuntime.TransferDeadLetterMessageCount,
                        0,
                        subscriptionRuntime.TransferMessageCount,
                        0);
                return new EntityInfo(EntityKind.Subscription, s.SubscriptionName, topicName, subscriptionCounts, new EntitySettings
                {
                    MaxDeliveryCount = s.MaxDeliveryCount,
                    LockDuration = s.LockDuration,
                    RequiresSession = s.RequiresSession,
                    DeadLetteringOnMessageExpiration = s.DeadLetteringOnMessageExpiration,
                    DefaultMessageTimeToLive = s.DefaultMessageTimeToLive,
                    ForwardTo = s.ForwardTo,
                    ForwardDeadLetteredMessagesTo = s.ForwardDeadLetteredMessagesTo,
                    Status = s.Status.ToString(),
                });
            })
            .ToList();
    }

    private static EntityCounts ToCounts(QueueRuntimeProperties? runtime) => runtime is null
        ? EntityCounts.Empty
        : new EntityCounts(
            runtime.ActiveMessageCount,
            runtime.DeadLetterMessageCount,
            runtime.TransferDeadLetterMessageCount,
            runtime.ScheduledMessageCount,
            runtime.TransferMessageCount,
            runtime.SizeInBytes);

    private static SubQueue ToSdk(SubQueueKind kind) => kind switch
    {
        SubQueueKind.DeadLetter => SubQueue.DeadLetter,
        SubQueueKind.TransferDeadLetter => SubQueue.TransferDeadLetter,
        _ => SubQueue.None,
    };

    private static ServiceBusClientOptions CreateOptions(bool useWebSockets) => new()
    {
        TransportType = useWebSockets ? ServiceBusTransportType.AmqpWebSockets : ServiceBusTransportType.AmqpTcp,
        RetryOptions = new ServiceBusRetryOptions { MaxRetries = 2, TryTimeout = TimeSpan.FromSeconds(30) },
    };

    private static async Task<List<T>> ToListAsync<T>(AsyncPageable<T> source)
        where T : notnull
    {
        var items = new List<T>();
        await foreach (var item in source.ConfigureAwait(false))
        {
            items.Add(item);
        }

        return items;
    }
}
