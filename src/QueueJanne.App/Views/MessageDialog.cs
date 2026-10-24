using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace QueueJanne.App.Views;

/// <summary>A small message or confirmation box. Returns true for OK.</summary>
internal sealed class MessageDialog : Window
{
    public MessageDialog(string title, string message, bool withCancel)
    {
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(BackgroundProperty, this.GetResourceObservable("QjWindowBrush"));

        var ok = new Button { Content = withCancel ? "OK" : "Close", IsDefault = true, MinWidth = 80 };
        ok.Click += (_, _) => Close(true);
        Opened += (_, _) => ok.Focus();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(ok);
        if (withCancel)
        {
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
            cancel.Click += (_, _) => Close(false);
            buttons.Children.Add(cancel);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                buttons,
            },
        };
    }
}
