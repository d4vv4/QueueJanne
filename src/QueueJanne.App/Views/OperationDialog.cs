using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using QueueJanne.App.Services;

namespace QueueJanne.App.Views;

/// <summary>
/// Confirms an operation that changes messages: what will happen, warnings worked out from the entity's settings,
/// and options. The text scrolls, so the buttons stay on screen however long it gets.
/// </summary>
internal sealed class OperationDialog : Window
{
    public OperationDialog(OperationPrompt prompt)
    {
        Title = prompt.Title;
        Width = 540;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(BackgroundProperty, this.GetResourceObservable("QjWindowBrush"));

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = prompt.Summary, TextWrapping = TextWrapping.Wrap });
        foreach (var warning in prompt.Warnings)
        {
            var text = new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap };
            text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("QjTestForegroundBrush"));
            var box = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 8), Child = text };
            box.Bind(Border.BackgroundProperty, this.GetResourceObservable("QjTestBackgroundBrush"));
            panel.Children.Add(box);
        }

        var options = prompt.Options
            .Select(o => new CheckBox { IsChecked = o.IsChecked, Content = new TextBlock { Text = o.Label, TextWrapping = TextWrapping.Wrap } })
            .ToList();
        foreach (var option in options)
        {
            panel.Children.Add(option);
        }

        var ok = new Button { Content = prompt.ActionLabel, Classes = { prompt.IsDestructive ? "danger" : "accent" }, MinWidth = 90, IsDefault = true };
        Opened += (_, _) => ok.Focus();

        ok.Click += (_, _) => Close((IReadOnlyList<bool>)options.Select(o => o.IsChecked == true).ToList());
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(20, 0, 20, 20) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        Content = new DockPanel { Children = { buttons, new ScrollViewer { Content = panel } } };
    }
}
