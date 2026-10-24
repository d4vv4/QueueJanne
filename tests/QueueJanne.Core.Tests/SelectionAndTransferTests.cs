using System.Text;
using QueueJanne.Core.Demo;
using QueueJanne.Core.Messages;
using QueueJanne.Core.ServiceBus;

namespace QueueJanne.Core.Tests;

public class SelectionAndTransferTests
{
    [Fact]
    public async Task Filter_finds_messages_by_reason_property_and_body()
    {
        await using var client = new DemoNamespaceClient();
        var ledger = EntityAddress.Subscription("billing-events", "ledger", SubQueueKind.DeadLetter);

        var byReason = await MessageSelection.FindAsync(client, ledger, new MessageFilter { DeadLetterReason = "ttlexpiredexception" }, 10_000, 5_000, cancellationToken: TestContext.Current.CancellationToken);
        var byProperty = await MessageSelection.FindAsync(client, ledger, new MessageFilter { PropertyName = "region", PropertyValue = "US" }, 10_000, 5_000, cancellationToken: TestContext.Current.CancellationToken);
        var byBody = await MessageSelection.FindAsync(client, ledger, new MessageFilter { BodyContains = "SKU-" }, 10_000, 5_000, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(byReason.ReachedEnd);
        Assert.Equal(1_204, byReason.Scanned);
        Assert.NotEmpty(byReason.Messages);
        Assert.All(byReason.Messages, m => Assert.Equal("TTLExpiredException", m.DeadLetterReason));
        Assert.All(byProperty.Messages, m => Assert.Equal("us", m.ApplicationProperties["region"]));
        Assert.True(byBody.Messages.Count > byReason.Messages.Count);
    }

    [Fact]
    public void Partial_filters_match_part_of_the_text_unless_it_is_quoted()
    {
        var message = new QueueMessage
        {
            Subject = "InvoiceRequested",
            DeadLetterReason = "MaxDeliveryCountExceeded",
            ApplicationProperties = new Dictionary<string, object?> { ["BodyType"] = "OrderCreatedEvent" },
        };

        Assert.True(new MessageFilter { Partial = true, PropertyName = "bodytype", PropertyValue = "created" }.Matches(message));
        Assert.True(new MessageFilter { Partial = true, Subject = "invoice", DeadLetterReason = "maxdelivery" }.Matches(message));
        Assert.False(new MessageFilter { Partial = true, PropertyName = "BodyType", PropertyValue = "\"Created\"" }.Matches(message));
        Assert.True(new MessageFilter { Partial = true, PropertyName = "BodyType", PropertyValue = "\"orderCreatedEvent\"" }.Matches(message));
        Assert.False(new MessageFilter { PropertyName = "BodyType", PropertyValue = "Created" }.Matches(message));
        Assert.True(new MessageFilter { Partial = true }.IsEmpty);
    }

    [Fact]
    public async Task Send_reaches_a_queue_and_every_subscription_of_a_topic()
    {
        await using var client = new DemoNamespaceClient();
        var message = new OutgoingMessage { Body = Encoding.UTF8.GetBytes("""{"hello":"janne"}"""), Subject = "Test", ApplicationProperties = new Dictionary<string, object?> { ["tenant"] = "t-1" } };

        await client.SendAsync("orders", [message], TestContext.Current.CancellationToken);
        await client.SendAsync("billing-events", [message], TestContext.Current.CancellationToken);

        var snapshot = await client.GetSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(13, snapshot.Queues.Single(q => q.Name == "orders").Counts.Active);
        Assert.All(snapshot.Topics.Single(t => t.Topic.Name == "billing-events").Subscriptions, s => Assert.True(s.Counts.Active > 0));
    }

    [Fact]
    public async Task Move_copies_to_the_target_and_removes_the_originals()
    {
        await using var client = new DemoNamespaceClient();
        var deadLetters = EntityAddress.Queue("notifications", SubQueueKind.DeadLetter);
        var chosen = (await MessageSelection.FindAsync(client, deadLetters, new MessageFilter(), 3, 3, cancellationToken: TestContext.Current.CancellationToken)).Messages;

        var result = await MessageTransfer.CopyAsync(client, deadLetters, chosen.Select(m => m.SequenceNumber).ToList(), client, "orders", move: true, newMessageId: false, TestContext.Current.CancellationToken);

        var snapshot = await client.GetSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new TransferResult(3, 3, 0, []), result with { Errors = [] });
        Assert.Equal(38, snapshot.Queues.Single(q => q.Name == "notifications").Counts.DeadLetter);
        Assert.Equal(15, snapshot.Queues.Single(q => q.Name == "orders").Counts.Active);
    }
}
