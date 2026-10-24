using QueueJanne.Core.Connections;
using QueueJanne.Core.Demo;

namespace QueueJanne.Core.ServiceBus;

public static class NamespaceClientFactory
{
    public static INamespaceClient Create(ConnectionProfile profile, ConnectionStore store, bool allowInteractiveSignIn = true) => profile.Auth switch
    {
        ConnectionAuth.Demo => new DemoNamespaceClient(),
        ConnectionAuth.ConnectionString => ServiceBusNamespaceClient.FromConnectionString(
            store.RevealConnectionString(profile),
            profile.UseWebSockets),
        ConnectionAuth.EntraId => ServiceBusNamespaceClient.FromCredential(
            profile.Namespace ?? throw new InvalidOperationException($"Connection '{profile.Name}' has no namespace."),
            AzureCredentials.Get(allowInteractiveSignIn, profile.TenantId),
            profile.UseWebSockets),
        _ => throw new NotSupportedException($"Unknown sign-in method '{profile.Auth}'."),
    };
}
