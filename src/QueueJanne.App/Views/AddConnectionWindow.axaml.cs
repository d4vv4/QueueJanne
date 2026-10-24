using Avalonia.Controls;
using Avalonia.Interactivity;
using QueueJanne.App.ViewModels;

namespace QueueJanne.App.Views;

public partial class AddConnectionWindow : Window
{
    public AddConnectionWindow() => InitializeComponent();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AddConnectionViewModel viewModel && viewModel.TryCreateDraft() is { } draft)
        {
            Close(draft);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
