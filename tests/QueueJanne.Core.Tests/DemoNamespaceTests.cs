using QueueJanne.Core.Demo;
using QueueJanne.Core.Messages;
using QueueJanne.Core.ServiceBus;

namespace QueueJanne.Core.Tests;

public class DemoNamespaceTests
{
    [Fact]
    public async Task Snapshot_lists_queues_topics_and_dead_letters()
    {
        await using var client = new DemoNamespaceClient();

        var snapshot = await client.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Contains(snapshot.Queues, q => q.Name == "orders");
        Assert.Contains(snapshot.Topics, t => t.Topic.Name == "billing-events" && t.Subscriptions.Any(s => s.Path == "billing-events/subscriptions/ledger"));
        Assert.True(snapshot.TotalDeadLetters > 1000);
    }

    [Fact]
    public async Task Peek_pages_up_to_the_limit_and_continues_from_a_sequence_number()
    {
        await using var client = new DemoNamespaceClient();
        var address = EntityAddress.Subscription("billing-events", "ledger", SubQueueKind.DeadLetter);

        var first = await PeekAsync(client, address, new PeekOptions { MaxMessages = 120, PageSize = 50 });
        var next = await PeekAsync(client, address, new PeekOptions { MaxMessages = 10, FromSequenceNumber = first[^1].SequenceNumber + 1 });

        Assert.Equal(120, first.Count);
        Assert.Equal(first[^1].SequenceNumber + 1, next[0].SequenceNumber);
        Assert.All(first, m => Assert.False(string.IsNullOrEmpty(m.DeadLetterReason)));
    }

    [Fact]
    public async Task Messages_are_repeatable()
    {
        await using var client = new DemoNamespaceClient();
        var address = EntityAddress.Queue("invoices", SubQueueKind.DeadLetter);

        var a = await PeekAsync(client, address, new PeekOptions { MaxMessages = 5 });
        var b = await PeekAsync(client, address, new PeekOptions { MaxMessages = 5 });

        Assert.Equal(a.Select(m => m.MessageId), b.Select(m => m.MessageId));
    }

    [Fact]
    public void Dead_letter_summary_groups_by_reason_largest_first()
    {
        QueueMessage[] messages =
        [
            new() { DeadLetterReason = "MaxDeliveryCountExceeded", DeadLetterErrorDescription = "x", Subject = "A" },
            new() { DeadLetterReason = "MaxDeliveryCountExceeded", DeadLetterErrorDescription = "x", Subject = "B" },
            new() { DeadLetterReason = "TTLExpiredException", Subject = "A" },
        ];

        var groups = DeadLetterSummary.ByReason(messages);

        Assert.Equal(["MaxDeliveryCountExceeded", "TTLExpiredException"], groups.Select(g => g.Reason));
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(new CountedValue("x", 2), groups[0].TopDescriptions[0]);
    }

    private static async Task<List<QueueMessage>> PeekAsync(INamespaceClient client, EntityAddress address, PeekOptions options)
    {
        var messages = new List<QueueMessage>();
        await foreach (var page in client.PeekAsync(address, options, TestContext.Current.CancellationToken))
        {
            messages.AddRange(page);
        }

        return messages;
    }
}
