using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QueueJanne.App.Services;
using QueueJanne.Core.ServiceBus;
using QueueJanne.Core.Updates;

namespace QueueJanne.App.ViewModels;

/// <summary>
/// Check for updates: finds the newest build in the project's GitHub releases, downloads it, checks its checksum,
/// puts it in place of the installed files and restarts QueueJanne.
/// </summary>
public sealed partial class UpdateViewModel(IDialogService dialogs, Action<string> report, Action exit) : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateText))]
    private UpdateInfo? _available;

    [ObservableProperty]
    private bool _isBusy;

    public static Version CurrentVersion { get; } =
        UpdateChecker.Normalize(typeof(UpdateViewModel).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public bool HasUpdate => Available is not null;

    public string UpdateText => Available is { } update ? $"Update to {update.Version}" : string.Empty;

    /// <summary>Runs once after startup when it's switched on; quiet unless it finds a newer version.</summary>
    public async Task CheckQuietlyAsync()
    {
        if (!ViewSettings.Current.CheckForUpdatesAtStartup || UpdateChecker.DefaultRepository is null || UpdateInstaller.InstalledLocation() is null)
        {
            return;
        }

        try
        {
            Available = await FindNewerAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
        }
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (UpdateChecker.DefaultRepository is null)
        {
            await dialogs.ShowMessageAsync("Check for updates", "This build doesn't know where its updates are published.");
            return;
        }

        IsBusy = true;
        try
        {
            report("Checking for updates…");
            Available = await FindNewerAsync();
            if (Available is null)
            {
                report($"QueueJanne {CurrentVersion} is the latest version.");
                await dialogs.ShowMessageAsync("Check for updates", $"You have the latest version, {CurrentVersion}.");
                return;
            }
        }
        catch (Exception ex)
        {
            report("Couldn't check for updates");
            await dialogs.ShowMessageAsync("Check for updates", "Couldn't reach GitHub to check for updates: " + ConnectionErrors.Explain(ex));
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await InstallAsync();
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Available is not { } update || IsBusy)
        {
            return;
        }

        var location = UpdateInstaller.InstalledLocation();
        if (location is null || update.DownloadUrl is null)
        {
            var why = location is null
                ? "This copy of QueueJanne runs from a development build, so it can't update itself."
                : "This release has no download for this computer.";
            if (await dialogs.ConfirmAsync("Update available", $"QueueJanne {update.Version} is available (you have {CurrentVersion}). {why} Open the release page to download it?"))
            {
                OpenPage(update.PageUrl);
            }

            return;
        }

        var notes = string.IsNullOrWhiteSpace(update.Notes) ? string.Empty : "\n\nWhat's new:\n" + Shorten(update.Notes);
        if (!await dialogs.ConfirmAsync(
                "Install update",
                $"Install QueueJanne {update.Version}? You have {CurrentVersion}. QueueJanne downloads it ({Formatting.Bytes(update.Size)}), replaces the program in {location} and restarts. Your connections and settings stay as they are.{notes}"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var zip = await UpdateInstaller.DownloadAsync(Http, update, new Progress<double>(done => report($"Downloading QueueJanne {update.Version}… {done:P0}")));
            report("Installing the update…");
            await Task.Run(() => UpdateInstaller.Install(zip, location));
            File.Delete(zip);
            report($"Installed {update.Version}. Restarting…");
            Restart(location);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            report("Couldn't install the update");
            await dialogs.ShowMessageAsync("Install update", $"Couldn't replace the files in {location}: {ex.Message}\n\nYou can download {update.Version} from {update.PageUrl}.");
        }
        catch (Exception ex)
        {
            report("Couldn't install the update");
            await dialogs.ShowMessageAsync("Install update", ex is InvalidDataException ? ex.Message : "The download failed: " + ConnectionErrors.Explain(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static async Task<UpdateInfo?> FindNewerAsync()
    {
        var latest = await new UpdateChecker(Http, UpdateChecker.DefaultRepository!).GetLatestAsync();
        return latest is not null && latest.Version > CurrentVersion ? latest : null;
    }

    private void Restart(string location)
    {
        var program = location.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("open", ["-n", location])
            : new ProcessStartInfo(Path.Combine(location, Path.GetFileName(Environment.ProcessPath!)));
        Process.Start(program);
        exit();
    }

    private static void OpenPage(string url)
    {
        if (url.StartsWith("https://", StringComparison.Ordinal))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    private static string Shorten(string notes)
    {
        var lines = notes.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(8).ToList();
        return string.Join('\n', lines);
    }
}
