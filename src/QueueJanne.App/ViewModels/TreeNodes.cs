using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;
using QueueJanne.Core.Connections;
using QueueJanne.Core.Entities;
using QueueJanne.Core.Messages;
using QueueJanne.Core.ServiceBus;

namespace QueueJanne.App.ViewModels;

/// <summary>A row in the Servers tree. <see cref="VisibleChildren"/> is <see cref="Children"/> after the filter.</summary>
public abstract partial class TreeNodeViewModel : ObservableObject
{
    private static readonly TimeSpan PulseDuration = TimeSpan.FromMilliseconds(1600);

    private string? _shownCounts;
    private long _shownDeadLetters;
    private int _pulseVersion;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>True for a moment after the counts change, so the row can flash. Rising means more dead letters.</summary>
    [ObservableProperty]
    private bool _isPulsing;

    [ObservableProperty]
    private bool _isRising;

    [ObservableProperty]
    private bool _isFalling;

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public virtual bool IsBusy => false;

    public ObservableCollection<TreeNodeViewModel> VisibleChildren { get; } = [];

    public abstract ConnectionNodeViewModel Connection { get; }

    public abstract string Title { get; }

    public abstract MaterialIconKind Icon { get; }

    public virtual string? CountText => null;

    public virtual long DeadLetters => 0;

    public bool HasDeadLetters => DeadLetters > 0;

    public string DeadLetterText => Formatting.Count(DeadLetters);

    public virtual string? ToolTip => null;

    public virtual string? EnvironmentText => null;

    public bool HasEnvironment => EnvironmentText is not null;

    public virtual EnvironmentKind EnvironmentKind => EnvironmentKind.Unknown;

    public bool IsProduction => EnvironmentKind == EnvironmentKind.Production;

    public bool IsTest => EnvironmentKind == EnvironmentKind.Test;

    public bool IsDevelopment => EnvironmentKind == EnvironmentKind.Development;

    public virtual bool IsReadOnly => false;

    /// <summary>Connection rows get a button that opens their dead-letter overview, and a dot that shows whether they're connected.</summary>
    public bool IsConnection => this is ConnectionNodeViewModel;

    public virtual bool IsStateConnected => false;

    public virtual bool IsStateConnecting => false;

    public virtual bool IsStateFailed => false;

    public virtual string? StateText => null;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            OnExpanded();
        }
    }

    /// <summary>Called when the row is expanded, by a click, the keyboard or code.</summary>
    protected virtual void OnExpanded()
    {
    }

    /// <summary>Shows the children that match. Returns true when this node or a descendant matches.</summary>
    public bool ApplyFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            foreach (var child in Children)
            {
                child.ApplyFilter(null);
            }

            SyncVisible(Children);
            return true;
        }

        if (MatchesSelf(filter))
        {
            foreach (var child in Children)
            {
                child.ApplyFilter(null);
            }

            SyncVisible(Children);
            return true;
        }

        var visible = Children.Where(child => child.ApplyFilter(filter)).ToList();
        SyncVisible(visible);
        if (visible.Count > 0)
        {
            IsExpanded = true;
        }

        return visible.Count > 0;
    }

    internal void NotifyDisplayChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(DeadLetters));
        OnPropertyChanged(nameof(HasDeadLetters));
        OnPropertyChanged(nameof(DeadLetterText));
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsStateConnected));
        OnPropertyChanged(nameof(IsStateConnecting));
        OnPropertyChanged(nameof(IsStateFailed));
        OnPropertyChanged(nameof(StateText));

        // Only flash for changes seen on a refresh, not while the first load fills the tree.
        var shown = $"{CountText}|{DeadLetters}";
        if (_shownCounts is not null && shown != _shownCounts && Connection.HasLoadedOnce)
        {
            Pulse(DeadLetters.CompareTo(_shownDeadLetters));
        }

        _shownCounts = shown;
        _shownDeadLetters = DeadLetters;
    }

    private async void Pulse(int direction)
    {
        var version = ++_pulseVersion;
        IsPulsing = false;
        IsRising = direction > 0;
        IsFalling = direction < 0;

        // Let the view drop the class first, so a second change during a pulse starts the animation again.
        await Task.Yield();
        IsPulsing = true;
        await Task.Delay(PulseDuration);
        if (version == _pulseVersion)
        {
            IsPulsing = false;
        }
    }

    internal void SetChildren(IReadOnlyList<TreeNodeViewModel> children)
    {
        if (Children.SequenceEqual(children))
        {
            return;
        }

        Children.Clear();
        foreach (var child in children)
        {
            Children.Add(child);
        }
    }

    protected virtual bool MatchesSelf(string filter) => Title.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private void SyncVisible(IReadOnlyList<TreeNodeViewModel> desired)
    {
        if (VisibleChildren.SequenceEqual(desired))
        {
            return;
        }

        VisibleChildren.Clear();
        foreach (var node in desired)
        {
            VisibleChildren.Add(node);
        }
    }
}

/// <summary>
/// A saved namespace. It starts disconnected, so nothing reaches Azure until it's needed: expanding it in the tree (or
/// opening its overview) connects it, and Disconnect closes the connection and forgets what was loaded.
/// </summary>
public sealed partial class ConnectionNodeViewModel : TreeNodeViewModel, IAsyncDisposable
{
    private readonly Func<INamespaceClient> _clientFactory;
    private readonly FolderNodeViewModel _queues;
    private readonly FolderNodeViewModel _topics;
    private readonly Dictionary<string, EntityNodeViewModel> _entities = new(StringComparer.OrdinalIgnoreCase);
    private Lazy<INamespaceClient> _client;
    private string? _filter;
    private int _session;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isConnected;

    public ConnectionNodeViewModel(ConnectionProfile profile, Func<INamespaceClient> clientFactory)
    {
        Profile = profile;
        _clientFactory = clientFactory;
        _client = NewClient();
        _queues = new FolderNodeViewModel(this, "Queues") { IsExpanded = true };
        _topics = new FolderNodeViewModel(this, "Topics") { IsExpanded = true };
        SetChildren([_queues, _topics]);
        ApplyFilter(null);
    }

    public ConnectionProfile Profile { get; private set; }

    /// <summary>Raised after every load, successful or not, so views built on the counts can update.</summary>
    public event EventHandler? Loaded;

    public INamespaceClient Client => _client.Value;

    public NamespaceSnapshot? Snapshot { get; private set; }

    /// <summary>Set after the first successful load, so later count changes can be highlighted.</summary>
    public bool HasLoadedOnce { get; private set; }

    public override bool IsBusy => IsLoading;

    public override ConnectionNodeViewModel Connection => this;

    public override string Title => Profile.Name;

    public override MaterialIconKind Icon => Error is null ? MaterialIconKind.Server : MaterialIconKind.ServerOff;

    public override string? CountText =>
        Error is not null ? "can't connect"
        : !IsConnected ? "not connected"
        : IsLoading && Snapshot is null ? "connecting…"
        : null;

    public override long DeadLetters => Snapshot?.TotalDeadLetters ?? 0;

    public override bool IsStateFailed => IsConnected && Error is not null;

    public override bool IsStateConnecting => IsConnected && Error is null && Snapshot is null;

    public override bool IsStateConnected => IsConnected && Error is null && Snapshot is not null;

    public override string StateText =>
        !IsConnected ? "Not connected. Expand to connect."
        : IsStateFailed ? "Can't connect: " + Error
        : IsStateConnecting ? "Connecting…"
        : "Connected. Right-click and choose Connect / disconnect to disconnect.";

    public override string? ToolTip => Error ?? Profile.Namespace;

    public override string? EnvironmentText => string.IsNullOrWhiteSpace(Profile.Environment) ? null : Profile.Environment;

    public override EnvironmentKind EnvironmentKind => Profile.EnvironmentKind;

    public override bool IsReadOnly => Profile.ReadOnly;

    public EntityNodeViewModel? FindEntity(string path) => _entities.GetValueOrDefault(path);

    public IEnumerable<EntityNodeViewModel> Entities => _entities.Values;

    /// <summary>Connects if needed and loads the entities and their counts. While a load runs, returns that one.</summary>
    public Task LoadAsync(CancellationToken cancellationToken = default) =>
        IsLoading && _load is not null ? _load : _load = LoadCoreAsync(cancellationToken);

    private Task? _load;

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        IsConnected = true;
        IsLoading = true;
        var session = _session;
        var client = _client.Value;
        try
        {
            // Creating the client, signing in and starting the requests all run before the first real await, so they go to
            // a background thread, where they can't hold up the window.
            var snapshot = await Task.Run(() => client.GetSnapshotAsync(cancellationToken), cancellationToken);
            if (session == _session)
            {
                Error = null;
                Apply(snapshot);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (session == _session)
            {
                Error = ConnectionErrors.Explain(ex);
            }
        }
        finally
        {
            IsLoading = false;
            Loaded?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Closes the connection to Azure and forgets what was loaded. Expanding the connection again reconnects.</summary>
    public async Task DisconnectAsync()
    {
        if (!IsConnected)
        {
            return;
        }

        _session++;
        var old = _client;
        _client = NewClient();
        IsConnected = false;
        Error = null;
        Snapshot = null;
        HasLoadedOnce = false;
        _entities.Clear();
        _queues.SetChildren([]);
        _topics.SetChildren([]);
        ApplyFilter(_filter);
        IsExpanded = false;
        _queues.NotifyDisplayChanged();
        _topics.NotifyDisplayChanged();
        NotifyDisplayChanged();
        Loaded?.Invoke(this, EventArgs.Empty);
        if (old.IsValueCreated)
        {
            await old.Value.DisposeAsync();
        }
    }

    protected override void OnExpanded()
    {
        if (!IsConnected)
        {
            _ = LoadAsync();
        }
    }

    partial void OnIsConnectedChanged(bool value) => NotifyDisplayChanged();

    private Lazy<INamespaceClient> NewClient() => new(_clientFactory, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>For changes that don't need a new client, such as the read-only flag.</summary>
    public void UpdateProfile(ConnectionProfile profile)
    {
        Profile = profile;
        OnPropertyChanged(nameof(IsReadOnly));
        NotifyDisplayChanged();
    }

    public void SetFilter(string? filter)
    {
        _filter = filter;
        ApplyFilter(filter);
    }

    public ValueTask DisposeAsync() => _client.IsValueCreated ? _client.Value.DisposeAsync() : ValueTask.CompletedTask;

    partial void OnIsLoadingChanged(bool value) => NotifyDisplayChanged();

    partial void OnErrorChanged(string? value) => NotifyDisplayChanged();

    protected override bool MatchesSelf(string filter) => false;

    private void Apply(NamespaceSnapshot snapshot)
    {
        Snapshot = snapshot;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        _queues.SetChildren(snapshot.Queues.Select(q => (TreeNodeViewModel)GetOrCreate(q, seen)).ToList());

        var topics = new List<TreeNodeViewModel>();
        foreach (var topic in snapshot.Topics)
        {
            var topicNode = GetOrCreate(topic.Topic, seen);
            topicNode.SetChildren(topic.Subscriptions.Select(s => (TreeNodeViewModel)GetOrCreate(s, seen)).ToList());
            topicNode.NotifyDisplayChanged();
            topics.Add(topicNode);
        }

        _topics.SetChildren(topics);

        foreach (var stale in _entities.Keys.Where(path => !seen.Contains(path)).ToList())
        {
            _entities.Remove(stale);
        }

        _queues.NotifyDisplayChanged();
        _topics.NotifyDisplayChanged();
        ApplyFilter(_filter);
        NotifyDisplayChanged();
        HasLoadedOnce = true;
    }

    private EntityNodeViewModel GetOrCreate(EntityInfo entity, HashSet<string> seen)
    {
        seen.Add(entity.Path);
        if (_entities.TryGetValue(entity.Path, out var node))
        {
            node.Update(entity);
            return node;
        }

        node = new EntityNodeViewModel(this, entity);
        _entities[entity.Path] = node;
        node.NotifyDisplayChanged();
        node.MessagesNode?.NotifyDisplayChanged();
        node.DeadLetterNode?.NotifyDisplayChanged();
        node.TransferDeadLetterNode?.NotifyDisplayChanged();
        return node;
    }
}

public sealed class FolderNodeViewModel(ConnectionNodeViewModel connection, string title) : TreeNodeViewModel
{
    public override ConnectionNodeViewModel Connection => connection;

    public override string Title => title;

    public override MaterialIconKind Icon => MaterialIconKind.FolderOutline;

    public override string? CountText => connection.Snapshot is null ? null : $"({Formatting.Count(Children.Count)})";

    public override long DeadLetters => Children.OfType<EntityNodeViewModel>().Sum(c => c.DeadLetters);

    protected override bool MatchesSelf(string filter) => false;
}

public sealed class EntityNodeViewModel : TreeNodeViewModel
{
    private readonly ConnectionNodeViewModel _connection;

    public EntityNodeViewModel(ConnectionNodeViewModel connection, EntityInfo entity)
    {
        _connection = connection;
        Entity = entity;
        if (entity.Kind != EntityKind.Topic)
        {
            MessagesNode = new SubQueueNodeViewModel(this, SubQueueKind.Active);
            DeadLetterNode = new SubQueueNodeViewModel(this, SubQueueKind.DeadLetter);
            TransferDeadLetterNode = new SubQueueNodeViewModel(this, SubQueueKind.TransferDeadLetter);
            SetSubQueueNodes();
            ApplyFilter(null);
        }
    }

    public EntityInfo Entity { get; private set; }

    public SubQueueNodeViewModel? MessagesNode { get; }

    public SubQueueNodeViewModel? DeadLetterNode { get; }

    public SubQueueNodeViewModel? TransferDeadLetterNode { get; }

    public override ConnectionNodeViewModel Connection => _connection;

    public override string Title => Entity.Name;

    public override MaterialIconKind Icon => Entity.Kind switch
    {
        EntityKind.Queue => MaterialIconKind.TrayFull,
        EntityKind.Topic => MaterialIconKind.BullhornOutline,
        _ => MaterialIconKind.SubdirectoryArrowRight,
    };

    public override string? CountText => Entity.Kind == EntityKind.Topic ? null : $"({Formatting.Count(Entity.Counts.Active)})";

    public override long DeadLetters => Entity.Kind == EntityKind.Topic
        ? Children.OfType<EntityNodeViewModel>().Sum(c => c.DeadLetters)
        : Entity.Counts.DeadLetter + Entity.Counts.TransferDeadLetter;

    public override string? ToolTip => Entity.Kind == EntityKind.Topic
        ? $"{Entity.Path} · {Entity.Settings.SubscriptionCount ?? Children.Count} subscriptions"
        : $"{Entity.Path} · active {Formatting.Count(Entity.Counts.Active)} · dead-letter {Formatting.Count(Entity.Counts.DeadLetter)}"
          + $" · scheduled {Formatting.Count(Entity.Counts.Scheduled)} · max deliveries {Entity.Settings.MaxDeliveryCount?.ToString() ?? "–"}";

    public void Update(EntityInfo entity)
    {
        Entity = entity;
        SetSubQueueNodes();
        NotifyDisplayChanged();
        MessagesNode?.NotifyDisplayChanged();
        DeadLetterNode?.NotifyDisplayChanged();
        TransferDeadLetterNode?.NotifyDisplayChanged();
    }

    // The transfer dead-letter queue is nearly always empty, so its node only shows when it holds messages.
    private void SetSubQueueNodes()
    {
        if (MessagesNode is null || DeadLetterNode is null || TransferDeadLetterNode is null)
        {
            return;
        }

        SetChildren(Entity.Counts.TransferDeadLetter > 0 ? [MessagesNode, DeadLetterNode, TransferDeadLetterNode] : [MessagesNode, DeadLetterNode]);
    }
}

public sealed class SubQueueNodeViewModel(EntityNodeViewModel owner, SubQueueKind subQueue) : TreeNodeViewModel
{
    public EntityNodeViewModel Owner { get; } = owner;

    public SubQueueKind SubQueue { get; } = subQueue;

    public override ConnectionNodeViewModel Connection => Owner.Connection;

    public override string Title => SubQueue switch
    {
        SubQueueKind.Active => "Messages",
        SubQueueKind.DeadLetter => "Dead-letter",
        _ => "Transfer dead-letter",
    };

    public override MaterialIconKind Icon => SubQueue switch
    {
        SubQueueKind.Active => MaterialIconKind.EmailOutline,
        SubQueueKind.DeadLetter => MaterialIconKind.EmailAlertOutline,
        _ => MaterialIconKind.TransferRight,
    };

    public override string? CountText => $"({Formatting.Count(Count)})";

    private long Count => SubQueue switch
    {
        SubQueueKind.Active => Owner.Entity.Counts.Active,
        SubQueueKind.DeadLetter => Owner.Entity.Counts.DeadLetter,
        _ => Owner.Entity.Counts.TransferDeadLetter,
    };

    protected override bool MatchesSelf(string filter) => false;
}
