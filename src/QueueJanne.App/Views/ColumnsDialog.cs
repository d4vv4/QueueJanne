using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using QueueJanne.App.ViewModels;

namespace QueueJanne.App.Views;

/// <summary>Chooses, orders and adds message grid columns. Returns the new column list, or null when cancelled.</summary>
internal sealed class ColumnsDialog : Window
{
    private readonly ObservableCollection<Entry> _entries;
    private readonly ListBox _list;

    public ColumnsDialog(IReadOnlyList<ColumnSetting> columns, IReadOnlyList<string> knownProperties)
    {
        Title = "Columns";
        Width = 480;
        Height = 560;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(BackgroundProperty, this.GetResourceObservable("QjWindowBrush"));

        _entries = new ObservableCollection<Entry>(columns.Select(c => new Entry(c)));
        _list = new ListBox
        {
            ItemsSource = _entries,
            ItemTemplate = new FuncDataTemplate<Entry>((entry, _) =>
            {
                var check = new CheckBox { IsChecked = entry?.Visible ?? false, Content = entry?.Header };
                check.IsCheckedChanged += (_, _) =>
                {
                    if (check.DataContext is Entry current)
                    {
                        current.Visible = check.IsChecked == true;
                    }
                };
                return check;
            }),
        };

        var up = SideButton("Move up", () => Move(-1));
        var down = SideButton("Move down", () => Move(1));
        var remove = SideButton("Remove", RemoveSelected);
        var reset = SideButton("Reset", () => Replace(ColumnCatalog.Defaults));
        _list.SelectionChanged += (_, _) => remove.IsEnabled = (_list.SelectedItem as Entry)?.Definition.IsApplicationProperty == true;
        remove.IsEnabled = false;
        var side = new StackPanel { Spacing = 6, Margin = new Thickness(10, 0, 0, 0), Children = { up, down, remove, reset } };

        var propertyName = new AutoCompleteBox
        {
            ItemsSource = knownProperties,
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            PlaceholderText = knownProperties.Count > 0 ? $"For example {knownProperties[0]}" : "Property name",
            MinimumPrefixLength = 0,
        };
        var add = new Button { Content = "Add", MinWidth = 70, Margin = new Thickness(8, 0, 0, 0) };
        add.Click += (_, _) =>
        {
            AddProperty(propertyName.Text);
            propertyName.Text = string.Empty;
        };
        var addRow = new DockPanel { Children = { add, propertyName } };
        DockPanel.SetDock(add, Dock.Right);

        var ok = new Button { Content = "OK", Classes = { "accent" }, IsDefault = true, MinWidth = 80 };
        ok.Click += (_, _) => Close((IReadOnlyList<ColumnSetting>)_entries.Select(e => e.ToSetting()).ToList());
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } };

        var root = new DockPanel { Margin = new Thickness(20) };
        AddDocked(root, new TextBlock { Text = "Tick the columns to show. Drag column headers in the grid to reorder them too.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }, Dock.Top);
        AddDocked(root, buttons, Dock.Bottom);
        AddDocked(root, new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 12, 0, 16),
            Children = { new TextBlock { Text = "Add an application property column" }, addRow },
        }, Dock.Bottom);
        AddDocked(root, side, Dock.Right);
        root.Children.Add(_list);
        Content = root;
    }

    private static void AddDocked(DockPanel panel, Control child, Dock dock)
    {
        DockPanel.SetDock(child, dock);
        panel.Children.Add(child);
    }

    private static Button SideButton(string text, Action onClick)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void Move(int offset)
    {
        var index = _list.SelectedIndex;
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _entries.Count)
        {
            return;
        }

        _entries.Move(index, target);
        _list.SelectedIndex = target;
    }

    private void RemoveSelected()
    {
        if (_list.SelectedItem is Entry { Definition.IsApplicationProperty: true } entry)
        {
            _entries.Remove(entry);
        }
    }

    private void AddProperty(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var key = ColumnCatalog.PropertyKey(name.Trim());
        var existing = _entries.FirstOrDefault(e => e.Definition.Key == key);
        if (existing is null)
        {
            existing = new Entry(new ColumnSetting(key, 120, true));
            _entries.Add(existing);
        }

        _list.SelectedItem = existing;
        _list.ScrollIntoView(existing);
    }

    private void Replace(IReadOnlyList<ColumnSetting> columns)
    {
        _entries.Clear();
        foreach (var column in columns)
        {
            _entries.Add(new Entry(column));
        }
    }

    private sealed class Entry(ColumnSetting setting)
    {
        public MessageColumn Definition { get; } = ColumnCatalog.Find(setting.Key);

        public string Header => Definition.IsApplicationProperty ? $"{Definition.Header} (application property)" : Definition.Header;

        public bool Visible { get; set; } = setting.Visible;

        public ColumnSetting ToSetting() => setting with { Visible = Visible };
    }
}
