using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using QueueJanne.Core.Entities;
using QueueJanne.Core.Messages;
using QueueJanne.Core.ServiceBus;

namespace QueueJanne.Core.Demo;

/// <summary>
/// An in-memory namespace with repeatable data, for trying QueueJanne without Azure and for screenshots.
/// Generated messages are computed from their position; resubmitted ones are kept per instance.
/// </summary>
public sealed class DemoNamespaceClient : INamespaceClient
{
    public const string HostName = "demo.queuejanne.local";

    private const long FirstSequenceNumber = 48_000;
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 5, 0, TimeSpan.Zero);

    private static readonly DemoEntity[] Queues =
    [
        new("orders", Active: 12, DeadLetter: 3),
        new("payments-cmd", Active: 0, DeadLetter: 0),
        new("notifications", Active: 2, DeadLetter: 41, RequiresSession: true),
        new("invoices", Active: 5, DeadLetter: 286, Scheduled: 3, RequiresDuplicateDetection: true),
        new("audit-log", Active: 15_230, DeadLetter: 0, MaxDeliveryCount: 1),
    ];

    private static readonly (string Topic, DemoEntity[] Subscriptions)[] Topics =
    [
        ("billing-events", [new("ledger", Active: 0, DeadLetter: 1_204), new("invoicing", Active: 7, DeadLetter: 0), new("audit", Active: 0, DeadLetter: 7)]),
        ("inventory-events", [new("warehouse", Active: 340, DeadLetter: 0), new("search-index", Active: 0, DeadLetter: 2, TransferDeadLetter: 1)]),
    ];

    private static readonly string[] Subjects = ["OrderCreated", "OrderPaid", "InvoiceRequested", "RefundRequested", "ShipmentBooked", "CustomerUpdated", "StockAdjusted"];

    private static readonly string[] Sources = ["web", "pos", "api", "batch"];

    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<long>> _removed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<QueueMessage>> _added = new(StringComparer.Ordinal);
    private long _nextSequenceNumber = 900_000;

    public string Namespace => HostName;

    public Task<NamespaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var queues = Queues.Select(q => ToInfo(q, EntityKind.Queue, null)).ToList();
            var topics = Topics
                .Select(t => new TopicInfo(
                    new EntityInfo(EntityKind.Topic, t.Topic, null, EntityCounts.Empty, new EntitySettings { SubscriptionCount = t.Subscriptions.Length }),
                    t.Subscriptions.Select(s => ToInfo(s, EntityKind.Subscription, t.Topic)).ToList()))
                .ToList();
            return Task.FromResult(new NamespaceSnapshot(queues, topics, Now));
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<QueueMessage>> PeekAsync(
        EntityAddress address,
        PeekOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<QueueMessage> all;
        lock (_gate)
        {
            all = Current(address).Where(m => m.SequenceNumber >= (options.FromSequenceNumber ?? 0)).Take(options.MaxMessages).ToList();
        }

        for (var index = 0; index < all.Count; index += options.PageSize)
        {
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
            yield return all.Skip(index).Take(options.PageSize).ToList();
        }
    }

    public async Task<ResubmitResult> ResubmitAsync(EntityAddress source, ResubmitRequest request, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (source.SubQueue == SubQueueKind.Active)
        {
            throw new InvalidOperationException("Resubmit reads from a dead-letter queue, not from the active queue.");
        }

        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var available = Current(source).ToList();
            var chosen = request.SequenceNumbers is null
                ? available.Take(request.MaxMessages).ToList()
                : available.Where(m => request.SequenceNumbers.Contains(m.SequenceNumber)).ToList();

            var targets = source.Kind == EntityKind.Subscription
                ? Topics.First(t => t.Topic == source.TopicName).Subscriptions.Select(s => EntityAddress.Subscription(source.TopicName!, s.Name).EntityPath).ToList()
                : [source.Name];

            var removed = Removed(source);
            foreach (var message in chosen)
            {
                removed.Add(message.SequenceNumber);
                foreach (var target in targets)
                {
                    Added(target).Add(CopyForResubmit(message, request.NewMessageId));
                }
            }

            progress?.Report(chosen.Count);
            var notFound = request.SequenceNumbers is null ? 0 : request.SequenceNumbers.Count - chosen.Count;
            return new ResubmitResult(ResubmitAdvice.TargetOf(source), chosen.Count, notFound, []);
        }
    }

    public async Task<MessageOperationResult> DeleteAsync(EntityAddress address, DeleteRequest request, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var wanted = request.SequenceNumbers.ToHashSet();
            var found = Current(address).Where(m => wanted.Contains(m.SequenceNumber)).Select(m => m.SequenceNumber).ToList();
            Removed(address).UnionWith(found);
            progress?.Report(found.Count);
            return new MessageOperationResult(found.Count, wanted.Count - found.Count, []);
        }
    }

    public async Task<MessageOperationResult> PurgeAsync(EntityAddress address, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var all = Current(address).Where(m => m.State != MessageState.Scheduled).Select(m => m.SequenceNumber).ToList();
            Removed(address).UnionWith(all);
            progress?.Report(all.Count);
            return new MessageOperationResult(all.Count, 0, []);
        }
    }

    public async Task SendAsync(string entityName, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default)
    {
        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var targets = Topics.FirstOrDefault(t => t.Topic == entityName) is { Subscriptions: { } subscriptions }
                ? subscriptions.Select(s => EntityAddress.Subscription(entityName, s.Name).EntityPath).ToList()
                : Queues.Any(q => q.Name == entityName)
                    ? [entityName]
                    : throw new InvalidOperationException($"The messaging entity '{entityName}' could not be found.");
            foreach (var message in messages)
            {
                foreach (var target in targets)
                {
                    Added(target).Add(new QueueMessage
                    {
                        SequenceNumber = _nextSequenceNumber++,
                        MessageId = message.MessageId ?? Guid.NewGuid().ToString("N"),
                        Subject = message.Subject,
                        ContentType = message.ContentType,
                        CorrelationId = message.CorrelationId,
                        SessionId = message.SessionId,
                        PartitionKey = message.PartitionKey,
                        To = message.To,
                        ReplyTo = message.ReplyTo,
                        EnqueuedTime = DateTimeOffset.UtcNow,
                        TimeToLive = message.TimeToLive ?? TimeSpan.FromDays(14),
                        ExpiresAt = DateTimeOffset.UtcNow + (message.TimeToLive ?? TimeSpan.FromDays(14)),
                        ApplicationProperties = message.ApplicationProperties,
                        Body = message.Body,
                    });
                }
            }
        }
    }

    public Task<IReadOnlyList<RuleInfo>> GetRulesAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RuleInfo>>([new RuleInfo("$Default", "1=1 (every message)", null)]);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static DemoEntity Find(EntityAddress address)
    {
        var entity = address.Kind == EntityKind.Subscription
            ? Topics.Where(t => t.Topic == address.TopicName).SelectMany(t => t.Subscriptions).FirstOrDefault(s => s.Name == address.Name)
            : Queues.FirstOrDefault(q => q.Name == address.Name);
        return entity ?? throw new InvalidOperationException($"The messaging entity '{address.EntityPath}' could not be found.");
    }

    private static string Key(string path, SubQueueKind subQueue) => $"{path}|{subQueue}";

    private static long GeneratedCount(DemoEntity entity, SubQueueKind subQueue) => subQueue switch
    {
        SubQueueKind.DeadLetter => entity.DeadLetter,
        SubQueueKind.TransferDeadLetter => entity.TransferDeadLetter,
        _ => entity.Active + entity.Scheduled,
    };

    private IEnumerable<QueueMessage> Current(EntityAddress address)
    {
        var entity = Find(address);
        var total = GeneratedCount(entity, address.SubQueue);
        var removed = _removed.GetValueOrDefault(Key(address.EntityPath, address.SubQueue));
        for (long index = 0; index < total; index++)
        {
            if (removed is null || !removed.Contains(FirstSequenceNumber + index))
            {
                yield return CreateMessage(address, entity, index, total);
            }
        }

        foreach (var message in _added.GetValueOrDefault(Key(address.EntityPath, address.SubQueue)) ?? [])
        {
            if (removed is null || !removed.Contains(message.SequenceNumber))
            {
                yield return message;
            }
        }
    }

    private HashSet<long> Removed(EntityAddress address)
    {
        var key = Key(address.EntityPath, address.SubQueue);
        if (!_removed.TryGetValue(key, out var set))
        {
            _removed[key] = set = [];
        }

        return set;
    }

    private List<QueueMessage> Added(string activePath)
    {
        var key = Key(activePath, SubQueueKind.Active);
        if (!_added.TryGetValue(key, out var list))
        {
            _added[key] = list = [];
        }

        return list;
    }

    private EntityInfo ToInfo(DemoEntity entity, EntityKind kind, string? topic)
    {
        var path = topic is null ? entity.Name : EntityAddress.Subscription(topic, entity.Name).EntityPath;
        long Count(SubQueueKind subQueue) =>
            GeneratedCount(entity, subQueue)
            - (_removed.GetValueOrDefault(Key(path, subQueue))?.Count ?? 0)
            + (_added.GetValueOrDefault(Key(path, subQueue))?.Count ?? 0);

        var active = Count(SubQueueKind.Active) - entity.Scheduled;
        var deadLetter = Count(SubQueueKind.DeadLetter);
        return new EntityInfo(
            kind,
            entity.Name,
            topic,
            new EntityCounts(active, deadLetter, Count(SubQueueKind.TransferDeadLetter), entity.Scheduled, 0, (active + deadLetter) * 1_250),
            new EntitySettings
            {
                MaxDeliveryCount = entity.MaxDeliveryCount,
                LockDuration = TimeSpan.FromSeconds(60),
                RequiresSession = entity.RequiresSession,
                RequiresDuplicateDetection = entity.RequiresDuplicateDetection,
                DuplicateDetectionWindow = entity.RequiresDuplicateDetection ? TimeSpan.FromMinutes(10) : null,
                DefaultMessageTimeToLive = TimeSpan.FromDays(14),
                DeadLetteringOnMessageExpiration = true,
            });
    }

    private QueueMessage CopyForResubmit(QueueMessage message, bool newMessageId)
    {
        var properties = message.ApplicationProperties.ToDictionary(p => p.Key, p => p.Value);
        if (newMessageId)
        {
            properties[ResubmitAdvice.OriginalMessageIdProperty] = message.MessageId;
        }

        return new QueueMessage
        {
            SequenceNumber = _nextSequenceNumber++,
            MessageId = newMessageId ? Guid.NewGuid().ToString("N") : message.MessageId,
            Subject = message.Subject,
            ContentType = message.ContentType,
            CorrelationId = message.CorrelationId,
            SessionId = message.SessionId,
            PartitionKey = message.PartitionKey,
            To = message.To,
            ReplyTo = message.ReplyTo,
            EnqueuedTime = DateTimeOffset.UtcNow,
            TimeToLive = message.TimeToLive,
            ExpiresAt = DateTimeOffset.UtcNow + (message.TimeToLive ?? TimeSpan.FromDays(14)),
            ApplicationProperties = properties,
            Body = message.Body,
            BodyKind = message.BodyKind,
        };
    }

    private static QueueMessage CreateMessage(EntityAddress address, DemoEntity entity, long index, long total)
    {
        var random = new Random(StableSeed(address.EntityPath, address.SubQueue, index));
        var subject = Subjects[random.Next(Subjects.Length)];
        var enqueued = Now - TimeSpan.FromSeconds(((total - index) * 47) + random.Next(30));
        var (body, contentType) = CreateBody(random, subject, index, enqueued);
        var deadLettered = address.SubQueue != SubQueueKind.Active;
        var (reason, description, deliveries) = deadLettered
            ? DeadLetterDetails(random, subject, entity.MaxDeliveryCount)
            : (null, null, random.Next(0, 2));
        var scheduled = !deadLettered && index >= total - entity.Scheduled;
        var id = new byte[16];
        random.NextBytes(id);

        return new QueueMessage
        {
            SequenceNumber = FirstSequenceNumber + index,
            MessageId = new Guid(id).ToString("N"),
            Subject = subject,
            ContentType = contentType,
            CorrelationId = $"corr-{random.Next(100_000, 999_999)}",
            SessionId = entity.RequiresSession ? $"device-{random.Next(1, 20):00}" : null,
            EnqueuedTime = enqueued,
            ScheduledEnqueueTime = scheduled ? Now.AddMinutes(random.Next(5, 240)) : null,
            ExpiresAt = enqueued.AddDays(14),
            TimeToLive = TimeSpan.FromDays(14),
            DeliveryCount = deliveries,
            DeadLetterReason = reason,
            DeadLetterErrorDescription = description,
            State = scheduled ? MessageState.Scheduled : MessageState.Active,
            ApplicationProperties = new Dictionary<string, object?>
            {
                ["tenant"] = $"t-{random.Next(1, 9):0000}",
                ["region"] = random.Next(3) == 0 ? "us" : "eu",
                ["source"] = Sources[random.Next(Sources.Length)],
            },
            Body = body,
        };
    }

    private static (string Reason, string Description, int DeliveryCount) DeadLetterDetails(Random random, string subject, int maxDeliveryCount)
    {
        var roll = random.Next(100);
        if (roll < 70)
        {
            return ("MaxDeliveryCountExceeded", $"Message could not be consumed after {maxDeliveryCount} delivery attempts.", maxDeliveryCount);
        }

        if (roll < 85)
        {
            return ("TTLExpiredException", "The message expired and was dead lettered.", 0);
        }

        return ("ValidationFailed", $"{subject} rejected: customer C-{random.Next(1000, 9999)} does not exist.", 1);
    }

    private static (byte[] Body, string ContentType) CreateBody(Random random, string subject, long index, DateTimeOffset enqueued)
    {
        if (index % 31 == 30)
        {
            var bytes = new byte[96];
            random.NextBytes(bytes);
            return (bytes, "application/octet-stream");
        }

        if (index % 17 == 16)
        {
            var (plain, _) = CreateBody(random, subject, 0, enqueued);
            using var output = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal))
            {
                gzip.Write(plain);
            }

            return (output.ToArray(), "application/json");
        }

        if (index % 13 == 12)
        {
            return (Encoding.UTF8.GetBytes($"Reindex product SKU-{random.Next(100, 999)} requested by {Sources[random.Next(Sources.Length)]}"), "text/plain");
        }

        if (index % 9 == 8)
        {
            var xml = $"<invoice id=\"INV-{random.Next(10_000, 99_999)}\"><customer>C-{random.Next(1000, 9999)}</customer>"
                + $"<amount currency=\"SEK\">{random.Next(50, 5000)}.{random.Next(0, 99):00}</amount><issued>{enqueued:O}</issued></invoice>";
            return (Encoding.UTF8.GetBytes(xml), "application/xml");
        }

        var json = JsonSerializer.Serialize(new
        {
            type = subject,
            orderId = $"ORD-{random.Next(10_000, 99_999)}",
            customerId = $"C-{random.Next(1000, 9999)}",
            amount = Math.Round(random.Next(5_000, 500_000) / 100m, 2),
            currency = random.Next(4) == 0 ? "NOK" : "SEK",
            lines = Enumerable.Range(0, random.Next(1, 4)).Select(_ => new { sku = $"SKU-{random.Next(100, 999)}", quantity = random.Next(1, 5) }).ToArray(),
            createdAt = enqueued.AddSeconds(-random.Next(1, 20)),
        });
        return (Encoding.UTF8.GetBytes(json), "application/json");
    }

    private static int StableSeed(string path, SubQueueKind subQueue, long index)
    {
        var hash = 2166136261u;
        foreach (var c in $"{path}|{subQueue}|{index}")
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return (int)hash;
    }

    private sealed record DemoEntity(
        string Name,
        long Active,
        long DeadLetter,
        long TransferDeadLetter = 0,
        long Scheduled = 0,
        bool RequiresSession = false,
        bool RequiresDuplicateDetection = false,
        int MaxDeliveryCount = 10);
}
