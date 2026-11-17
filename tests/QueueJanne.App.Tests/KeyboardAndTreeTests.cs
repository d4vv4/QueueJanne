using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using QueueJanne.App.Services;
using QueueJanne.App.ViewModels;
using QueueJanne.App.Views;
using QueueJanne.Core.Messages;
using static QueueJanne.App.Tests.TestApp;

namespace QueueJanne.App.Tests;

public class KeyboardAndTreeTests
{
    [Fact]
    public Task Delete_key_in_the_grid_asks_to_delete_the_selected_message() => Run(async () =>
    {
        var (window, viewModel, dialogs) = await OpenDemoAsync();
        dialogs.ConfirmOperations = false;
        await OpenTabAsync(viewModel, "orders", SubQueueKind.DeadLetter);
        DataGrid? grid = null;
        await WaitUntil(() => (grid = window.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault(g => g.Name == "MessageGrid")) is not null, "the grid is shown");
        grid!.Focus();

        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        await WaitUntil(() => dialogs.Prompts.Count == 1, "the delete prompt has opened");

        Assert.Equal("Delete messages", dialogs.Prompts[0].Title);
        window.Close();
    });

    [Fact]
    public Task Delete_key_while_typing_in_search_doesnt_delete() => Run(async () =>
    {
        var (window, viewModel, dialogs) = await OpenDemoAsync();
        await OpenTabAsync(viewModel, "orders", SubQueueKind.DeadLetter);
        var view = window.GetVisualDescendants().OfType<EntityTabView>().First();
        view.FocusSearch();

        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        await WaitUntil(() => true, "keys are handled");

        Assert.Empty(dialogs.Prompts);
        window.Close();
    });

    [Fact]
    public Task A_long_warning_scrolls_and_keeps_the_buttons_on_screen() => Run(async () =>
    {
        var owner = new Window();
        owner.Show();
        var warning = string.Join(", ", Enumerable.Range(0, 400).Select(i => $"subscription-{i}"));
        var dialog = new OperationDialog(new OperationPrompt("Resubmit messages", "Resubmit", "Resubmit 1 message?", [warning], [], IsDestructive: false));
        _ = dialog.ShowDialog<IReadOnlyList<bool>?>(owner);
        await WaitUntil(() => dialog.IsVisible, "the dialog is open");

        var ok = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Resubmit");
        var bottom = ok.TranslatePoint(new Point(0, ok.Bounds.Height), dialog)!.Value.Y;
        Assert.True(dialog.Bounds.Height <= 640, $"the dialog is {dialog.Bounds.Height} high");
        Assert.True(bottom <= dialog.Bounds.Height, $"the button ends at {bottom}, the dialog at {dialog.Bounds.Height}");
        dialog.Close();
        owner.Close();
    });

    [Fact]
    public Task Enter_confirms_a_delete_and_Escape_cancels() => Run(async () =>
    {
        var owner = new Window();
        owner.Show();
        var prompt = new OperationPrompt("Delete messages", "Delete", "Delete 1 message?", [], [], IsDestructive: true);

        var confirmed = new OperationDialog(prompt);
        var result = confirmed.ShowDialog<IReadOnlyList<bool>?>(owner);
        await WaitUntil(() => confirmed.IsActive || confirmed.IsVisible, "the dialog is open");
        confirmed.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await WaitUntil(() => result.IsCompleted, "Enter has closed the dialog");
        Assert.NotNull(await result);

        var cancelled = new OperationDialog(prompt);
        result = cancelled.ShowDialog<IReadOnlyList<bool>?>(owner);
        await WaitUntil(() => cancelled.IsVisible, "the dialog is open");
        cancelled.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await WaitUntil(() => result.IsCompleted, "Esc has closed the dialog");
        Assert.Null(await result);
        owner.Close();
    });

    [Fact]
    public Task Selecting_a_connection_doesnt_open_its_overview() => Run(async () =>
    {
        var (window, viewModel, _) = await OpenDemoAsync();
        var tabs = viewModel.Tabs.ToList();

        viewModel.SelectedNode = viewModel.Connections[0];

        Assert.Equal(tabs, viewModel.Tabs);
        Assert.True(viewModel.Connections[0].IsConnection);
        window.Close();
    });

    [Fact]
    public Task Transfer_dead_letter_nodes_only_show_when_they_hold_messages() => Run(async () =>
    {
        var (window, viewModel, _) = await OpenDemoAsync();
        var connection = viewModel.Connections[0];

        Assert.Equal(["Messages", "Dead-letter"], connection.FindEntity("orders")!.Children.Select(c => c.Title));
        Assert.Equal(["Messages", "Dead-letter", "Transfer dead-letter"], connection.FindEntity("inventory-events/subscriptions/search-index")!.Children.Select(c => c.Title));

        var orders = await OpenTabAsync(viewModel, "orders", SubQueueKind.DeadLetter);
        Assert.False(orders.ShowsTransferDeadLetter);
        window.Close();
    });
}
