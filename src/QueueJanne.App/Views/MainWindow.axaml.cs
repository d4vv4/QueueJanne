using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using QueueJanne.App.Services;
using QueueJanne.App.ViewModels;
using QueueJanne.Core.Messages;

namespace QueueJanne.App.Views;

public partial class MainWindow : Window, IDialogService
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    // Shortcuts that move focus are handled here, before focused text boxes or grids see the keys.
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control && e.KeyModifiers != KeyModifiers.Meta)
        {
            return;
        }

        if (e.Key == Key.F && this.GetVisualDescendants().OfType<EntityTabView>().FirstOrDefault() is { } tab)
        {
            tab.FocusSearch();
            e.Handled = true;
        }
    }

    public Task<ConnectionDraft?> ShowAddConnectionAsync() =>
        new AddConnectionWindow { DataContext = new AddConnectionViewModel() }.ShowDialog<ConnectionDraft?>(this);

    public Task<bool> ConfirmAsync(string title, string message) =>
        new MessageDialog(title, message, withCancel: true).ShowDialog<bool>(this);

    public Task ShowMessageAsync(string title, string message) =>
        new MessageDialog(title, message, withCancel: false).ShowDialog<bool>(this);

    public Task<IReadOnlyList<bool>?> ConfirmOperationAsync(OperationPrompt prompt) =>
        new OperationDialog(prompt).ShowDialog<IReadOnlyList<bool>?>(this);

    public Task<IReadOnlyList<OutgoingMessage>?> ShowSendAsync(SendMessageViewModel dialog) =>
        new SendMessageWindow { DataContext = dialog }.ShowDialog<IReadOnlyList<OutgoingMessage>?>(this);

    public async Task OpenFolderAsync(string path)
    {
        Directory.CreateDirectory(path);
        await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }

    public Task<PaletteItem?> ShowPaletteAsync(IReadOnlyList<PaletteItem> items) =>
        new CommandPaletteWindow(items).ShowDialog<PaletteItem?>(this);

    public async Task CopyToClipboardAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    public async Task<bool> SaveTextFileAsync(string suggestedFileName, string content)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export messages",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (file is null)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
        return true;
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e) =>
        (DataContext as MainWindowViewModel)?.ActivateSelectedNode();

    // A click anywhere on a row opens or closes it, not only on the arrow. A double-click toggles twice, so it ends where it started.
    private void OnTreeRowTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        // Clicking the sub-queue that's already selected doesn't change the selection, so show its tab here; otherwise,
        // after looking at another tab, you'd have to click somewhere else and back.
        if ((sender as Control)?.DataContext is SubQueueNodeViewModel subQueue && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Open(subQueue.Owner, subQueue.SubQueue, preview: true);
            return;
        }

        if ((sender as Control)?.DataContext is TreeNodeViewModel { Children.Count: > 0 } node)
        {
            node.IsExpanded = !node.IsExpanded;
        }
    }

    private void OnConnectionOverviewClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ConnectionNodeViewModel connection && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OpenOverview(connection, preview: false);
        }
    }

    private void OnTabHeaderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DocumentViewModel tab)
        {
            tab.IsPreview = false;
        }
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ActivateSelectedNode();
            e.Handled = true;
        }
    }
}
