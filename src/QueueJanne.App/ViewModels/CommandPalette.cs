using QueueJanne.Core.Entities;
using QueueJanne.Core.Messages;

namespace QueueJanne.App.ViewModels;

/// <summary>One thing the command palette (Ctrl+K) can open.</summary>
public sealed record PaletteItem(string Title, string Detail, string SearchText, long DeadLetters, Action Open)
{
    public override string ToString() => Title;
}

/// <summary>Builds and ranks the command palette's items: every queue and subscription in every connection, and the overviews.</summary>
public static class CommandPalette
{
    public const int MaxResults = 60;

    public static IReadOnlyList<PaletteItem> Items(MainWindowViewModel main)
    {
        var items = new List<PaletteItem>
        {
            new("Dead-letter overview", "All connections", "overview dead letters all", 0, () => main.ShowAllDeadLettersCommand.Execute(null)),
        };

        foreach (var connection in main.Connections)
        {
            var name = connection.Profile.Name;
            items.Add(new PaletteItem($"{name} overview", connection.Profile.Namespace ?? string.Empty, $"{name} overview dead letters", 0, () => main.OpenOverview(connection, preview: false)));
            foreach (var entity in connection.Entities.Where(e => e.Entity.Kind != EntityKind.Topic).OrderBy(e => e.Entity.Path, StringComparer.OrdinalIgnoreCase))
            {
                var info = entity.Entity;
                var counts = $"{Formatting.Count(info.Counts.Active)} active";
                items.Add(new PaletteItem(info.Path, $"{name} · {counts}", $"{name} {info.Path}", 0, () => main.Open(entity, SubQueueKind.Active)));
                if (info.Counts.DeadLetter > 0)
                {
                    items.Add(new PaletteItem($"{info.Path} · dead-letter", $"{name} · {Formatting.Count(info.Counts.DeadLetter)} dead letters", $"{name} {info.Path} dead letter dlq", info.Counts.DeadLetter, () => main.Open(entity, SubQueueKind.DeadLetter)));
                }

                if (info.Counts.TransferDeadLetter > 0)
                {
                    items.Add(new PaletteItem($"{info.Path} · transfer DL", $"{name} · {Formatting.Count(info.Counts.TransferDeadLetter)} transfer dead letters", $"{name} {info.Path} transfer dead letter tdlq", info.Counts.TransferDeadLetter, () => main.Open(entity, SubQueueKind.TransferDeadLetter)));
                }
            }
        }

        return items;
    }

    /// <summary>
    /// Items containing every word of the query, best first: titles that start with the query, then titles that contain it,
    /// then the rest; ties go to the most dead letters. An empty query lists the dead-letter queues, biggest first.
    /// </summary>
    public static IReadOnlyList<PaletteItem> Search(IReadOnlyList<PaletteItem> items, string? query)
    {
        var words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return items.Where(i => i.DeadLetters > 0).OrderByDescending(i => i.DeadLetters)
                .Concat(items.Where(i => i.DeadLetters == 0))
                .Take(MaxResults)
                .ToList();
        }

        var whole = query!.Trim();
        return items
            .Where(i => words.All(w => i.SearchText.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Title.StartsWith(whole, StringComparison.OrdinalIgnoreCase) ? 0
                : i.Title.Contains(whole, StringComparison.OrdinalIgnoreCase) ? 1
                : 2)
            .ThenByDescending(i => i.DeadLetters)
            .ThenBy(i => i.Title.Length)
            .Take(MaxResults)
            .ToList();
    }
}
