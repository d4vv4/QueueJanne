using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using QueueJanne.App.Services;
using QueueJanne.Core.Bodies;
using QueueJanne.Core.Connections;
using QueueJanne.Core.Entities;
using QueueJanne.Core.Messages;
using QueueJanne.Core.ServiceBus;

namespace QueueJanne.App.ViewModels;

/// <summary>One open queue or subscription: the peeked messages, the selected message's body and its properties.</summary>
public sealed partial class EntityTabViewModel : DocumentViewModel
{
    private const int PageSize = 250;
    private const int FlushIntervalMilliseconds = 150;

    private readonly IDialogService _dialogs;
    private readonly BatchCollection<MessageRowViewModel> _messages = [];
    private readonly ObservableCollection<PropertyRow> _properties = [];
    private IReadOnlyList<MessageRowViewModel> _selection = [];
    private CancellationTokenSource? _loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header), nameof(ToolTip), nameof(Icon), nameof(IsActiveQueue), nameof(IsDeadLetterQueue), nameof(IsTransferDeadLetterQueue), nameof(IsDeadLetterView), nameof(ShowsTransferDeadLetter))]
    private SubQueueKind _subQueue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BodyTitle))]
    private MessageRowViewModel? _selectedMessage;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _filterText;

    [ObservableProperty]
    private string? _reasonFilter;

    [ObservableProperty]
    private string? _subjectFilter;

    [ObservableProperty]
    private string? _propertyNameFilter;

    [ObservableProperty]
    private string? _propertyValueFilter;

    [ObservableProperty]
    private string? _bodyFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroupingByProperty))]
    private GroupOption _groupBy = GroupOption.All[0];

    [ObservableProperty]
    private string? _groupProperty;

    private MessageFilter _structuredFilter = new();

    [ObservableProperty]
    private TopOption _top;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextFormat), nameof(IsJsonFormat), nameof(IsXmlFormat), nameof(IsHexFormat), nameof(BodyTitle))]
    private BodyFormat _bodyFormat;

    [ObservableProperty]
    private string? _bodyText;

    public EntityTabViewModel(ConnectionNodeViewModel connection, EntityInfo entity, SubQueueKind subQueue, IDialogService dialogs, Action<DocumentViewModel> close)
        : base(close)
    {
        Connection = connection;
        Entity = entity;
        _subQueue = subQueue;
        _dialogs = dialogs;
        _top = TopOptions[2];
        Messages = new DataGridCollectionView(_messages)
        {
            Filter = item => item is MessageRowViewModel row
                && (string.IsNullOrWhiteSpace(FilterText) || row.Matches(FilterText.Trim()))
                && _structuredFilter.Matches(row.Message),
        };
        Properties = new DataGridCollectionView(_properties);
        Properties.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(PropertyRow.Group)));
    }

    public ConnectionNodeViewModel Connection { get; }

    /// <summary>Every open connection, offered as targets when sending, copying and moving.</summary>
    public IReadOnlyList<ConnectionNodeViewModel> AllConnections { get; init; } = [];

    public EntityInfo Entity { get; private set; }

    public EntityAddress Address => EntityAddress.For(Entity, SubQueue);

    public DataGridCollectionView Messages { get; }

    public DataGridCollectionView Properties { get; }

    public IReadOnlyList<TopOption> TopOptions { get; } = [new(100), new(500), new(1_000), new(5_000), new(20_000), new(int.MaxValue)];

    public override string Header => SubQueue switch
    {
        SubQueueKind.DeadLetter => $"{Entity.Path} · dead-letter",
        SubQueueKind.TransferDeadLetter => $"{Entity.Path} · transfer DL",
        _ => Entity.Path,
    };

    public override string ToolTip => $"{Address.DisplayName} on {Connection.Profile.Name} ({Connection.Profile.Namespace})"
        + (IsPreview ? "\nPreview tab: the next single click replaces it. Double-click to keep it open." : string.Empty);

    public ViewSettings Settings => ViewSettings.Current;

    public override MaterialIconKind Icon => SubQueue != SubQueueKind.Active
        ? MaterialIconKind.EmailAlertOutline
        : Entity.Kind == EntityKind.Queue ? MaterialIconKind.TrayFull : MaterialIconKind.SubdirectoryArrowRight;

    public string ActiveLabel => $"Messages ({Formatting.Count(Entity.Counts.Active)})";

    public string DeadLetterLabel => $"Dead-letter ({Formatting.Count(Entity.Counts.DeadLetter)})";

    /// <summary>The transfer dead-letter queue is nearly always empty, so its button only shows when it has messages or is open.</summary>
    public bool ShowsTransferDeadLetter => Entity.Counts.TransferDeadLetter > 0 || SubQueue == SubQueueKind.TransferDeadLetter;

    public string TransferDeadLetterLabel => $"Transfer DL ({Formatting.Count(Entity.Counts.TransferDeadLetter)})";

    /// <summary>The entity's live settings, read on every refresh rather than assumed.</summary>
    public string SettingsText
    {
        get
        {
            var settings = Entity.Settings;
            var parts = new List<string> { $"Max deliveries {settings.MaxDeliveryCount?.ToString() ?? "–"}", $"Lock {Formatting.Duration(settings.LockDuration)}" };
            if (settings.RequiresSession)
            {
                parts.Add("Sessions");
            }

            if (settings.RequiresDuplicateDetection)
            {
                parts.Add($"Duplicate detection {Formatting.Duration(settings.DuplicateDetectionWindow)}");
            }

            if (settings.ForwardTo is { Length: > 0 } forwardTo)
            {
                parts.Add($"Forwards to {forwardTo}");
            }

            return string.Join(" · ", parts);
        }
    }

    public bool IsActiveQueue
    {
        get => SubQueue == SubQueueKind.Active;
        set => SelectSubQueue(SubQueueKind.Active, value);
    }

    public bool IsDeadLetterQueue
    {
        get => SubQueue == SubQueueKind.DeadLetter;
        set => SelectSubQueue(SubQueueKind.DeadLetter, value);
    }

    public bool IsTransferDeadLetterQueue
    {
        get => SubQueue == SubQueueKind.TransferDeadLetter;
        set => SelectSubQueue(SubQueueKind.TransferDeadLetter, value);
    }

    /// <summary>Resubmit is offered in the dead-letter and transfer dead-letter queues.</summary>
    public bool IsDeadLetterView => SubQueue != SubQueueKind.Active;

    public bool IsTextFormat
    {
        get => BodyFormat == BodyFormat.Text;
        set => SelectFormat(BodyFormat.Text, value);
    }

    public bool IsJsonFormat
    {
        get => BodyFormat == BodyFormat.Json;
        set => SelectFormat(BodyFormat.Json, value);
    }

    public bool IsXmlFormat
    {
        get => BodyFormat == BodyFormat.Xml;
        set => SelectFormat(BodyFormat.Xml, value);
    }

    public bool IsHexFormat
    {
        get => BodyFormat == BodyFormat.Hex;
        set => SelectFormat(BodyFormat.Hex, value);
    }

    public string BodyTitle => SelectedMessage switch
    {
        { Body.IsDecompressed: true } row => $"Message body · #{row.SequenceNumber} · {row.SizeText} {row.Body.Encoding}, {Formatting.Bytes(row.Body.Bytes.Length)} unpacked",
        { } row => $"Message body · #{row.SequenceNumber} · {row.SizeText}",
        null => "Message body",
    };

    /// <summary>How many messages are shown and loaded, and when the peek stopped at its limit, how many the sub-queue holds.</summary>
    public string CountText
    {
        get
        {
            var total = SubQueue switch
            {
                SubQueueKind.DeadLetter => Entity.Counts.DeadLetter,
                SubQueueKind.TransferDeadLetter => Entity.Counts.TransferDeadLetter,
                _ => Entity.Counts.Active + Entity.Counts.Scheduled,
            };
            var loaded = !IsLoading && _stoppedAtLimit && total > _messages.Count
                ? $"{Formatting.Count(_messages.Count)} of {Formatting.Count(total)} loaded"
                : $"{Formatting.Count(_messages.Count)} messages";
            return Messages.Count == _messages.Count ? loaded : $"{Formatting.Count(Messages.Count)} shown · {loaded}";
        }
    }

    partial void OnIsLoadingChanged(bool value) => NotifyCounts();

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>Shown over the grid when it has no rows, saying why; null when there are rows.</summary>
    public string? EmptyText => Messages.Count > 0 || IsLoading
        ? null
        : _messages.Count > 0
            ? "No loaded message matches the filter."
            : SubQueue switch
            {
                SubQueueKind.DeadLetter => "The dead-letter queue is empty.",
                SubQueueKind.TransferDeadLetter => "The transfer dead-letter queue is empty.",
                _ => "No messages.",
            };

    /// <summary>True when the last peek ended because it reached the chosen number of messages, so there may be more.</summary>
    private bool _stoppedAtLimit;

    public string SelectionText => _selection.Count > 1 ? $"{Formatting.Count(_selection.Count)} selected" : string.Empty;

    public IReadOnlyList<GroupOption> GroupOptions => GroupOption.All;

    public bool IsGroupingByProperty => GroupBy.Kind == GroupKind.ApplicationProperty;

    /// <summary>True when any of the filter bar's fields is set; then "matching" operations act on every match in the sub-queue.</summary>
    public bool HasStructuredFilter => !_structuredFilter.IsEmpty;

    [ObservableProperty]
    private IReadOnlyList<string> _reasonSuggestions = [];

    [ObservableProperty]
    private IReadOnlyList<string> _subjectSuggestions = [];

    /// <summary>Application property names seen in the loaded messages, offered when filtering, grouping and adding columns.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _applicationPropertyNames = [];

    private MessageRowViewModel? FirstShown => Messages.Cast<MessageRowViewModel>().FirstOrDefault();

    public string LocationText => $"{Connection.Profile.Name} · {Connection.Profile.Namespace}";

    public async Task LoadAsync()
    {
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        IsLoading = true;
        var keepSelected = SelectedMessage?.SequenceNumber;
        SelectedMessage = null;
        _messages.Clear();
        ProgressText = "Peeking…";
        NotifyCounts();
        try
        {
            var options = new PeekOptions { MaxMessages = Top.Value, PageSize = PageSize };
            var pending = new List<MessageRowViewModel>();
            var sinceFlush = Stopwatch.StartNew();
            void Flush()
            {
                foreach (var row in pending)
                {
                    WithGroup(row);
                }

                var kept = keepSelected is { } sequenceNumber ? pending.FirstOrDefault(r => r.SequenceNumber == sequenceNumber) : null;
                _messages.AddRange(pending);
                pending.Clear();
                sinceFlush.Restart();
                ProgressText = $"Peeking… {Formatting.Count(_messages.Count)}";
                NotifyCounts();

                // A refresh keeps the message that was selected when it's still there; otherwise the first one is shown.
                if (kept is not null && (SelectedMessage is null || SelectedMessage == FirstShown))
                {
                    SelectedMessage = kept;
                    keepSelected = null;
                }

                SelectedMessage ??= FirstShown;
            }

            // Rows are built off the UI thread and shown in batches: the first page at once, then at most a few times a second,
            // because every batch makes the grid filter, sort and group all rows again.
            await foreach (var rows in PeekRowsAsync(options, loading.Token))
            {
                loading.Token.ThrowIfCancellationRequested();
                pending.AddRange(rows);
                if (_messages.Count == 0 || sinceFlush.ElapsedMilliseconds >= FlushIntervalMilliseconds)
                {
                    Flush();
                }
            }

            loading.Token.ThrowIfCancellationRequested();
            Flush();
            _stoppedAtLimit = _messages.Count >= Top.Value;
            ProgressText = $"Peeked in {stopwatch.Elapsed.TotalSeconds:0.00} s";
            UpdateSuggestions();
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ProgressText = "Couldn't read messages";
            await _dialogs.ShowMessageAsync("Couldn't read messages", ConnectionErrors.Explain(ex));
        }
        finally
        {
            if (ReferenceEquals(_loading, loading))
            {
                IsLoading = false;
            }

            NotifyCounts();
        }
    }

    private async IAsyncEnumerable<IReadOnlyList<MessageRowViewModel>> PeekRowsAsync(PeekOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pages = Connection.Client.PeekAsync(Address, options, cancellationToken);
        await using var enumerator = pages.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            var next = await Task.Run(async () =>
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    return null;
                }

                return enumerator.Current.Select(m => new MessageRowViewModel(m)).ToList();
            }, cancellationToken);
            if (next is null)
            {
                yield break;
            }

            yield return next;
        }
    }

    /// <summary>True for a moment after a refresh changes this entity's counts, so the sub-queue buttons can flash.</summary>
    [ObservableProperty]
    private bool _isCountsPulsing;

    public async void UpdateEntity(EntityInfo entity)
    {
        var changed = entity.Counts.Active != Entity.Counts.Active
            || entity.Counts.DeadLetter != Entity.Counts.DeadLetter
            || entity.Counts.TransferDeadLetter != Entity.Counts.TransferDeadLetter;
        Entity = entity;
        OnPropertyChanged(nameof(ActiveLabel));
        OnPropertyChanged(nameof(DeadLetterLabel));
        OnPropertyChanged(nameof(TransferDeadLetterLabel));
        OnPropertyChanged(nameof(ShowsTransferDeadLetter));
        OnPropertyChanged(nameof(SettingsText));
        if (!changed)
        {
            return;
        }

        var version = ++_pulseVersion;
        IsCountsPulsing = false;
        await Task.Yield();
        IsCountsPulsing = true;
        await Task.Delay(1600);
        if (version == _pulseVersion)
        {
            IsCountsPulsing = false;
        }
    }

    private int _pulseVersion;

    public void SetSelection(IReadOnlyList<MessageRowViewModel> selection)
    {
        _selection = selection;
        OnPropertyChanged(nameof(SelectionText));
    }

    public override void Dispose()
    {
        _loading?.Cancel();
        _loading?.Dispose();
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task CopyBodyAsync()
    {
        if (SelectedMessage is { } row)
        {
            await _dialogs.CopyToClipboardAsync(BodyFormatter.Format(row.Body.Bytes, BodyFormat));
            ProgressText = "Body copied";
        }
    }

    [RelayCommand]
    private async Task CopyMessageIdAsync()
    {
        if (SelectedMessage?.MessageId is { } messageId)
        {
            await _dialogs.CopyToClipboardAsync(messageId);
            ProgressText = "Message ID copied";
        }
    }

    [RelayCommand]
    private async Task CopyAsJsonAsync()
    {
        var rows = SelectedOrCurrent();
        if (rows.Count > 0)
        {
            await _dialogs.CopyToClipboardAsync(MessageExport.ToJson(rows.Select(r => r.Message)));
            ProgressText = $"{Formatting.Count(rows.Count)} message(s) copied as JSON";
        }
    }

    [RelayCommand]
    private async Task ExportSelectedAsync()
    {
        var rows = _selection.Count > 1 ? _selection : Messages.Cast<MessageRowViewModel>().ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var fileName = $"{Entity.Path.Replace('/', '_')}-{SubQueue.ToString().ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        if (await _dialogs.SaveTextFileAsync(fileName, MessageExport.ToJson(rows.Select(r => r.Message))))
        {
            ProgressText = $"Exported {Formatting.Count(rows.Count)} messages";
        }
    }

    [RelayCommand]
    private Task ResubmitSelectedAsync() => RunOperationAsync(progress =>
        EntityOperations.ResubmitAsync(Connection, Entity, SubQueue, SelectedOrCurrent() is { Count: > 0 } rows ? rows : null, _dialogs, progress));

    [RelayCommand]
    private Task ResubmitAllAsync() => RunOperationAsync(progress =>
        EntityOperations.ResubmitAsync(Connection, Entity, SubQueue, null, _dialogs, progress));

    [RelayCommand]
    private Task DeleteSelectedAsync() => RunOperationAsync(progress =>
        EntityOperations.DeleteAsync(Connection, Entity, SubQueue, SelectedOrCurrent(), _dialogs, progress));

    [RelayCommand]
    private Task PurgeAsync() => RunOperationAsync(progress =>
        EntityOperations.PurgeAsync(Connection, Entity, SubQueue, _dialogs, progress));

    /// <summary>Opens the selected message in the Send dialog, addressed to where a resubmit would send it.</summary>
    [RelayCommand]
    private async Task EditAndResendAsync()
    {
        if (SelectedMessage is not { } row)
        {
            return;
        }

        var dialog = SendMessageViewModel.EditAndResend(Connections(), Connection, ResubmitAdvice.TargetOf(Address), row.Message);
        if (await _dialogs.ShowSendAsync(dialog) is { } messages)
        {
            ProgressText = await EntityOperations.SendAsync(dialog, messages, _dialogs);
            if (Connection.FindEntity(Entity.Path) is { } node)
            {
                UpdateEntity(node.Entity);
            }
        }
    }

    [RelayCommand]
    private Task CopyToAsync() => RunOperationAsync(async progress =>
    {
        var rows = SelectedOrCurrent();
        if (rows.Count == 0)
        {
            return null;
        }

        var dialog = SendMessageViewModel.CopyOrMove(Connections(), Connection, Address, rows.Select(r => r.Message).ToList());
        return await _dialogs.ShowSendAsync(dialog) is null ? null : await EntityOperations.TransferAsync(dialog, _dialogs, progress);
    });

    private IReadOnlyList<ConnectionNodeViewModel> Connections() => AllConnections.Count > 0 ? AllConnections : [Connection];

    private async Task RunOperationAsync(Func<IProgress<string>, Task<OperationOutcome?>> operation)
    {
        IsLoading = true;
        try
        {
            // Progress reports are posted to the UI thread and can arrive after the operation returned; they mustn't
            // replace the summary.
            var finished = false;
            var outcome = await operation(new Progress<string>(text =>
            {
                if (!finished)
                {
                    ProgressText = text;
                }
            }));
            finished = true;
            if (outcome is null)
            {
                return;
            }

            if (Connection.FindEntity(Entity.Path) is { } node)
            {
                UpdateEntity(node.Entity);
            }

            // When the operation finished for chosen messages, drop just those rows: the rest of the tab, its scroll position
            // and the next message stay put. Anything less certain peeks again.
            if (outcome is { Complete: true, SequenceNumbers: { } removed })
            {
                RemoveRows(removed);
            }
            else
            {
                await LoadAsync();
            }

            ProgressText = outcome.Summary;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RemoveRows(IReadOnlyList<long> sequenceNumbers)
    {
        var gone = sequenceNumbers.ToHashSet();
        var shown = Messages.Cast<MessageRowViewModel>().ToList();
        var lastRemoved = shown.FindLastIndex(r => gone.Contains(r.SequenceNumber));
        var next = shown.Skip(lastRemoved + 1).FirstOrDefault(r => !gone.Contains(r.SequenceNumber))
            ?? shown.Take(Math.Max(lastRemoved, 0)).LastOrDefault(r => !gone.Contains(r.SequenceNumber));
        _messages.RemoveWhere(r => gone.Contains(r.SequenceNumber));
        SelectedMessage = next;
        NotifyCounts();
    }

    partial void OnSelectedMessageChanged(MessageRowViewModel? value)
    {
        _properties.Clear();
        if (value is null)
        {
            BodyText = null;
            return;
        }

        foreach (var row in PropertyRow.For(value.Message))
        {
            _properties.Add(row);
        }

        var format = BodyFormatter.Detect(value.Body.Bytes, value.Message.ContentType);
        if (format == BodyFormat)
        {
            BodyText = BodyFormatter.Format(value.Body.Bytes, format);
        }
        else
        {
            BodyFormat = format;
        }
    }

    partial void OnBodyFormatChanged(BodyFormat value) =>
        BodyText = SelectedMessage is null ? null : BodyFormatter.Format(SelectedMessage.Body.Bytes, value);

    partial void OnFilterTextChanged(string? value) => ScheduleFilter(value);

    partial void OnReasonFilterChanged(string? value) => ScheduleFilter(value);

    partial void OnSubjectFilterChanged(string? value) => ScheduleFilter(value);

    partial void OnPropertyNameFilterChanged(string? value) => ScheduleFilter(value);

    partial void OnPropertyValueFilterChanged(string? value) => ScheduleFilter(value);

    partial void OnBodyFilterChanged(string? value) => ScheduleFilter(value);

    private int _filterVersion;

    /// <summary>Applies the filters once typing pauses, so a large tab isn't filtered again on every key. Clearing a field applies at once.</summary>
    private async void ScheduleFilter(string? changedValue)
    {
        if (_clearingFilters)
        {
            return;
        }

        var version = ++_filterVersion;
        if (!string.IsNullOrEmpty(changedValue))
        {
            await Task.Delay(_messages.Count > 2_000 ? 250 : 80);
            if (version != _filterVersion)
            {
                return;
            }
        }

        ApplyStructuredFilter();
    }

    partial void OnGroupByChanged(GroupOption value) => ApplyGrouping();

    partial void OnGroupPropertyChanged(string? value) => ApplyGrouping();

    [RelayCommand]
    private void ClearFilters()
    {
        _clearingFilters = true;
        try
        {
            ReasonFilter = SubjectFilter = PropertyNameFilter = PropertyValueFilter = BodyFilter = FilterText = null;
        }
        finally
        {
            _clearingFilters = false;
        }

        ApplyStructuredFilter();
    }

    private bool _clearingFilters;

    [RelayCommand]
    private Task ResubmitMatchingAsync() => RunOperationAsync(async progress =>
        await FindMatchingAsync(progress) is { } rows
            ? await EntityOperations.ResubmitAsync(Connection, Entity, SubQueue, rows, _dialogs, progress)
            : null);

    [RelayCommand]
    private Task DeleteMatchingAsync() => RunOperationAsync(async progress =>
        await FindMatchingAsync(progress) is { } rows
            ? await EntityOperations.DeleteAsync(Connection, Entity, SubQueue, rows, _dialogs, progress)
            : null);

    /// <summary>Every message in the sub-queue that matches the filter bar, not only the loaded ones. Peeks, so nothing is locked.</summary>
    private async Task<IReadOnlyList<MessageRowViewModel>?> FindMatchingAsync(IProgress<string> progress)
    {
        if (!await EntityOperations.EnsureWritableAsync(Connection, _dialogs))
        {
            return null;
        }

        progress.Report("Finding matching messages…");
        var counts = Entity.Counts;
        var expected = SubQueue switch
        {
            SubQueueKind.DeadLetter => counts.DeadLetter,
            SubQueueKind.TransferDeadLetter => counts.TransferDeadLetter,
            _ => counts.Active + counts.Scheduled,
        };
        var limit = (int)Math.Clamp(expected + 500, 1_000, 50_000);
        var found = await MessageSelection.FindAsync(Connection.Client, Address, _structuredFilter, limit, limit);
        if (found.Messages.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Nothing matches", $"No message in {Address.DisplayName} matches the filter.");
            return null;
        }

        return found.Messages.Select(m => new MessageRowViewModel(m)).ToList();
    }

    private void ApplyStructuredFilter()
    {
        _filterVersion++;
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _structuredFilter = new MessageFilter
        {
            DeadLetterReason = Clean(ReasonFilter),
            Subject = Clean(SubjectFilter),
            PropertyName = Clean(PropertyNameFilter),
            PropertyValue = Clean(PropertyNameFilter) is null ? null : Clean(PropertyValueFilter),
            BodyContains = Clean(BodyFilter),
            Partial = true,
        };
        Messages.Refresh();
        NotifyCounts();
        OnPropertyChanged(nameof(HasStructuredFilter));
    }

    private void ApplyGrouping()
    {
        foreach (var row in _messages)
        {
            WithGroup(row);
        }

        Messages.GroupDescriptions.Clear();
        if (GroupBy.Kind != GroupKind.None && (GroupBy.Kind != GroupKind.ApplicationProperty || !string.IsNullOrWhiteSpace(GroupProperty)))
        {
            Messages.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(MessageRowViewModel.GroupValue)));
        }

        Messages.Refresh();
    }

    private MessageRowViewModel WithGroup(MessageRowViewModel row)
    {
        var value = GroupBy.Kind switch
        {
            GroupKind.DeadLetterReason => row.DeadLetterReason,
            GroupKind.ErrorDescription => row.DeadLetterDescription,
            GroupKind.Subject => row.Subject,
            GroupKind.ContentType => row.ContentType,
            GroupKind.ApplicationProperty when !string.IsNullOrWhiteSpace(GroupProperty) => row.ApplicationProperty(GroupProperty.Trim()),
            _ => null,
        };
        row.GroupValue = string.IsNullOrWhiteSpace(value) ? "(none)" : value;
        return row;
    }

    private void UpdateSuggestions()
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in _messages)
        {
            var message = row.Message;
            if (!string.IsNullOrWhiteSpace(message.DeadLetterReason))
            {
                reasons.Add(message.DeadLetterReason);
            }

            if (!string.IsNullOrWhiteSpace(message.Subject))
            {
                subjects.Add(message.Subject);
            }

            properties.UnionWith(message.ApplicationProperties.Keys);
        }

        properties.Remove("DeadLetterReason");
        properties.Remove("DeadLetterErrorDescription");
        ReasonSuggestions = reasons.Order(StringComparer.OrdinalIgnoreCase).ToList();
        SubjectSuggestions = subjects.Order(StringComparer.OrdinalIgnoreCase).ToList();
        ApplicationPropertyNames = properties.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    partial void OnTopChanged(TopOption value) => _ = LoadAsync();

    private IReadOnlyList<MessageRowViewModel> SelectedOrCurrent() =>
        _selection.Count > 1 ? _selection : SelectedMessage is { } row ? [row] : [];

    private void SelectSubQueue(SubQueueKind kind, bool selected)
    {
        if (selected && SubQueue != kind)
        {
            SubQueue = kind;
            _ = LoadAsync();
            return;
        }

        // Clicking the checked button again would uncheck it; tell the view the state didn't change.
        OnPropertyChanged(nameof(IsActiveQueue));
        OnPropertyChanged(nameof(IsDeadLetterQueue));
        OnPropertyChanged(nameof(IsTransferDeadLetterQueue));
    }

    private void SelectFormat(BodyFormat format, bool selected)
    {
        if (selected)
        {
            BodyFormat = format;
        }

        OnPropertyChanged(nameof(IsTextFormat));
        OnPropertyChanged(nameof(IsJsonFormat));
        OnPropertyChanged(nameof(IsXmlFormat));
        OnPropertyChanged(nameof(IsHexFormat));
    }
}
