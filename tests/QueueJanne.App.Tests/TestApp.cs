using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using QueueJanne.App;
using QueueJanne.App.ViewModels;
using QueueJanne.App.Views;
using QueueJanne.Core.Connections;

namespace QueueJanne.App.Tests;

/// <summary>Runs the real app headless: the same windows and view models, no screen. All UI work happens on one thread.</summary>
public static class TestApp
{
    /// <summary>A throwaway home folder, so tests never see saved connections or settings.</summary>
    public static string Home { get; } = Path.Combine(Path.GetTempPath(), "qj-apptests-" + Guid.NewGuid().ToString("N"));

    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(TestApp)));

    public static AppBuilder BuildAvaloniaApp()
    {
        Environment.SetEnvironmentVariable("QUEUEJANNE_HOME", Home);
        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
    }

    public static Task Run(Func<Task> test) =>
        Session.Value.Dispatch(async () =>
        {
            await test();
            return true;
        }, TestContext.Current.CancellationToken);

    /// <summary>Lets the UI thread run, with layout and rendering, until the condition holds.</summary>
    public static async Task WaitUntil(Func<bool> condition, string what, int timeoutMilliseconds = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting until {what}.");
            }

            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(10);
        }
    }

    /// <summary>The main window on the demo namespace, with dialogs answered by <see cref="FakeDialogs"/>.</summary>
    public static async Task<(MainWindow Window, MainWindowViewModel ViewModel, FakeDialogs Dialogs)> OpenDemoAsync()
    {
        var store = new ConnectionStore(Path.Combine(Home, Guid.NewGuid().ToString("N")));
        store.Save(ConnectionProfile.CreateDemo());
        var dialogs = new FakeDialogs();
        var window = new MainWindow { Width = 1400, Height = 860 };
        var viewModel = new MainWindowViewModel(store, dialogs);
        window.DataContext = viewModel;
        window.Show();
        await WaitUntil(() => viewModel.Connections.All(c => c.Snapshot is not null), "the demo namespace has loaded");
        return (window, viewModel, dialogs);
    }

    public static async Task<EntityTabViewModel> OpenTabAsync(MainWindowViewModel viewModel, string path, Core.Messages.SubQueueKind subQueue, bool preview = true)
    {
        var entity = viewModel.Connections[0].FindEntity(path) ?? throw new ArgumentException($"No entity {path}.");
        var tab = viewModel.Open(entity, subQueue, preview);
        await WaitUntil(() => !tab.IsLoading && tab.SelectedMessage is not null || tab.EmptyText is not null, $"{path} has loaded");
        return tab;
    }
}
