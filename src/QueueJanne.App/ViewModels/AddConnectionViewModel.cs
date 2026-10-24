using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QueueJanne.Core.Connections;
using QueueJanne.Core.ServiceBus;

namespace QueueJanne.App.ViewModels;

/// <summary>What the Add connection dialog returns. The connection string is protected before it is saved.</summary>
public sealed record ConnectionDraft(
    string Name,
    ConnectionAuth Auth,
    string? Namespace,
    string? ConnectionString,
    string? Environment,
    bool ReadOnly,
    bool UseWebSockets,
    string? TenantId);

public sealed partial class AddConnectionViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseConnectionString))]
    private bool _useEntraId = true;

    [ObservableProperty]
    private string? _namespace;

    [ObservableProperty]
    private string? _connectionString;

    [ObservableProperty]
    private string? _tenantId;

    [ObservableProperty]
    private string? _environment;

    [ObservableProperty]
    private bool _readOnly;

    [ObservableProperty]
    private bool _useWebSockets;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string? _testResult;

    [ObservableProperty]
    private bool _isTesting;

    public bool UseConnectionString
    {
        get => !UseEntraId;
        set => UseEntraId = !value;
    }

    public ConnectionDraft? TryCreateDraft()
    {
        Error = Validate();
        if (Error is not null)
        {
            return null;
        }

        var connectionString = UseEntraId ? null : ConnectionString!.Trim();
        return new ConnectionDraft(
            Name.Trim(),
            UseEntraId ? ConnectionAuth.EntraId : ConnectionAuth.ConnectionString,
            UseEntraId ? ConnectionProfile.NormalizeNamespace(Namespace!) : ConnectionProfile.NamespaceFromConnectionString(connectionString!),
            connectionString,
            string.IsNullOrWhiteSpace(Environment) ? null : Environment.Trim(),
            ReadOnly,
            UseWebSockets,
            UseEntraId && !string.IsNullOrWhiteSpace(TenantId) ? TenantId.Trim() : null);
    }

    // A production tag turns read-only on by default; people can still turn it off.
    partial void OnEnvironmentChanged(string? value)
    {
        if (EnvironmentClassifier.Classify(value) == EnvironmentKind.Production)
        {
            ReadOnly = true;
        }
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        TestResult = null;
        var draft = TryCreateDraft();
        if (draft is null)
        {
            return;
        }

        IsTesting = true;
        try
        {
            await using var client = draft.Auth == ConnectionAuth.EntraId
                ? ServiceBusNamespaceClient.FromCredential(draft.Namespace!, AzureCredentials.Get(allowInteractive: true, draft.TenantId), draft.UseWebSockets)
                : ServiceBusNamespaceClient.FromConnectionString(draft.ConnectionString!, draft.UseWebSockets);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var snapshot = await client.GetSnapshotAsync(timeout.Token);
            TestResult = $"Connected. {snapshot.Queues.Count} queues, {snapshot.Topics.Count} topics, "
                + $"{snapshot.Topics.Sum(t => t.Subscriptions.Count)} subscriptions, {Formatting.Count(snapshot.TotalDeadLetters)} dead letters.";
        }
        catch (Exception ex)
        {
            Error = $"Couldn't connect. {ConnectionErrors.Explain(ex)}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    private string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return "Enter a name for the connection.";
        }

        if (UseEntraId && string.IsNullOrWhiteSpace(Namespace))
        {
            return "Enter the namespace, for example my-namespace.";
        }

        if (!UseEntraId && (string.IsNullOrWhiteSpace(ConnectionString) || ConnectionProfile.NamespaceFromConnectionString(ConnectionString) is null))
        {
            return "Paste a connection string that starts with Endpoint=sb://.";
        }

        return null;
    }
}
