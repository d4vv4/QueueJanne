using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using QueueJanne.App.ViewModels;
using QueueJanne.App.Views;
using QueueJanne.Core.Messages;
using static QueueJanne.App.Tests.TestApp;

namespace QueueJanne.App.Tests;

public class MainWindowTests
{
    [Fact]
    public Task Single_clicks_share_one_preview_tab_and_reuse_its_view() => Run(async () =>
    {
        var (window, viewModel, _) = await OpenDemoAsync();

        await OpenTabAsync(viewModel, "orders", SubQueueKind.DeadLetter);
        var view = await ViewOf(window);
        await OpenTabAsync(viewModel, "notifications", SubQueueKind.DeadLetter);

        Assert.Single(viewModel.Tabs, t => t.IsPreview);
        Assert.Equal("notifications · dead-letter", viewModel.SelectedTab!.Header);
        Assert.Same(view, await ViewOf(window));
        window.Close();
    });

    [Fact]
    public Task Loading_every_message_of_a_big_queue_keeps_the_first_one_selected() => Run(async () =>
    {
        var (window, viewModel, _) = await OpenDemoAsync();
        var tab = await OpenTabAsync(viewModel, "audit-log", SubQueueKind.Active);
        Assert.Equal($"{Formatting.Count(1_000)} of {Formatting.Count(15_230)} loaded", tab.CountText);

        tab.Top = tab.TopOptions[^1];
        await WaitUntil(() => !tab.IsLoading && tab.Messages.Count == 15_230, "every message has loaded");

        Assert.Equal($"{Formatting.Count(15_230)} messages", tab.CountText);
        Assert.Equal(48_000, tab.SelectedMessage!.SequenceNumber);
        window.Close();
    });

    [Fact]
    public Task Refresh_keeps_the_selected_message() => Run(async () =>
    {
        var (window, viewModel, _) = await OpenDemoAsync();
        var tab = await OpenTabAsync(viewModel, "invoices", SubQueueKind.DeadLetter);
        var chosen = tab.Messages.Cast<MessageRowViewModel>().ElementAt(7);
        tab.SelectedMessage = chosen;

        await tab.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(chosen.SequenceNumber, tab.SelectedMessage!.SequenceNumber);
        window.Close();
    });

    [Fact]
    public Task Delete_removes_the_row_in_place_selects_the_next_and_journals_it() => Run(async () =>
    {
        var (window, viewModel, dialogs) = await OpenDemoAsync();
        var tab = await OpenTabAsync(viewModel, "notifications", SubQueueKind.DeadLetter);
        var rows = tab.Messages.Cast<MessageRowViewModel>().ToList();
        tab.SelectedMessage = rows[3];

        await tab.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Prompts);
        Assert.Equal(40, tab.Messages.Count);
        Assert.Equal(rows[4].SequenceNumber, tab.SelectedMessage!.SequenceNumber);
        Assert.Equal("Deleted 1", tab.ProgressText);
        Assert.Contains("Dead-letter (40)", tab.DeadLetterLabel);
        var journal = Directory.GetFiles(Path.Combine(Home, "journal"), "*delete-notifications*", SearchOption.AllDirectories)
            .Select(OperationJournal.Load)
            .Single(e => e.Messages.Any(m => m.SequenceNumber == rows[3].SequenceNumber));
        Assert.Equal("Deleted 1", journal.Outcome);
        window.Close();
    });

    [Fact]
    public Task Cancelling_an_operation_changes_nothing() => Run(async () =>
    {
        var (window, viewModel, dialogs) = await OpenDemoAsync();
        dialogs.ConfirmOperations = false;
        var tab = await OpenTabAsync(viewModel, "orders", SubQueueKind.DeadLetter);

        await tab.PurgeCommand.ExecuteAsync(null);

        Assert.Equal(3, tab.Messages.Count);
        Assert.Equal(3, viewModel.Connections[0].FindEntity("orders")!.Entity.Counts.DeadLetter);
        window.Close();
    });

    [Fact]
    public Task Search_matches_sequence_numbers_and_says_when_nothing_matches() => Run(async () =>
    {
        var (window, viewModel, _) = await OpenDemoAsync();
        var tab = await OpenTabAsync(viewModel, "notifications", SubQueueKind.DeadLetter);

        tab.FilterText = "48016";
        await WaitUntil(() => tab.Messages.Count == 1, "the search has applied");
        Assert.Equal("1 shown · 41 messages", tab.CountText);

        tab.FilterText = "nothing like this";
        await WaitUntil(() => tab.Messages.Count == 0, "the search has applied");
        Assert.Equal("No loaded message matches the filter.", tab.EmptyText);
        window.Close();
    });

    [Fact]
    public Task Compressed_bodies_are_shown_unpacked() => Run(async () =>
    {
        var (window, viewModel, _) = await OpenDemoAsync();
        var tab = await OpenTabAsync(viewModel, "notifications", SubQueueKind.DeadLetter);

        tab.SelectedMessage = tab.Messages.Cast<MessageRowViewModel>().Single(r => r.SequenceNumber == 48_016);

        Assert.Contains("gzip", tab.BodyTitle);
        Assert.Equal(Core.Bodies.BodyFormat.Json, tab.BodyFormat);
        Assert.StartsWith("{", tab.BodyText);
        window.Close();
    });

    [Fact]
    public Task Ctrl_F_puts_the_cursor_in_the_search_box() => Run(async () =>
    {
        var (window, viewModel, _) = await OpenDemoAsync();
        await OpenTabAsync(viewModel, "orders", SubQueueKind.DeadLetter);
        var view = await ViewOf(window);

        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");

        Assert.Same(view.FindControl<TextBox>("SearchBox"), window.FocusManager?.GetFocusedElement());
        window.Close();
    });

    [Fact]
    public Task Ctrl_K_opens_the_palette_and_opens_the_chosen_dead_letter_queue() => Run(async () =>
    {
        var (window, viewModel, dialogs) = await OpenDemoAsync();
        dialogs.ChoosePaletteItem = items => CommandPalette.Search(items, "ledger dead").First();

        window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, "k");
        await WaitUntil(() => viewModel.SelectedEntityTab is not null, "a tab has opened");

        Assert.Single(dialogs.Palettes);
        Assert.Equal("billing-events/subscriptions/ledger · dead-letter", viewModel.SelectedTab!.Header);
        window.Close();
    });

    [Fact]
    public Task Edit_and_resend_sends_the_edited_copy_to_the_source_queue() => Run(async () =>
    {
        var (window, viewModel, dialogs) = await OpenDemoAsync();
        var tab = await OpenTabAsync(viewModel, "orders", SubQueueKind.DeadLetter);
        dialogs.FillSend = dialog =>
        {
            Assert.Equal("orders", dialog.Target);
            dialog.Subject = "Retried";
            return true;
        };

        await tab.EditAndResendCommand.ExecuteAsync(null);

        Assert.StartsWith("Sent 1 to 'orders'", tab.ProgressText);
        Assert.Equal(13, viewModel.Connections[0].FindEntity("orders")!.Entity.Counts.Active);
        Assert.Contains("Messages (13)", tab.ActiveLabel);
        window.Close();
    });

    [Fact]
    public Task Moving_messages_removes_them_here_and_adds_them_there() => Run(async () =>
    {
        var (window, viewModel, dialogs) = await OpenDemoAsync();
        var tab = await OpenTabAsync(viewModel, "notifications", SubQueueKind.DeadLetter);
        dialogs.FillSend = dialog =>
        {
            dialog.Target = "orders";
            dialog.Move = true;
            return true;
        };

        await tab.CopyToCommand.ExecuteAsync(null);

        Assert.Equal(40, tab.Messages.Count);
        Assert.Equal(13, viewModel.Connections[0].FindEntity("orders")!.Entity.Counts.Active);
        window.Close();
    });

    [Fact]
    public Task Zoom_is_kept_between_limits() => Run(async () =>
    {
        var (window, viewModel, _) = await OpenDemoAsync();
        var settings = viewModel.Settings;
        try
        {
            for (var i = 0; i < 20; i++)
            {
                settings.ZoomInCommand.Execute(null);
            }

            Assert.Equal(2, settings.Zoom);
            settings.ResetZoomCommand.Execute(null);
            settings.ZoomOutCommand.Execute(null);
            Assert.Equal(0.9, settings.Zoom);
        }
        finally
        {
            settings.ResetZoomCommand.Execute(null);
            window.Close();
        }
    });

    private static async Task<EntityTabView> ViewOf(Window window)
    {
        EntityTabView? view = null;
        await WaitUntil(() => (view = window.GetVisualDescendants().OfType<EntityTabView>().FirstOrDefault()) is not null, "the tab view is shown");
        return view!;
    }
}
