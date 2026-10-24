using QueueJanne.Core.Demo;
using QueueJanne.Core.Messages;
using QueueJanne.Core.ServiceBus;

namespace QueueJanne.Core.Tests;

public class ResubmitTests
{
    [Fact]
    public async Task Resubmitting_selected_messages_moves_them_back_to_the_queue()
    {
        await using var client = new DemoNamespaceClient();
        var deadLetters = EntityAddress.Queue("orders", SubQueueKind.DeadLetter);
        var before = await PeekAsync(client, deadLetters);

        var result = await client.ResubmitAsync(
            deadLetters,
            new ResubmitRequest { SequenceNumbers = [before[0].SequenceNumber, 1] },
            cancellationToken: TestContext.Current.CancellationToken);

        var snapshot = await client.GetSnapshotAsync(TestContext.Current.CancellationToken);
        var orders = snapshot.Queues.Single(q => q.Name == "orders");
        var active = await PeekAsync(client, EntityAddress.Queue("orders"));
        Assert.Equal(new ResubmitResult("orders", 1, 1, []), result with { Errors = [] });
        Assert.Equal(2, orders.Counts.DeadLetter);
        Assert.Equal(13, orders.Counts.Active);
        var copy = active[^1];
        Assert.Equal(before[0].MessageId, copy.MessageId);
        Assert.Null(copy.DeadLetterReason);
        Assert.Equal(before[0].Body, copy.Body);
    }

    [Fact]
    public async Task New_message_id_keeps_the_original_in_a_property()
    {
        await using var client = new DemoNamespaceClient();
        var deadLetters = EntityAddress.Queue("invoices", SubQueueKind.DeadLetter);
        var original = (await PeekAsync(client, deadLetters))[0];

        await client.ResubmitAsync(deadLetters, new ResubmitRequest { SequenceNumbers = [original.SequenceNumber], NewMessageId = true }, cancellationToken: TestContext.Current.CancellationToken);

        var copy = (await PeekAsync(client, EntityAddress.Queue("invoices")))[^1];
        Assert.NotEqual(original.MessageId, copy.MessageId);
        Assert.Equal(original.MessageId, copy.ApplicationProperties[ResubmitAdvice.OriginalMessageIdProperty]);
    }

    [Fact]
    public async Task Resubmitting_all_from_a_subscription_reaches_every_subscription_of_the_topic()
    {
        await using var client = new DemoNamespaceClient();

        var result = await client.ResubmitAsync(EntityAddress.Subscription("billing-events", "audit", SubQueueKind.DeadLetter), new ResubmitRequest(), cancellationToken: TestContext.Current.CancellationToken);

        var snapshot = await client.GetSnapshotAsync(TestContext.Current.CancellationToken);
        var subscriptions = snapshot.Topics.Single(t => t.Topic.Name == "billing-events").Subscriptions.ToDictionary(s => s.Name);
        Assert.Equal("billing-events", result.Target);
        Assert.Equal(7, result.Resubmitted);
        Assert.Equal(0, subscriptions["audit"].Counts.DeadLetter);
        Assert.Equal(7, subscriptions["audit"].Counts.Active);
        Assert.Equal(14, subscriptions["invoicing"].Counts.Active);
    }

    [Fact]
    public async Task Advice_warns_about_topic_fan_out_and_duplicate_detection()
    {
        await using var client = new DemoNamespaceClient();
        var snapshot = await client.GetSnapshotAsync(TestContext.Current.CancellationToken);

        var subscriptionWarnings = ResubmitAdvice.Warnings(snapshot, EntityAddress.Subscription("billing-events", "ledger", SubQueueKind.DeadLetter), newMessageId: false);
        var duplicateWarnings = ResubmitAdvice.Warnings(snapshot, EntityAddress.Queue("invoices", SubQueueKind.DeadLetter), newMessageId: false);

        Assert.Contains("invoicing, audit", Assert.Single(subscriptionWarnings));
        Assert.Contains("duplicate detection", Assert.Single(duplicateWarnings));
        Assert.Empty(ResubmitAdvice.Warnings(snapshot, EntityAddress.Queue("invoices", SubQueueKind.DeadLetter), newMessageId: true));
    }

    private static async Task<List<QueueMessage>> PeekAsync(INamespaceClient client, EntityAddress address)
    {
        var messages = new List<QueueMessage>();
        await foreach (var page in client.PeekAsync(address, new PeekOptions(), TestContext.Current.CancellationToken))
        {
            messages.AddRange(page);
        }

        return messages;
    }
}
