// Renders the main window with the demo namespace to PNG files, without showing a window.
// Usage: dotnet run --project tools/QueueJanne.Snapshot -- [output folder]
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using QueueJanne.App;
using QueueJanne.App.Services;
using QueueJanne.App.ViewModels;
using QueueJanne.App.Views;
using QueueJanne.Core.Connections;
using QueueJanne.Core.Messages;

var outputFolder = Path.GetFullPath(args.Length > 0 ? args[0] : "snapshots");
Directory.CreateDirectory(outputFolder);
var home = Path.Combine(Path.GetTempPath(), "queuejanne-snapshot-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("QUEUEJANNE_HOME", home);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var store = new ConnectionStore(home);
store.Save(ConnectionProfile.CreateDemo());

var window = new MainWindow { Width = 1440, Height = 880 };
var viewModel = new MainWindowViewModel(store, window);
window.DataContext = viewModel;
window.Show();

Pump(() => viewModel.Connections.All(c => c.Snapshot is not null));
var connection = viewModel.Connections[0];
connection.FindEntity("billing-events")!.IsExpanded = true;
var ledger = connection.FindEntity("billing-events/subscriptions/ledger")!;
ledger.IsExpanded = true;
viewModel.SelectedNode = connection.FindEntity("orders")!.MessagesNode;
viewModel.SelectedNode = ledger.DeadLetterNode;
if (viewModel.Tabs.Count(t => t.IsPreview) != 1 || viewModel.SelectedTab is not EntityTabViewModel { IsPreview: true } tab)
{
    throw new InvalidOperationException($"Single clicks should share one preview tab, found {viewModel.Tabs.Count(t => t.IsPreview)}.");
}

Pump(() => !tab.IsLoading && tab.SelectedMessage is not null);
Settle();
Save(window, Path.Combine(outputFolder, "main-light.png"));

ViewSettings.Current.ShowProperties = false;
Settle();
Save(window, Path.Combine(outputFolder, "main-no-properties.png"));
ViewSettings.Current.ShowProperties = true;

var defaultColumns = ViewSettings.Current.Columns;
ViewSettings.Current.Columns =
[
    .. defaultColumns.Take(3),
    new ColumnSetting(ColumnCatalog.PropertyKey("tenant"), 90, true),
    new ColumnSetting(ColumnCatalog.PropertyKey("region"), 80, true),
    .. defaultColumns.Skip(3).Select(c => c.Key is "Size" or "MessageId" ? c with { Visible = false } : c),
];
Settle();
Save(window, Path.Combine(outputFolder, "main-columns.png"));

var columnsDialog = new ColumnsDialog(ViewSettings.Current.Columns, tab.ApplicationPropertyNames);
columnsDialog.Show();
Settle();
Save(columnsDialog, Path.Combine(outputFolder, "columns-dialog.png"));
ViewSettings.Current.Columns = defaultColumns;

tab.ReasonFilter = "MaxDeliveryCountExceeded";
tab.GroupBy = GroupOption.All.First(g => g.Kind == GroupKind.Subject);
Settle();
Save(window, Path.Combine(outputFolder, "filter-group.png"));
tab.GroupBy = GroupOption.All[0];
tab.ClearFiltersCommand.Execute(null);

Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
Settle();
Save(window, Path.Combine(outputFolder, "main-dark.png"));

Application.Current.RequestedThemeVariant = ThemeVariant.Light;
var dialog = new AddConnectionWindow
{
    DataContext = new AddConnectionViewModel { Name = "Orders (test)", Namespace = "queuejanne-test", Environment = "test" },
};
dialog.Show();
Settle();
Save(dialog, Path.Combine(outputFolder, "add-connection.png"));

var ledgerDeadLetters = EntityAddress.Subscription("billing-events", "ledger", SubQueueKind.DeadLetter);
var resubmit = new OperationDialog(new OperationPrompt(
    "Resubmit messages",
    "Resubmit",
    "Resubmit 3 messages from billing-events/subscriptions/ledger (dead-letter) to 'billing-events'? Each copy is sent first, and the original is removed from the dead-letter queue only after that.",
    ResubmitAdvice.Warnings(connection.Snapshot!, ledgerDeadLetters, newMessageId: false),
    [new OperationOption("Give each copy a new message ID (the old one is kept in the OriginalMessageId property)", false)],
    ConfirmText: "billing-events/subscriptions/ledger"));
resubmit.Show();
Settle();
Save(resubmit, Path.Combine(outputFolder, "resubmit.png"));

var ledgerInfo = ledger.Entity;
var purge = new OperationDialog(new OperationPrompt(
    "Purge",
    "Purge",
    $"Remove every message from {ledgerInfo.Path}? Choose what to empty. This can't be undone.",
    DeleteAdvice.ForPurge(ledgerInfo, [SubQueueKind.Active, SubQueueKind.DeadLetter]),
    [
        new OperationOption($"Active messages ({ledgerInfo.Counts.Active})", false),
        new OperationOption($"Dead-letter queue ({ledgerInfo.Counts.DeadLetter})", true),
        new OperationOption($"Transfer dead-letter queue ({ledgerInfo.Counts.TransferDeadLetter})", false),
    ],
    ConfirmText: null,
    IsDestructive: true));
purge.Show();
Settle();
Save(purge, Path.Combine(outputFolder, "purge.png"));

var changeCounts = connection.Client.ResubmitAsync(ledgerDeadLetters, new ResubmitRequest { MaxMessages = 5 });
Pump(() => changeCounts.IsCompleted);
viewModel.AutoRefresh = true;
var refresh = viewModel.RefreshCommand.ExecuteAsync(null);
Pump(() => refresh.IsCompleted);
var pulseUntil = DateTime.UtcNow.AddMilliseconds(250);
Pump(() => DateTime.UtcNow > pulseUntil);
Save(window, Path.Combine(outputFolder, "pulse.png"));
viewModel.AutoRefresh = false;

viewModel.SelectedTab = viewModel.Tabs.OfType<OverviewViewModel>().First(o => o.Connection is null);
Settle();
Save(window, Path.Combine(outputFolder, "overview.png"));

Console.WriteLine($"Saved snapshots to {outputFolder}");

static void Pump(Func<bool> done, TimeSpan? timeout = null)
{
    var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
    while (!done())
    {
        if (DateTime.UtcNow > deadline)
        {
            throw new TimeoutException("The window didn't reach the expected state.");
        }

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(10);
    }
}

static void Settle()
{
    var until = DateTime.UtcNow.AddMilliseconds(400);
    Pump(() => DateTime.UtcNow > until);
}

static void Save(Window window, string path)
{
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was rendered.");
    frame.Save(path, PngBitmapEncoderOptions.Default);
}
