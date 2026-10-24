using QueueJanne.Core.Demo;
using QueueJanne.Core.Messages;

namespace QueueJanne.Core.Tests;

public sealed class OperationJournalTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "qj-journal-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task Backs_up_the_chosen_messages_before_deleting_them()
    {
        await using var client = new DemoNamespaceClient();
        var journal = new OperationJournal(_folder);
        var address = EntityAddress.Queue("notifications", SubQueueKind.DeadLetter);
        long[] chosen = [48_003, 48_010];

        var result = await journal.RecordAsync(
            new JournalEntry { Source = "test", Operation = "delete", Connection = "Demo" },
            client,
            address,
            known: null,
            chosen,
            () => client.DeleteAsync(address, new DeleteRequest { SequenceNumbers = chosen }, cancellationToken: TestContext.Current.CancellationToken),
            r => ($"Deleted {r.Count}", r.Errors),
            TestContext.Current.CancellationToken);

        var entry = OperationJournal.Load(Directory.GetFiles(_folder, "*.json", SearchOption.AllDirectories).Single());
        Assert.Equal(2, result.Count);
        Assert.Equal("Deleted 2", entry.Outcome);
        Assert.Equal("notifications", entry.Entity);
        Assert.Equal(SubQueueKind.DeadLetter, entry.SubQueue);
        Assert.Equal(2, entry.Requested);
        Assert.True(entry.BackupComplete);
        Assert.Equal(chosen, entry.Messages.Select(m => m.SequenceNumber));
        Assert.Equal(Environment.UserName, entry.User);
    }

    [Fact]
    public async Task Records_a_failure_and_rethrows()
    {
        await using var client = new DemoNamespaceClient();
        var journal = new OperationJournal(_folder);
        var address = EntityAddress.Queue("orders", SubQueueKind.DeadLetter);

        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RecordAsync<int>(
            new JournalEntry { Source = "test", Operation = "purge" },
            client,
            address,
            known: null,
            sequenceNumbers: null,
            () => throw new InvalidOperationException("lost the connection"),
            _ => ("never", []),
            TestContext.Current.CancellationToken));

        var entry = OperationJournal.Load(Directory.GetFiles(_folder, "*.json", SearchOption.AllDirectories).Single());
        Assert.Equal("Failed: lost the connection", entry.Outcome);
        Assert.Equal(3, entry.Messages.Count);
        Assert.Null(entry.Requested);
    }

    [Fact]
    public async Task Says_when_a_sub_queue_was_too_big_to_back_up_completely()
    {
        await using var client = new DemoNamespaceClient();

        var (messages, complete) = await OperationJournal.CollectAsync(client, EntityAddress.Queue("audit-log", SubQueueKind.Active), null, TestContext.Current.CancellationToken);

        Assert.Equal(OperationJournal.MaxBackup, messages.Count);
        Assert.False(complete);
    }
}
