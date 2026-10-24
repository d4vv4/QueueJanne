using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using QueueJanne.Core.Entities;

namespace QueueJanne.App.ViewModels;

/// <summary>
/// Every queue and subscription with dead letters, biggest first, for one connection or for all of them.
/// It's rebuilt whenever a connection's counts refresh, and shows how much each entity changed since the last refresh.
/// </summary>
public sealed partial class OverviewViewModel : DocumentViewModel
{
    private readonly Action<ConnectionNodeViewModel, EntityInfo> _openDeadLetters;
    private Dictionary<string, long> _previous = new(StringComparer.Ordinal);

    [ObservableProperty]
    private string _summary = "Waiting for counts…";

    [ObservableProperty]
    private OverviewRow? _selectedRow;

    public OverviewViewModel(ConnectionNodeViewModel? connection, Action<DocumentViewModel> close, Action<ConnectionNodeViewModel, EntityInfo> openDeadLetters)
        : base(close)
    {
        Connection = connection;
        _openDeadLetters = openDeadLetters;
    }

    /// <summary>The connection this overview covers, or null for all connections.</summary>
    public ConnectionNodeViewModel? Connection { get; }

    public ObservableCollection<OverviewRow> Rows { get; } = [];

    public bool ShowConnectionColumn => Connection is null;

    public override string Header => Connection is null ? "Dead letters · all" : $"{Connection.Profile.Name} · dead letters";

    public override string ToolTip => (Connection is null
            ? "Every queue and subscription with dead letters, in every connection."
            : $"Every queue and subscription with dead letters in {Connection.Profile.Name} ({Connection.Profile.Namespace}).")
        + " Double-click a row to open its dead-letter queue."
        + (IsPreview ? "\nPreview tab: the next single click replaces it. Double-click to keep it open." : string.Empty);

    public override MaterialIconKind Icon => MaterialIconKind.ViewDashboardOutline;

    public bool Covers(ConnectionNodeViewModel connection) => Connection is null || Connection == connection;

    public void Refresh(IEnumerable<ConnectionNodeViewModel> connections)
    {
        var scope = connections.Where(Covers).ToList();
        var rows = new List<OverviewRow>();
        var current = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var connection in scope)
        {
            if (connection.Snapshot is not { } snapshot)
            {
                continue;
            }

            foreach (var entity in snapshot.ReadableEntities)
            {
                var key = $"{connection.Profile.Id}|{entity.Path}";
                var dead = entity.Counts.DeadLetter + entity.Counts.TransferDeadLetter;
                current[key] = dead;
                long? change = _previous.TryGetValue(key, out var before) && before != dead ? dead - before : null;
                if (dead > 0 || change is not null)
                {
                    rows.Add(new OverviewRow(connection, entity, key, change));
                }
            }
        }

        _previous = current;
        rows.Sort((a, b) => b.DeadLetters != a.DeadLetters ? b.DeadLetters.CompareTo(a.DeadLetters) : string.CompareOrdinal(a.Path, b.Path));

        var selectedKey = SelectedRow?.Key;
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        SelectedRow = Rows.FirstOrDefault(r => r.Key == selectedKey);

        var total = rows.Sum(r => r.DeadLetters);
        var withDeadLetters = rows.Count(r => r.DeadLetters > 0);
        var connected = scope.Where(c => c.IsConnected).ToList();
        var loading = connected.Count(c => c.Snapshot is null && c.Error is null);
        var failed = connected.Where(c => c.Error is not null).Select(c => c.Profile.Name).ToList();
        var notConnected = scope.Count - connected.Count;
        Summary = (scope.Count == 0
                ? "No connections yet."
                : connected.Count == 0
                    ? "Not connected yet."
                    : total == 0
                        ? "No dead letters."
                        : $"{Formatting.Count(total)} dead letters in {Formatting.Count(withDeadLetters)} queues and subscriptions"
                          + (Connection is null ? $" across {connected.Count} connected namespace(s)." : "."))
            + (loading > 0 ? $" Still loading {loading} connection(s)." : string.Empty)
            + (failed.Count > 0 ? $" Couldn't reach {string.Join(", ", failed)}." : string.Empty)
            + (notConnected > 0 ? $" {notConnected} connection(s) not connected: expand them in Servers to include them." : string.Empty);
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedRow is { } row)
        {
            _openDeadLetters(row.Connection, row.Entity);
        }
    }
}

public sealed class OverviewRow(ConnectionNodeViewModel connection, EntityInfo entity, string key, long? change)
{
    public ConnectionNodeViewModel Connection { get; } = connection;

    public EntityInfo Entity { get; } = entity;

    public string Key { get; } = key;

    public string ConnectionName => Connection.Profile.Name;

    public string Path => Entity.Path;

    public long DeadLetters => Entity.Counts.DeadLetter + Entity.Counts.TransferDeadLetter;

    public string DeadLettersText => Formatting.Count(Entity.Counts.DeadLetter);

    public string TransferText => Entity.Counts.TransferDeadLetter == 0 ? string.Empty : Formatting.Count(Entity.Counts.TransferDeadLetter);

    public string ActiveText => Formatting.Count(Entity.Counts.Active);

    public string MaxDeliveriesText => Entity.Settings.MaxDeliveryCount?.ToString() ?? "–";

    public long? Change { get; } = change;

    public string ChangeText => Change switch
    {
        > 0 => $"▲ {Formatting.Count(Change.Value)}",
        < 0 => $"▼ {Formatting.Count(-Change.Value)}",
        _ => string.Empty,
    };

    public bool IsRising => Change > 0;

    public bool IsFalling => Change < 0;

    public override string ToString() => $"{Path} on {ConnectionName}";
}
