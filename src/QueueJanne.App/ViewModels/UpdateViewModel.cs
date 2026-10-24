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

    /// <summary>Download progress, 0 to 100, shown in the status bar while an update installs.</summary>
    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalling))]
    private string? _progressText;

    public bool IsInstalling => ProgressText is not null;

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

        var shortNotes = Shorten(update.Notes);
        var notes = shortNotes.Length == 0 ? string.Empty : "\n\nWhat's new:\n" + shortNotes;
        if (!await dialogs.ConfirmAsync(
                "Install update",
                $"Install QueueJanne {update.Version}? You have {CurrentVersion}. QueueJanne downloads it ({Formatting.Bytes(update.Size)}), replaces the program in {location} and restarts. Your connections and settings stay as they are.{notes}"))
        {
            return;
        }

        IsBusy = true;
        var step = "download it";
        try
        {
            Progress = 0;
            ProgressText = $"Downloading {update.Version}…";
            var zip = await UpdateInstaller.DownloadAsync(Http, update, new Progress<double>(done =>
            {
                Progress = done * 100;
                ProgressText = $"Downloading {update.Version}… {done:P0}";
                report($"Downloading QueueJanne {update.Version}: {Formatting.Bytes((long)(done * update.Size))} of {Formatting.Bytes(update.Size)}");
            }));

            step = "prepare the restart";
            var restart = PrepareRestart(location);

            step = "replace the program files";
            Progress = 100;
            ProgressText = "Installing…";
            report("Installing the update…");
            await Task.Run(() => UpdateInstaller.Install(zip, location));
            TryDelete(zip);

            step = "restart QueueJanne";
            ProgressText = "Restarting…";
            report($"Installed {update.Version}. Restarting…");
            Process.Start(restart);
            exit();
        }
        catch (Exception ex)
        {
            UiLagMonitor.Note($"update failed while trying to {step}: {ex}");
            report("Couldn't install the update");
            var detail = ex is InvalidDataException ? ex.Message : $"{ex.GetType().Name}: {(string.IsNullOrWhiteSpace(ex.Message) ? "no details" : ex.Message)}";
            await dialogs.ShowMessageAsync(
                "Install update",
                $"Couldn't {step}. {detail}\n\nYou can download {update.Version} yourself from {update.PageUrl}.");
        }
        finally
        {
            IsBusy = false;
            ProgressText = null;
        }
    }

    private static async Task<UpdateInfo?> FindNewerAsync()
    {
        var latest = await new UpdateChecker(Http, UpdateChecker.DefaultRepository!).GetLatestAsync();
        return latest is not null && latest.Version > CurrentVersion ? latest : null;
    }

    /// <summary>
    /// QueueJanne is one file that .NET reads parts of only when they're first used. Once the update has replaced that
    /// file, this process can't load anything it hasn't used yet, so starting a process is tried out first, while the
    /// old file is still in place.
    /// </summary>
    private static ProcessStartInfo PrepareRestart(string location)
    {
        var probe = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", ["/c", "exit"])
            : new ProcessStartInfo("/usr/bin/true");
        probe.CreateNoWindow = true;
        probe.UseShellExecute = false;
        using (var started = Process.Start(probe))
        {
            started?.WaitForExit();
        }

        return location.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("open", ["-n", location])
            : new ProcessStartInfo(Path.Combine(location, Path.GetFileName(Environment.ProcessPath!)));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void OpenPage(string url)
    {
        if (url.StartsWith("https://", StringComparison.Ordinal))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    /// <summary>The first lines of GitHub's release notes as plain text, without Markdown marks or the changelog link.</summary>
    internal static string Shorten(string notes)
    {
        var lines = notes.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')
            .Select(l => l.Trim().TrimStart('#').Trim().Replace("**", string.Empty, StringComparison.Ordinal))
            .Where(l => l.Length > 0 && !l.StartsWith("Full Changelog", StringComparison.OrdinalIgnoreCase) && !l.Equals("What's Changed", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.StartsWith("* ", StringComparison.Ordinal) ? "• " + l[2..] : l)
            .Take(8)
            .ToList();
        return string.Join('\n', lines);
    }
}
