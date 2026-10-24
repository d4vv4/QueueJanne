namespace QueueJanne.App.ViewModels;

/// <summary>How often auto-refresh reloads the counts of the connected namespaces.</summary>
public sealed record RefreshInterval(int Seconds, string Label)
{
    public const int DefaultSeconds = 15;

    public static IReadOnlyList<RefreshInterval> All { get; } =
    [
        new(5, "5 s"),
        new(10, "10 s"),
        new(15, "15 s"),
        new(30, "30 s"),
        new(60, "1 min"),
        new(300, "5 min"),
    ];

    public static RefreshInterval For(int seconds) =>
        All.FirstOrDefault(i => i.Seconds == seconds) ?? All.First(i => i.Seconds == DefaultSeconds);

    public TimeSpan Interval => TimeSpan.FromSeconds(Seconds);

    /// <summary>"every 15 seconds", "every minute", for tooltips and the status line.</summary>
    public string Every => Seconds switch
    {
        60 => "every minute",
        < 60 => $"every {Seconds} seconds",
        _ => $"every {Seconds / 60} minutes",
    };

    public override string ToString() => Label;
}
