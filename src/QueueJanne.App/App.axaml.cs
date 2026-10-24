using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using QueueJanne.App.Services;
using QueueJanne.App.ViewModels;
using QueueJanne.App.Views;
using QueueJanne.Core.Connections;

namespace QueueJanne.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = new ConnectionStore();
            if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("QUEUEJANNE_HOME")))
            {
                store.MigrateFrom(ConnectionStore.LegacyFolder());
            }

            if (Core.Updates.UpdateInstaller.InstalledLocation() is { } installed)
            {
                Core.Updates.UpdateInstaller.RemoveLeftovers(installed);
            }

            UiLagMonitor.StartIfRequested();
            var window = new MainWindow();
            window.DataContext = new MainWindowViewModel(store, window);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
