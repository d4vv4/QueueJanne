using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace QueueJanne.Core.Connections;

public interface ISecretProtector
{
    string Protect(string secret);

    string Unprotect(string protectedSecret);
}

public static class SecretProtector
{
    public static ISecretProtector CreateDefault() =>
        OperatingSystem.IsWindows() ? new DpapiSecretProtector() : new FileSecretProtector();
}

/// <summary>Encrypts with Windows DPAPI, so only the same Windows user can read the secret.</summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "QueueJanne"u8.ToArray();

    public string Protect(string secret) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string protectedSecret)
    {
        if (!protectedSecret.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return new FileSecretProtector().Unprotect(protectedSecret);
        }

        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>macOS and Linux fallback: the value is only encoded, and the store file is restricted to the current user.</summary>
internal sealed class FileSecretProtector : ISecretProtector
{
    private const string Prefix = "file:";

    public string Protect(string secret) => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));

    public string Unprotect(string protectedSecret)
    {
        if (!protectedSecret.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("This connection string was saved by another user or machine. Add the connection again.");
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(protectedSecret[Prefix.Length..]));
    }
}
