using QueueJanne.Core.Demo;
using QueueJanne.Core.Messages;
using QueueJanne.Core.ServiceBus;

namespace QueueJanne.Core.Tests;

public class DeleteAndPurgeTests
{
    [Fact]
    public async Task Delete_removes_only_the_chosen_messages()
    {
        await using var client = new DemoNamespaceClient();
        var deadLetters = EntityAddress.Queue("notifications", SubQueueKind.DeadLetter);
        var before = await PeekAsync(client, deadLetters);

        var result = await client.DeleteAsync(deadLetters, new DeleteRequest { SequenceNumbers = [before[0].SequenceNumber, before[5].SequenceNumber, 7] }, cancellationToken: TestContext.Current.CancellationToken);

        var after = await PeekAsync(client, deadLetters);
        Assert.Equal(new MessageOperationResult(2, 1, []), result with { Errors = [] });
        Assert.Equal(before.Count - 2, after.Count);
        Assert.DoesNotContain(after, m => m.SequenceNumber == before[0].SequenceNumber);
    }

    [Fact]
    public async Task Purge_empties_one_sub_queue_and_leaves_the_others()
    {
        await using var client = new DemoNamespaceClient();

        var result = await client.PurgeAsync(EntityAddress.Subscription("billing-events", "ledger", SubQueueKind.DeadLetter), cancellationToken: TestContext.Current.CancellationToken);

        var ledger = (await client.GetSnapshotAsync(TestContext.Current.CancellationToken)).Topics[0].Subscriptions.Single(s => s.Name == "ledger");
        Assert.Equal(1_204, result.Count);
        Assert.Equal(0, ledger.Counts.DeadLetter);
        Assert.Empty(await PeekAsync(client, EntityAddress.Subscription("billing-events", "ledger", SubQueueKind.DeadLetter)));
    }

    [Fact]
    public async Task Deleting_from_an_active_queue_warns_about_locks_and_the_live_delivery_limit()
    {
        await using var client = new DemoNamespaceClient();
        var auditLog = (await client.GetSnapshotAsync(TestContext.Current.CancellationToken)).Queues.Single(q => q.Name == "audit-log");

        var warnings = DeleteAdvice.ForDelete(auditLog, SubQueueKind.Active);

        Assert.Equal(2, warnings.Count);
        Assert.Contains("after 1 deliveries", warnings[1]);
        Assert.Empty(DeleteAdvice.ForDelete(auditLog, SubQueueKind.DeadLetter));
    }

    private static async Task<List<QueueMessage>> PeekAsync(INamespaceClient client, EntityAddress address)
    {
        var messages = new List<QueueMessage>();
        await foreach (var page in client.PeekAsync(address, new PeekOptions { MaxMessages = 5000 }, TestContext.Current.CancellationToken))
        {
            messages.AddRange(page);
        }

        return messages;
    }
}
