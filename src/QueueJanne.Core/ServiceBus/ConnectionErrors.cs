using System.Net.Sockets;
using Azure;
using Azure.Identity;
using Azure.Messaging.ServiceBus;

namespace QueueJanne.Core.ServiceBus;

/// <summary>Turns the exceptions people hit when connecting into what to do about them.</summary>
public static class ConnectionErrors
{
    public static string Explain(Exception exception)
    {
        var all = Flatten(exception).ToList();
        var text = string.Join(" ", all.Select(e => e.Message));

        if (all.Any(e => e is CredentialUnavailableException))
        {
            return "Couldn't find an Azure sign-in. Run 'az login' (or sign in to Visual Studio), then try again.";
        }

        if (text.Contains("InvalidIssuer", StringComparison.OrdinalIgnoreCase)
            || text.Contains("AADSTS50020", StringComparison.Ordinal)
            || text.Contains("AADSTS700016", StringComparison.Ordinal)
            || text.Contains("tenant", StringComparison.OrdinalIgnoreCase) && text.Contains("not", StringComparison.OrdinalIgnoreCase) && all.Any(e => e is AuthenticationFailedException))
        {
            return "The namespace belongs to a different Entra ID tenant than the one you're signed in to. "
                + "Set the tenant ID on the connection (you'll find it on the namespace's Microsoft Entra ID page in the Azure portal).";
        }

        if (all.Any(e => e is AuthenticationFailedException))
        {
            return $"Azure sign-in failed: {FirstLine(all.First(e => e is AuthenticationFailedException).Message)} Try 'az login', or set the tenant ID on the connection.";
        }

        if (all.Any(e => e is UnauthorizedAccessException || e is RequestFailedException { Status: 401 or 403 })
            || all.Any(e => e is ServiceBusException { Reason: ServiceBusFailureReason.GeneralError } && e.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)))
        {
            return "Signed in, but your account isn't allowed to read this namespace. Listing queues with counts needs the "
                + "'Azure Service Bus Data Owner' role on the namespace (peeking needs 'Data Receiver'). New role assignments can take a few minutes. "
                + "If the namespace is in another tenant, set the tenant ID on the connection.";
        }

        if (all.Any(e => e is SocketException) || text.Contains("No such host", StringComparison.OrdinalIgnoreCase) || text.Contains("name or service not known", StringComparison.OrdinalIgnoreCase))
        {
            return "Can't reach the namespace. Check its name and your network or VPN. If port 5671 is blocked, turn on AMQP over WebSockets.";
        }

        if (all.Any(e => e is ServiceBusException { Reason: ServiceBusFailureReason.MessagingEntityNotFound }))
        {
            return "The queue or subscription no longer exists. Refresh the counts.";
        }

        return FirstLine(exception.Message);
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
                {
                    yield return inner;
                }
            }

            yield return current;
        }
    }

    private static string FirstLine(string message) => message.Split('\n')[0].Trim();
}
