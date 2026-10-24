using Avalonia.Controls;
using Avalonia.Interactivity;
using QueueJanne.App.ViewModels;

namespace QueueJanne.App.Views;

/// <summary>Sends a new or edited message, or copies or moves messages. Closes with the messages to send, or null.</summary>
public partial class SendMessageWindow : Window
{
    public SendMessageWindow() => InitializeComponent();

    private void OnSend(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SendMessageViewModel viewModel && viewModel.Validate() is { } messages)
        {
            Close(messages);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
