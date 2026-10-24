using QueueJanne.Core.Connections;
using QueueJanne.Core.Entities;
using QueueJanne.Core.Messages;

namespace QueueJanne.Core.Tests;

public class AddressingTests
{
    [Theory]
    [InlineData("orders", EntityKind.Queue, "orders", null, SubQueueKind.Active)]
    [InlineData("billing/ledger", EntityKind.Subscription, "ledger", "billing", SubQueueKind.Active)]
    [InlineData("billing/Subscriptions/ledger", EntityKind.Subscription, "ledger", "billing", SubQueueKind.Active)]
    [InlineData("orders/$deadletterqueue", EntityKind.Queue, "orders", null, SubQueueKind.DeadLetter)]
    [InlineData("billing/subscriptions/ledger/$DeadLetterQueue", EntityKind.Subscription, "ledger", "billing", SubQueueKind.DeadLetter)]
    [InlineData("orders/$transferdeadletterqueue", EntityKind.Queue, "orders", null, SubQueueKind.TransferDeadLetter)]
    public void Parses_entity_paths(string path, EntityKind kind, string name, string? topic, SubQueueKind subQueue)
    {
        var address = EntityAddress.Parse(path);

        Assert.Equal(kind, address.Kind);
        Assert.Equal(name, address.Name);
        Assert.Equal(topic, address.TopicName);
        Assert.Equal(subQueue, address.SubQueue);
    }

    [Fact]
    public void Rejects_paths_with_too_many_parts()
    {
        Assert.Throws<FormatException>(() => EntityAddress.Parse("a/b/c/d"));
    }

    [Theory]
    [InlineData("my-ns", "my-ns.servicebus.windows.net")]
    [InlineData("My-NS.servicebus.windows.net", "my-ns.servicebus.windows.net")]
    [InlineData("sb://my-ns.servicebus.windows.net/", "my-ns.servicebus.windows.net")]
    public void Normalizes_namespace_names(string input, string expected)
    {
        Assert.Equal(expected, ConnectionProfile.NormalizeNamespace(input));
    }

    [Fact]
    public void Reads_namespace_from_connection_string()
    {
        const string connectionString = "Endpoint=sb://my-ns.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=abc=";

        Assert.Equal("my-ns.servicebus.windows.net", ConnectionProfile.NamespaceFromConnectionString(connectionString));
    }

    [Theory]
    [InlineData("production-eu", EnvironmentKind.Production)]
    [InlineData("preprod-eu", EnvironmentKind.Test)]
    [InlineData("Stagingwesteurope", EnvironmentKind.Test)]
    [InlineData("dev", EnvironmentKind.Development)]
    [InlineData(null, EnvironmentKind.Unknown)]
    [InlineData("customer-a", EnvironmentKind.Unknown)]
    public void Classifies_environment_tags(string? tag, EnvironmentKind expected)
    {
        Assert.Equal(expected, EnvironmentClassifier.Classify(tag));
    }
}
