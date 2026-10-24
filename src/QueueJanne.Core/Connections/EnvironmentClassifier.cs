namespace QueueJanne.Core.Connections;

public enum EnvironmentKind
{
    Unknown,
    Development,
    Test,
    Production,
}

/// <summary>Maps free-text environment tags such as "preprod-eu" or "production-us" to a kind.</summary>
public static class EnvironmentClassifier
{
    public static EnvironmentKind Classify(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return EnvironmentKind.Unknown;
        }

        var value = tag.Trim().ToLowerInvariant();
        if (ContainsAny(value, "preprod", "pre-prod", "stag", "test", "qa", "uat", "acceptance"))
        {
            return EnvironmentKind.Test;
        }

        if (ContainsAny(value, "prod", "live"))
        {
            return EnvironmentKind.Production;
        }

        if (ContainsAny(value, "dev", "local", "demo", "sandbox"))
        {
            return EnvironmentKind.Development;
        }

        return EnvironmentKind.Unknown;
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.Ordinal));
}
