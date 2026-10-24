using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;

namespace QueueJanne.Core.ServiceBus;

/// <summary>
/// The developer's own Azure sign-in (Azure CLI, Visual Studio, VS Code, azd, Azure PowerShell), optionally for a specific tenant.
/// The interactive variant falls back to a browser sign-in; the MCP server never prompts.
/// </summary>
public static class AzureCredentials
{
    private static readonly ConcurrentDictionary<(string? TenantId, bool Interactive), TokenCredential> Credentials = new();

    public static TokenCredential Get(bool allowInteractive, string? tenantId = null) =>
        Credentials.GetOrAdd((string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim(), allowInteractive), key => new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            TenantId = key.TenantId,
            ExcludeManagedIdentityCredential = true,
            ExcludeWorkloadIdentityCredential = true,
            ExcludeInteractiveBrowserCredential = !key.Interactive,
        }));
}
