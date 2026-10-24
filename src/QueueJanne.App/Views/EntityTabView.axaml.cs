using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using QueueJanne.App.ViewModels;

namespace QueueJanne.App.Views;

public partial class EntityTabView : UserControl
{
    private readonly ViewSettings _settings = ViewSettings.Current;
    private bool _buildingColumns;

    public EntityTabView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTabKeyDown, RoutingStrategies.Tunnel);
        PropertiesSplitter.DragCompleted += (_, _) => _settings.PropertiesWidth = DetailsGrid.ColumnDefinitions[2].ActualWidth;
        DetailsSplitter.DragCompleted += (_, _) => _settings.DetailsHeight = RootGrid.RowDefinitions[5].ActualHeight;
        ApplyLayout();
        BuildColumns();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _settings.PropertyChanged += OnSettingsChanged;
        ApplyLayout();
        BuildColumns();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SaveColumns();
        _settings.PropertyChanged -= OnSettingsChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewSettings.Columns))
        {
            BuildColumns();
        }
        else
        {
            ApplyLayout();
        }
    }

    // The splitters resize grid definitions directly, so the shared layout is applied in code rather than bound.
    private void ApplyLayout()
    {
        var show = _settings.ShowProperties;
        DetailsGrid.ColumnDefinitions[1].Width = new GridLength(show ? 4 : 0);
        DetailsGrid.ColumnDefinitions[2].Width = new GridLength(show ? _settings.PropertiesWidth : 0);
        PropertiesSplitter.IsVisible = show;
        PropertiesPanel.IsVisible = show;
        RootGrid.RowDefinitions[5].Height = new GridLength(_settings.DetailsHeight);
    }

    private void BuildColumns()
    {
        if (_buildingColumns || ColumnsMatchSettings())
        {
            return;
        }

        _buildingColumns = true;
        var selected = MessageGrid.SelectedItem;
        try
        {
            MessageGrid.Columns.Clear();
            foreach (var setting in _settings.Columns.Where(c => c.Visible))
            {
                var definition = ColumnCatalog.Find(setting.Key);
                var column = CreateColumn(definition);
                column.Header = definition.Header;
                column.Width = new DataGridLength(setting.Width);
                column.Tag = setting.Key;
                MessageGrid.Columns.Add(column);
            }

            // Replacing columns clears the grid's selection; put it back so the body pane keeps its message.
            if (selected is not null)
            {
                MessageGrid.SelectedItem = selected;
            }
        }
        finally
        {
            _buildingColumns = false;
        }
    }

    // Rows never change after they're created, so cells bind once with compiled bindings: realizing a screenful of rows
    // with reflection bindings cost noticeably more.
    private static readonly Dictionary<string, Func<BindingBase>> CellBindings = new(StringComparer.Ordinal)
    {
        [nameof(MessageRowViewModel.SequenceNumber)] = () => Once(r => r.SequenceNumber),
        [nameof(MessageRowViewModel.EnqueuedText)] = () => Once(r => r.EnqueuedText),
        [nameof(MessageRowViewModel.Subject)] = () => Once(r => r.Subject),
        [nameof(MessageRowViewModel.SizeText)] = () => Once(r => r.SizeText),
        [nameof(MessageRowViewModel.DeliveryCount)] = () => Once(r => r.DeliveryCount),
        [nameof(MessageRowViewModel.DeadLetterReason)] = () => Once(r => r.DeadLetterReason),
        [nameof(MessageRowViewModel.DeadLetterDescription)] = () => Once(r => r.DeadLetterDescription),
        [nameof(MessageRowViewModel.MessageId)] = () => Once(r => r.MessageId),
        [nameof(MessageRowViewModel.ContentType)] = () => Once(r => r.ContentType),
        [nameof(MessageRowViewModel.SessionId)] = () => Once(r => r.SessionId),
        [nameof(MessageRowViewModel.CorrelationId)] = () => Once(r => r.CorrelationId),
        [nameof(MessageRowViewModel.State)] = () => Once(r => r.State),
        [nameof(MessageRowViewModel.To)] = () => Once(r => r.To),
        [nameof(MessageRowViewModel.ReplyTo)] = () => Once(r => r.ReplyTo),
        [nameof(MessageRowViewModel.ExpiresText)] = () => Once(r => r.ExpiresText),
        [nameof(MessageRowViewModel.Preview)] = () => Once(r => r.Preview),
    };

    private static CompiledBinding Once<T>(System.Linq.Expressions.Expression<Func<MessageRowViewModel, T>> path) =>
        CompiledBinding.Create(path, mode: BindingMode.OneTime);

    private static DataGridColumn CreateColumn(MessageColumn definition)
    {
        if (definition.Path is { } path)
        {
            var binding = CellBindings.TryGetValue(path, out var create) ? create() : new ReflectionBinding(path) { Mode = BindingMode.OneTime };
            return new DataGridTextColumn { Binding = binding, SortMemberPath = definition.SortPath ?? path };
        }

        // Application property names can contain characters a binding path can't, so these cells read the value directly.
        var name = definition.PropertyName;
        return new DataGridTemplateColumn
        {
            CellTemplate = new FuncDataTemplate<MessageRowViewModel>((_, _) =>
            {
                var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0), TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
                text.DataContextChanged += (_, _) => text.Text = (text.DataContext as MessageRowViewModel)?.ApplicationProperty(name);
                return text;
            }),
            CustomSortComparer = Comparer<object>.Create((a, b) => string.Compare(
                (a as MessageRowViewModel)?.ApplicationProperty(name),
                (b as MessageRowViewModel)?.ApplicationProperty(name),
                StringComparison.CurrentCultureIgnoreCase)),
        };
    }

    private bool ColumnsMatchSettings()
    {
        var shown = _settings.Columns.Where(c => c.Visible).Select(c => c.Key).ToList();
        return MessageGrid.Columns.Count == shown.Count
            && MessageGrid.Columns.OrderBy(c => c.DisplayIndex).Select(c => c.Tag as string).SequenceEqual(shown);
    }

    /// <summary>Stores the order and widths people set by dragging, keeping hidden columns where they were.</summary>
    private void SaveColumns()
    {
        if (_buildingColumns || MessageGrid.Columns.Count == 0)
        {
            return;
        }

        var shown = MessageGrid.Columns
            .OrderBy(c => c.DisplayIndex)
            .Select(c => new ColumnSetting((string)c.Tag!, c.ActualWidth > 0 ? Math.Round(c.ActualWidth) : c.Width.Value, true))
            .ToList();
        var queue = new Queue<ColumnSetting>(shown);
        var merged = _settings.Columns
            .Select(saved => saved.Visible && queue.Count > 0 ? queue.Dequeue() : saved)
            .Concat(queue)
            .ToList();
        if (!merged.SequenceEqual(_settings.Columns))
        {
            _settings.Columns = merged;
        }
    }

    private void OnColumnReordered(object? sender, DataGridColumnEventArgs e) => SaveColumns();

    private async void OnEditColumns(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        SaveColumns();
        var known = (DataContext as EntityTabViewModel)?.ApplicationPropertyNames ?? [];
        if (await new ColumnsDialog(_settings.Columns, known).ShowDialog<IReadOnlyList<ColumnSetting>?>(owner) is { } columns)
        {
            _settings.Columns = columns;
        }
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    // Delete removes the selected messages wherever the focus is in the tab (grid, body, properties), except while typing.
    private void OnTabKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Delete
            && e.KeyModifiers == Avalonia.Input.KeyModifiers.None
            && e.Source is not TextBox
            && (e.Source as Avalonia.Visual)?.FindAncestorOfType<AutoCompleteBox>() is null
            && DataContext is EntityTabViewModel viewModel)
        {
            viewModel.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is EntityTabViewModel viewModel && sender is DataGrid grid)
        {
            viewModel.SetSelection(grid.SelectedItems.OfType<MessageRowViewModel>().ToList());
        }
    }
}
