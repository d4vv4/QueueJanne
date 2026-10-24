using QueueJanne.Core.Demo;

namespace QueueJanne.Core.Connections;

public enum ConnectionAuth
{
    EntraId,
    ConnectionString,
    Demo,
}

/// <summary>A saved Service Bus namespace. Connection strings are stored protected, never in plain text.</summary>
public sealed record ConnectionProfile
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public ConnectionAuth Auth { get; init; }

    /// <summary>Fully qualified host name, for example <c>my-namespace.servicebus.windows.net</c>.</summary>
    public string? Namespace { get; init; }

    public string? ProtectedConnectionString { get; init; }

    /// <summary>Entra ID tenant that owns the namespace, when it isn't the tenant you're signed in to.</summary>
    public string? TenantId { get; init; }

    /// <summary>Free-text tag such as dev, test or prod. Drives colours and guard rails.</summary>
    public string? Environment { get; init; }

    /// <summary>When set, QueueJanne refuses operations that change messages.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Use AMQP over WebSockets (port 443) on networks that block port 5671.</summary>
    public bool UseWebSockets { get; init; }

    public EnvironmentKind EnvironmentKind => EnvironmentClassifier.Classify(Environment);

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static ConnectionProfile CreateDemo() => new()
    {
        Id = "demo",
        Name = "Demo namespace",
        Auth = ConnectionAuth.Demo,
        Namespace = DemoNamespaceClient.HostName,
        Environment = "demo",
    };

    /// <summary>Accepts <c>name</c>, <c>name.servicebus.windows.net</c> or <c>sb://name.servicebus.windows.net/</c>.</summary>
    public static string NormalizeNamespace(string value)
    {
        var host = value.Trim();
        string[] schemes = ["sb://", "https://", "amqps://"];
        foreach (var scheme in schemes)
        {
            if (host.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                host = host[scheme.Length..];
                break;
            }
        }

        host = host.TrimEnd('/').ToLowerInvariant();
        return host.Contains('.') ? host : $"{host}.servicebus.windows.net";
    }

    public static string? NamespaceFromConnectionString(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals("Endpoint", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeNamespace(pair[1]);
            }
        }

        return null;
    }
}
