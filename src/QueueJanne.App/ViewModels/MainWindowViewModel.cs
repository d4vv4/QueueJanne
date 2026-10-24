using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QueueJanne.App.Services;
using QueueJanne.Core.Connections;
using QueueJanne.Core.Entities;
using QueueJanne.Core.Messages;
using QueueJanne.Core.ServiceBus;

namespace QueueJanne.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ConnectionStore _store;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _autoRefreshTimer;

    [ObservableProperty]
    private TreeNodeViewModel? _selectedNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedEntityTab))]
    private DocumentViewModel? _selectedTab;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string? _treeFilter;

    [ObservableProperty]
    private bool _autoRefresh;

    [ObservableProperty]
    private bool _isRefreshing;

    public MainWindowViewModel(ConnectionStore store, IDialogService dialogs)
    {
        _store = store;
        _dialogs = dialogs;
        _autoRefreshTimer = new DispatcherTimer { Interval = RefreshEvery.Interval };
        _autoRefreshTimer.Tick += async (_, _) => await RefreshConnectionsAsync();
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTabs));
        Connections.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasConnections));

        // Saved connections are listed but not connected: expanding one connects it, so namespaces nobody looks at get no load.
        foreach (var profile in store.Load())
        {
            AddConnectionNode(profile);
        }

        Updates = new UpdateViewModel(dialogs, text => StatusText = text, () => ExitCommand.Execute(null));
        Dispatcher.UIThread.Post(Updates.StartChecking, DispatcherPriority.Background);

        StatusText = Connections.Count == 0
            ? "Add a connection to get started, or open the demo namespace from the File menu."
            : "Expand a connection to connect to it.";
    }

    public ObservableCollection<ConnectionNodeViewModel> Connections { get; } = [];

    public UpdateViewModel Updates { get; }

    public ObservableCollection<DocumentViewModel> Tabs { get; } = [];

    public EntityTabViewModel? SelectedEntityTab => SelectedTab as EntityTabViewModel;

    private IEnumerable<EntityTabViewModel> EntityTabs => Tabs.OfType<EntityTabViewModel>();

    public bool HasTabs => Tabs.Count > 0;

    public bool HasConnections => Connections.Count > 0;

    /// <summary>Double-click or Enter in the tree opens queues, subscriptions, their dead-letter queues and connection overviews in tabs that stay.</summary>
    public void ActivateSelectedNode()
    {
        switch (SelectedNode)
        {
            case ConnectionNodeViewModel connection:
                OpenOverview(connection, preview: false);
                break;
            case SubQueueNodeViewModel subQueue:
                Open(subQueue.Owner, subQueue.SubQueue);
                break;
            case EntityNodeViewModel { Entity.Kind: not EntityKind.Topic } entity:
                Open(entity, SubQueueKind.Active);
                break;
        }
    }

    public ViewSettings Settings => ViewSettings.Current;

    /// <summary>
    /// Opens a tab, or selects the one already open. A preview tab is the single reusable tab a single click opens;
    /// the next preview replaces it, and opening the same entity without preview keeps it.
    /// </summary>
    internal EntityTabViewModel Open(EntityNodeViewModel entity, SubQueueKind subQueue, bool preview = false)
    {
        var existing = EntityTabs.FirstOrDefault(t => t.Connection == entity.Connection && t.Entity.Path == entity.Entity.Path && t.SubQueue == subQueue);
        if (existing is not null)
        {
            if (!preview)
            {
                existing.IsPreview = false;
            }

            SelectedTab = existing;
            return existing;
        }

        var tab = new EntityTabViewModel(entity.Connection, entity.Entity, subQueue, _dialogs, CloseTab) { AllConnections = Connections };
        Show(tab, preview);
        _ = tab.LoadAsync();
        UiLagMonitor.NoteUntilIdle($"open {tab.Header}");
        return tab;
    }

    /// <summary>The dead-letter overview for one connection, or for all of them when <paramref name="connection"/> is null.</summary>
    internal OverviewViewModel OpenOverview(ConnectionNodeViewModel? connection, bool preview)
    {
        // A connection's own overview needs its counts, so opening it connects; the all-connections overview covers
        // only the ones already connected.
        if (connection is { IsConnected: false })
        {
            connection.IsExpanded = true;
        }

        var existing = Tabs.OfType<OverviewViewModel>().FirstOrDefault(t => t.Connection == connection);
        if (existing is not null)
        {
            if (!preview)
            {
                existing.IsPreview = false;
            }

            SelectedTab = existing;
            return existing;
        }

        var overview = new OverviewViewModel(connection, CloseTab, OpenDeadLetters);
        overview.Refresh(Connections);
        Show(overview, preview);
        return overview;
    }

    // A single click on Messages, Dead-letter or Transfer dead-letter previews that sub-queue. Clicking a connection, queue
    // or subscription only opens or closes its row; a connection's overview has its own button, which works however often
    // it's clicked.
    partial void OnSelectedNodeChanged(TreeNodeViewModel? value)
    {
        if (value is SubQueueNodeViewModel subQueue)
        {
            Open(subQueue.Owner, subQueue.SubQueue, preview: true);
        }
    }

    private void Show(DocumentViewModel tab, bool preview)
    {
        tab.IsPreview = preview;
        var replaced = preview ? Tabs.FirstOrDefault(t => t.IsPreview) : null;
        if (replaced is not null)
        {
            // Select the new tab before removing the old one: a moment with no selected tab would throw the shown view away,
            // and building a new one is the slow part of switching tabs.
            Tabs.Insert(Tabs.IndexOf(replaced), tab);
            SelectedTab = tab;
            Tabs.Remove(replaced);
            replaced.Dispose();
        }
        else
        {
            Tabs.Add(tab);
            SelectedTab = tab;
        }
    }

    private void OpenDeadLetters(ConnectionNodeViewModel connection, EntityInfo entity)
    {
        if (connection.FindEntity(entity.Path) is { } node)
        {
            Open(node, entity.Counts.DeadLetter == 0 && entity.Counts.TransferDeadLetter > 0 ? SubQueueKind.TransferDeadLetter : SubQueueKind.DeadLetter);
        }
    }

    private void RefreshOverviews()
    {
        foreach (var overview in Tabs.OfType<OverviewViewModel>())
        {
            overview.Refresh(Connections);
        }
    }

    [RelayCommand]
    private void ShowAllDeadLetters() => OpenOverview(null, preview: false);

    /// <summary>Sends a new message, addressed to the queue or topic selected in the tree (a subscription's topic).</summary>
    [RelayCommand]
    private async Task SendMessageAsync()
    {
        var (connection, target) = SelectedNode switch
        {
            SubQueueNodeViewModel node => (node.Connection, SendTarget(node.Owner.Entity)),
            EntityNodeViewModel node => (node.Connection, SendTarget(node.Entity)),
            { } node => (node.Connection, null),
            null => (Connections.FirstOrDefault(), null),
        };
        var dialog = SendMessageViewModel.New(Connections, connection, target);
        if (dialog.Connections.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Send message", "No connection allows changes. Right-click a connection and choose Read-only on/off to allow them.");
            return;
        }

        if (await _dialogs.ShowSendAsync(dialog) is { } messages)
        {
            StatusText = await EntityOperations.SendAsync(dialog, messages, _dialogs);
            UpdateTabs();
        }

        static string SendTarget(EntityInfo entity) => entity.Kind == EntityKind.Subscription ? entity.TopicName! : entity.Name;
    }

    [RelayCommand]
    private Task OpenJournalAsync() => _dialogs.OpenFolderAsync(EntityOperations.Journal.Folder);

    [RelayCommand]
    private async Task GoToAsync()
    {
        if (await _dialogs.ShowPaletteAsync(CommandPalette.Items(this)) is { } item)
        {
            item.Open();
        }
    }

    partial void OnTreeFilterChanged(string? value)
    {
        foreach (var connection in Connections)
        {
            connection.SetFilter(value);
        }
    }

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value)
        {
            _autoRefreshTimer.Start();
        }
        else
        {
            _autoRefreshTimer.Stop();
        }

        StatusText = value ? $"Auto-refresh is on: counts of connected namespaces update {RefreshEvery.Every}." : "Auto-refresh is off.";
    }

    public IReadOnlyList<RefreshInterval> RefreshIntervals => RefreshInterval.All;

    /// <summary>The auto-refresh interval, kept in the settings.</summary>
    public RefreshInterval RefreshEvery
    {
        get => RefreshInterval.For(Settings.AutoRefreshSeconds);
        set
        {
            if (value is null || value.Seconds == Settings.AutoRefreshSeconds)
            {
                return;
            }

            Settings.AutoRefreshSeconds = value.Seconds;
            _autoRefreshTimer.Interval = value.Interval;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoRefreshTip));
            if (AutoRefresh)
            {
                StatusText = $"Auto-refresh: counts of connected namespaces update {value.Every}.";
            }
        }
    }

    public string AutoRefreshTip => $"Refresh the counts of connected namespaces {RefreshEvery.Every}";

    [RelayCommand]
    private async Task AddConnectionAsync()
    {
        var draft = await _dialogs.ShowAddConnectionAsync();
        if (draft is null)
        {
            return;
        }

        var profile = _store.Save(new ConnectionProfile
        {
            Id = ConnectionProfile.NewId(),
            Name = draft.Name,
            Auth = draft.Auth,
            Namespace = draft.Namespace,
            ProtectedConnectionString = draft.ConnectionString is { } secret ? _store.Protect(secret) : null,
            Environment = draft.Environment,
            ReadOnly = draft.ReadOnly,
            UseWebSockets = draft.UseWebSockets,
            TenantId = draft.TenantId,
        });

        var node = AddConnectionNode(profile);
        SelectedNode = node;
        StatusText = $"Connecting to {profile.Namespace}…";
        var loading = node.LoadAsync();
        node.IsExpanded = true;
        OpenOverview(node, preview: true);
        await loading;
        StatusText = node.Error is null ? $"Connected to {profile.Namespace}." : $"Couldn't connect to {profile.Namespace}: {node.Error}";
    }

    [RelayCommand]
    private async Task AddDemoConnectionAsync()
    {
        var existing = Connections.FirstOrDefault(c => c.Profile.Auth == ConnectionAuth.Demo);
        if (existing is not null)
        {
            SelectedNode = existing;
            return;
        }

        var node = AddConnectionNode(_store.Save(ConnectionProfile.CreateDemo()));
        SelectedNode = node;
        var loading = node.LoadAsync();
        node.IsExpanded = true;
        OpenOverview(node, preview: true);
        await loading;
        StatusText = "Opened the demo namespace. Its data is made up and nothing leaves your machine.";
    }

    /// <summary>Connects the selected connection, or disconnects it: its tabs close and nothing more is sent to Azure.</summary>
    [RelayCommand]
    private async Task ToggleConnectedAsync()
    {
        if (SelectedNode?.Connection is not { } connection)
        {
            StatusText = "Select a connection first.";
            return;
        }

        if (!connection.IsConnected)
        {
            connection.IsExpanded = true;
            await connection.LoadAsync();
            StatusText = connection.Error is null ? $"Connected to {connection.Profile.Name}." : $"Couldn't connect to {connection.Profile.Name}: {connection.Error}";
            return;
        }

        CloseTabsOf(connection);
        SelectedNode = connection;
        await connection.DisconnectAsync();
        StatusText = $"Disconnected from {connection.Profile.Name}.";
    }

    private void CloseTabsOf(ConnectionNodeViewModel connection)
    {
        foreach (var tab in Tabs.Where(t => t is EntityTabViewModel e && e.Connection == connection || t is OverviewViewModel o && o.Connection == connection).ToList())
        {
            CloseTab(tab);
        }
    }

    [RelayCommand]
    private async Task RemoveConnectionAsync()
    {
        if (SelectedNode?.Connection is not { } connection)
        {
            StatusText = "Select a connection first.";
            return;
        }

        if (!await _dialogs.ConfirmAsync("Remove connection", $"Remove '{connection.Profile.Name}' from QueueJanne? Nothing changes in Azure."))
        {
            return;
        }

        CloseTabsOf(connection);
        _store.Remove(connection.Profile.Id);
        Connections.Remove(connection);
        SelectedNode = null;
        RefreshOverviews();
        await connection.DisposeAsync();
        StatusText = $"Removed {connection.Profile.Name}.";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var tabRefresh = SelectedEntityTab?.LoadAsync() ?? Task.CompletedTask;
        await Task.WhenAll(RefreshConnectionsAsync(), tabRefresh);
    }

    [RelayCommand]
    private void OpenActive() => OpenSelected(SubQueueKind.Active);

    [RelayCommand]
    private void OpenDeadLetter() => OpenSelected(SubQueueKind.DeadLetter);

    [RelayCommand]
    private void OpenTransferDeadLetter() => OpenSelected(SubQueueKind.TransferDeadLetter);

    [RelayCommand]
    private void CloseSelectedTab()
    {
        if (SelectedTab is { } tab)
        {
            CloseTab(tab);
        }
    }

    [RelayCommand]
    private async Task CopyEntityNameAsync()
    {
        var text = SelectedNode switch
        {
            EntityNodeViewModel entity => entity.Entity.Path,
            SubQueueNodeViewModel subQueue => subQueue.Owner.Entity.Path,
            ConnectionNodeViewModel connection => connection.Profile.Namespace,
            _ => null,
        };
        if (text is not null)
        {
            await _dialogs.CopyToClipboardAsync(text);
            StatusText = $"Copied {text}";
        }
    }

    [RelayCommand]
    private Task CopyBodyAsync() => SelectedEntityTab?.CopyBodyCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private Task CopyMessageIdAsync() => SelectedEntityTab?.CopyMessageIdCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private Task CopyAsJsonAsync() => SelectedEntityTab?.CopyAsJsonCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ExportSelectedAsync() => SelectedEntityTab?.ExportSelectedCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ResubmitSelectedAsync() => SelectedEntityTab?.ResubmitSelectedCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ResubmitAllAsync() => SelectedEntityTab?.ResubmitAllCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private Task EditAndResendAsync() => SelectedEntityTab?.EditAndResendCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private Task CopyToAsync() => SelectedEntityTab?.CopyToCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private Task DeleteSelectedAsync() => SelectedEntityTab?.DeleteSelectedCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    /// <summary>Purges the queue or subscription selected in the tree, with its dead-letter queue preselected when that node is selected.</summary>
    [RelayCommand]
    private async Task PurgeSelectedNodeAsync()
    {
        var (entity, subQueue) = SelectedNode switch
        {
            SubQueueNodeViewModel node => (node.Owner, node.SubQueue),
            EntityNodeViewModel node => (node, SubQueueKind.Active),
            _ => (null, SubQueueKind.Active),
        };
        if (entity is null || entity.Entity.Kind == EntityKind.Topic)
        {
            StatusText = "Select a queue or subscription to purge.";
            return;
        }

        var finished = false;
        var outcome = await EntityOperations.PurgeAsync(entity.Connection, entity.Entity, subQueue, _dialogs, new Progress<string>(text =>
        {
            if (!finished)
            {
                StatusText = text;
            }
        }));
        finished = true;
        if (outcome is null)
        {
            return;
        }

        foreach (var tab in EntityTabs.Where(t => t.Connection == entity.Connection && t.Entity.Path == entity.Entity.Path).ToList())
        {
            if (entity.Connection.FindEntity(tab.Entity.Path) is { } node)
            {
                tab.UpdateEntity(node.Entity);
            }

            _ = tab.LoadAsync();
        }

        StatusText = outcome.Summary;
    }

    [RelayCommand]
    private async Task ToggleReadOnlyAsync()
    {
        if (SelectedNode?.Connection is not { } connection)
        {
            StatusText = "Select a connection first.";
            return;
        }

        var profile = connection.Profile;
        if (profile.ReadOnly
            && profile.EnvironmentKind == EnvironmentKind.Production
            && !await _dialogs.ConfirmAsync("Allow changes on production", $"Turn off read-only for '{profile.Name}'? QueueJanne will then let you resubmit messages on this production namespace."))
        {
            return;
        }

        connection.UpdateProfile(_store.Save(profile with { ReadOnly = !profile.ReadOnly }));
        StatusText = connection.Profile.ReadOnly ? $"{profile.Name} is read-only." : $"{profile.Name} now allows changes.";
    }

    [RelayCommand]
    private void SetTheme(string? variant) => Application.Current!.RequestedThemeVariant = variant switch
    {
        "Light" => ThemeVariant.Light,
        "Dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    [RelayCommand]
    private void Exit() => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    [RelayCommand]
    private Task AboutAsync()
    {
        var version = typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "dev";
        return _dialogs.ShowMessageAsync(
            "About QueueJanne",
            $"QueueJanne {version}\nA fast explorer for Azure Service Bus.\n\nYour connections are saved in {_store.FilePath}.");
    }

    private void OpenSelected(SubQueueKind subQueue)
    {
        var entity = SelectedNode switch
        {
            SubQueueNodeViewModel node => node.Owner,
            EntityNodeViewModel node => node,
            _ => null,
        };
        if (entity is null || entity.Entity.Kind == EntityKind.Topic)
        {
            StatusText = "Select a queue or subscription first.";
            return;
        }

        Open(entity, subQueue);
    }

    private void CloseTab(DocumentViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.RemoveAt(index);
        tab.Dispose();
        if (Tabs.Count > 0 && (SelectedTab is null || !Tabs.Contains(SelectedTab)))
        {
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }

    private ConnectionNodeViewModel AddConnectionNode(ConnectionProfile profile)
    {
        var node = new ConnectionNodeViewModel(profile, () => NamespaceClientFactory.Create(profile, _store));
        node.SetFilter(TreeFilter);
        node.Loaded += (_, _) => RefreshOverviews();
        Connections.Add(node);
        return node;
    }

    private void UpdateTabs()
    {
        foreach (var tab in EntityTabs)
        {
            if (tab.Connection.FindEntity(tab.Entity.Path) is { } node)
            {
                tab.UpdateEntity(node.Entity);
            }
        }
    }

    private async Task RefreshConnectionsAsync()
    {
        IsRefreshing = true;
        try
        {
            await Task.WhenAll(Connections.Where(c => c.IsConnected).Select(c => c.LoadAsync()));
        }
        finally
        {
            IsRefreshing = false;
        }

        UpdateTabs();
        var failed = Connections.FirstOrDefault(c => c.Error is not null);
        StatusText = failed is null
            ? $"Counts refreshed at {DateTime.Now:HH:mm:ss}."
            : $"{failed.Profile.Name}: {failed.Error}";
    }
}
