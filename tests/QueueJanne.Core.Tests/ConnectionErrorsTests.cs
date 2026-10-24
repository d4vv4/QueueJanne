using System.Net.Sockets;
using Azure.Identity;
using QueueJanne.Core.ServiceBus;

namespace QueueJanne.Core.Tests;

public class ConnectionErrorsTests
{
    [Fact]
    public void Missing_sign_in_says_to_log_in()
    {
        Assert.Contains("az login", ConnectionErrors.Explain(new CredentialUnavailableException("DefaultAzureCredential failed to retrieve a token")));
    }

    [Fact]
    public void Wrong_tenant_says_to_set_the_tenant()
    {
        var error = new UnauthorizedAccessException("401 Unauthorized. InvalidIssuer: Token issuer is invalid.");

        Assert.Contains("tenant ID", ConnectionErrors.Explain(error));
    }

    [Fact]
    public void Missing_role_names_the_role()
    {
        var error = new AggregateException(new UnauthorizedAccessException("Unauthorized access. 'Manage' claim(s) are required for this operation."));

        Assert.Contains("Azure Service Bus Data Owner", ConnectionErrors.Explain(error));
    }

    [Fact]
    public void Unreachable_host_suggests_checking_name_and_network()
    {
        Assert.Contains("Can't reach", ConnectionErrors.Explain(new SocketException((int)SocketError.HostNotFound)));
    }
}
