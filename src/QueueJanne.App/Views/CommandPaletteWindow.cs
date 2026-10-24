using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using QueueJanne.App.ViewModels;

namespace QueueJanne.App.Views;

/// <summary>Ctrl+K: type part of a queue or subscription name, in any connection, and press Enter to open it.</summary>
internal sealed class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<PaletteItem> _items;
    private readonly TextBox _query;
    private readonly ListBox _results;

    public CommandPaletteWindow(IReadOnlyList<PaletteItem> items)
    {
        _items = items;
        Title = "Go to";
        Width = 560;
        Height = 420;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(BackgroundProperty, this.GetResourceObservable("QjWindowBrush"));

        _query = new TextBox { PlaceholderText = "Queue, subscription or connection…", Margin = new Thickness(10, 10, 10, 6) };
        AutomationPropertiesName(_query, "Go to");
        _results = new ListBox
        {
            Margin = new Thickness(10, 0, 10, 10),
            ItemTemplate = new FuncDataTemplate<PaletteItem>((item, _) => new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    new TextBlock { Text = item?.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = item?.Detail, Classes = { "muted" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            }),
        };
        AutomationPropertiesName(_results, "Results");

        var hint = new TextBlock
        {
            Text = "↑↓ to choose · Enter to open · Esc to close",
            Classes = { "muted" },
            FontSize = 11,
            Margin = new Thickness(12, 0, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var layout = new DockPanel();
        DockPanel.SetDock(_query, Dock.Top);
        DockPanel.SetDock(hint, Dock.Bottom);
        layout.Children.Add(_query);
        layout.Children.Add(hint);
        layout.Children.Add(_results);
        Content = layout;

        _query.TextChanged += (_, _) => Update();
        _query.KeyDown += OnQueryKeyDown;
        _results.DoubleTapped += (_, _) => Choose();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
            }
        };
        Opened += (_, _) => _query.Focus();
        Update();
    }

    private static void AutomationPropertiesName(Control control, string name) =>
        Avalonia.Automation.AutomationProperties.SetName(control, name);

    private void Update()
    {
        var results = CommandPalette.Search(_items, _query.Text);
        _results.ItemsSource = results;
        _results.SelectedIndex = results.Count > 0 ? 0 : -1;
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        var count = _results.ItemCount;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                _results.SelectedIndex = Math.Min(_results.SelectedIndex + 1, count - 1);
                _results.ScrollIntoView(_results.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                _results.SelectedIndex = Math.Max(_results.SelectedIndex - 1, 0);
                _results.ScrollIntoView(_results.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Enter:
                Choose();
                e.Handled = true;
                break;
        }
    }

    private void Choose()
    {
        if (_results.SelectedItem is PaletteItem item)
        {
            Close(item);
        }
    }
}
