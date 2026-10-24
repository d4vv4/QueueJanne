using QueueJanne.App.ViewModels;
using QueueJanne.App.Views;
using QueueJanne.Core.Connections;
using QueueJanne.Core.Messages;
using static QueueJanne.App.Tests.TestApp;

namespace QueueJanne.App.Tests;

public class ConnectionStateTests
{
    [Fact]
    public Task Saved_connections_stay_disconnected_until_expanded() => Run(async () =>
    {
        var store = new ConnectionStore(Path.Combine(Home, Guid.NewGuid().ToString("N")));
        store.Save(ConnectionProfile.CreateDemo());
        var window = new MainWindow();
        var viewModel = new MainWindowViewModel(store, new FakeDialogs());
        window.DataContext = viewModel;
        window.Show();
        var connection = viewModel.Connections.Single();

        await WaitUntil(() => true, "startup work has run");
        await Task.Delay(300);
        Assert.False(connection.IsConnected);
        Assert.Null(connection.Snapshot);
        Assert.Equal("not connected", connection.CountText);
        Assert.False(connection.IsStateConnected || connection.IsStateConnecting || connection.IsStateFailed);
        Assert.Empty(viewModel.Tabs);

        connection.IsExpanded = true;
        Assert.True(connection.IsStateConnecting);
        await WaitUntil(() => connection.Snapshot is not null, "expanding has connected");

        Assert.True(connection.IsStateConnected);
        Assert.NotNull(connection.FindEntity("orders"));
        window.Close();
    });

    [Fact]
    public Task Disconnect_closes_its_tabs_and_forgets_what_was_loaded() => Run(async () =>
    {
        var (window, viewModel, _) = await OpenDemoAsync();
        var connection = viewModel.Connections[0];
        await OpenTabAsync(viewModel, "orders", SubQueueKind.DeadLetter);
        viewModel.OpenOverview(connection, preview: false);
        viewModel.SelectedNode = connection;

        await viewModel.ToggleConnectedCommand.ExecuteAsync(null);

        Assert.False(connection.IsConnected);
        Assert.Null(connection.Snapshot);
        Assert.Null(connection.FindEntity("orders"));
        Assert.False(connection.IsExpanded);
        Assert.DoesNotContain(viewModel.Tabs, t => t is EntityTabViewModel || t is OverviewViewModel { Connection: not null });

        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.False(connection.IsConnected);

        await viewModel.ToggleConnectedCommand.ExecuteAsync(null);
        Assert.True(connection.IsStateConnected);
        Assert.NotNull(connection.FindEntity("orders"));
        window.Close();
    });

    [Fact]
    public Task Opening_a_connections_overview_connects_it() => Run(async () =>
    {
        var store = new ConnectionStore(Path.Combine(Home, Guid.NewGuid().ToString("N")));
        store.Save(ConnectionProfile.CreateDemo());
        var window = new MainWindow();
        var viewModel = new MainWindowViewModel(store, new FakeDialogs());
        window.DataContext = viewModel;
        window.Show();
        var connection = viewModel.Connections.Single();

        var overview = viewModel.OpenOverview(connection, preview: false);
        await WaitUntil(() => connection.Snapshot is not null && overview.Rows.Count > 0, "the overview has its rows");

        Assert.True(connection.IsExpanded);
        window.Close();
    });

    [Fact]
    public Task The_auto_refresh_interval_is_kept_in_the_settings() => Run(async () =>
    {
        var (window, viewModel, _) = await OpenDemoAsync();
        try
        {
            viewModel.RefreshEvery = RefreshInterval.All.First(i => i.Seconds == 5);

            Assert.Equal(5, viewModel.Settings.AutoRefreshSeconds);
            Assert.Contains("every 5 seconds", viewModel.AutoRefreshTip);
            Assert.Equal(RefreshInterval.DefaultSeconds, RefreshInterval.For(7).Seconds);
            Assert.Equal("every minute", RefreshInterval.For(60).Every);
        }
        finally
        {
            viewModel.RefreshEvery = RefreshInterval.For(RefreshInterval.DefaultSeconds);
            window.Close();
        }
    });
}
