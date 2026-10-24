using System.Globalization;

namespace QueueJanne.App;

internal static class Formatting
{
    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes / (1024d * 1024):0.#} MB",
    };

    public static string Duration(TimeSpan? value) => value switch
    {
        null => "–",
        { TotalDays: > 3650 } => "never",
        { TotalDays: >= 1 } v => $"{v.TotalDays:0.#} days",
        { TotalHours: >= 1 } v => $"{v.TotalHours:0.#} h",
        { TotalMinutes: >= 1 } v => $"{v.TotalMinutes:0.#} min",
        { } v => $"{v.TotalSeconds:0.#} s",
    };

    public static string Time(DateTimeOffset? value) =>
        value is null || value.Value.Year >= 9999
            ? "–"
            : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
