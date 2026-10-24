using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;

namespace QueueJanne.App.Services;

/// <summary>
/// Logs moments when the UI thread was blocked, for finding stutters. Off unless QUEUEJANNE_DIAGNOSTICS names a log file.
/// Each line is the time and how many milliseconds the UI thread couldn't respond.
/// </summary>
internal static class UiLagMonitor
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(10);
    private const double ReportAboveMilliseconds = 50;

    private static StreamWriter? _writer;

    /// <summary>Writes a marker line, so freezes can be matched to what the app was doing.</summary>
    public static void Note(string text) => _writer?.WriteLine($"# {DateTime.Now:HH:mm:ss.fff} {text}");

    /// <summary>Notes how long until the UI thread is idle again, which includes layout and the first render.</summary>
    public static void NoteUntilIdle(string text)
    {
        if (_writer is null)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        Dispatcher.UIThread.Post(() => Note($"{text}: idle after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms"), DispatcherPriority.ApplicationIdle);
    }

    public static void StartIfRequested()
    {
        var path = Environment.GetEnvironmentVariable("QUEUEJANNE_DIAGNOSTICS");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var writer = _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        writer.WriteLine($"# QueueJanne UI lag log, started {DateTime.Now:O}");
        var last = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(Interval, DispatcherPriority.Background, (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            var lag = Stopwatch.GetElapsedTime(last, now).TotalMilliseconds - Interval.TotalMilliseconds;
            last = now;
            if (lag > ReportAboveMilliseconds)
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.fff} {lag:0}"));
            }
        });
        timer.Start();
    }
}
