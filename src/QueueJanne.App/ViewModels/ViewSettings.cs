using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QueueJanne.Core.Connections;

namespace QueueJanne.App.ViewModels;

/// <summary>Layout choices shared by every tab and kept between runs in <c>settings.json</c> next to the connections.</summary>
public sealed partial class ViewSettings : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string? _path;

    [ObservableProperty]
    private bool _showProperties = true;

    [ObservableProperty]
    private double _propertiesWidth = 380;

    [ObservableProperty]
    private double _detailsHeight = 300;

    [ObservableProperty]
    private IReadOnlyList<ColumnSetting> _columns = ColumnCatalog.Defaults;

    /// <summary>Scale of the whole main window, changed with Ctrl+plus, Ctrl+minus and Ctrl+0.</summary>
    [ObservableProperty]
    private double _zoom = 1;

    /// <summary>Whether the app looks for a newer release when it starts and every few hours while it runs.</summary>
    [ObservableProperty]
    private bool _checkForUpdatesAutomatically = true;

    /// <summary>How often auto-refresh reloads counts, one of <see cref="RefreshInterval.All"/>.</summary>
    [ObservableProperty]
    private int _autoRefreshSeconds = RefreshInterval.DefaultSeconds;

    public ViewSettings()
    {
    }

    private ViewSettings(string path) => _path = path;

    public static ViewSettings Current { get; } = Loaded(Load(Path.Combine(ConnectionStore.DefaultFolder(), "settings.json")));

    private bool _loaded;

    private static ViewSettings Loaded(ViewSettings settings)
    {
        settings._loaded = true;
        return settings;
    }

    [RelayCommand]
    private void HideProperties() => ShowProperties = false;

    [RelayCommand]
    private void ToggleProperties() => ShowProperties = !ShowProperties;

    partial void OnShowPropertiesChanged(bool value) => Save();

    partial void OnPropertiesWidthChanged(double value) => Save();

    partial void OnDetailsHeightChanged(double value) => Save();

    partial void OnColumnsChanged(IReadOnlyList<ColumnSetting> value) => Save();

    partial void OnZoomChanged(double value) => Save();

    partial void OnCheckForUpdatesAutomaticallyChanged(bool value) => Save();

    partial void OnAutoRefreshSecondsChanged(int value) => Save();

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(Math.Round(Zoom + 0.1, 1), MaxZoom);

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(Math.Round(Zoom - 0.1, 1), MinZoom);

    [RelayCommand]
    private void ResetZoom() => Zoom = 1;

    private const double MinZoom = 0.7;
    private const double MaxZoom = 2;

    private static ViewSettings Load(string path)
    {
        var settings = new ViewSettings();
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) is { } stored)
            {
                settings.ShowProperties = stored.ShowProperties;
                settings.PropertiesWidth = Math.Clamp(stored.PropertiesWidth, 150, 2000);
                settings.DetailsHeight = Math.Clamp(stored.DetailsHeight, 100, 2000);
                settings.Columns = ColumnCatalog.Normalize(stored.Columns);
                settings.Zoom = Math.Clamp(stored.Zoom, MinZoom, MaxZoom);
                settings.CheckForUpdatesAutomatically = stored.CheckForUpdatesAutomatically;
                settings.AutoRefreshSeconds = RefreshInterval.For(stored.AutoRefreshSeconds).Seconds;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new ViewSettings(path)
        {
            ShowProperties = settings.ShowProperties,
            PropertiesWidth = settings.PropertiesWidth,
            DetailsHeight = settings.DetailsHeight,
            Columns = settings.Columns,
            Zoom = settings.Zoom,
            CheckForUpdatesAutomatically = settings.CheckForUpdatesAutomatically,
            AutoRefreshSeconds = settings.AutoRefreshSeconds,
        };
    }

    private void Save()
    {
        if (_path is null || !_loaded)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new Stored(ShowProperties, PropertiesWidth, DetailsHeight, Columns.ToList(), Zoom, CheckForUpdatesAutomatically, AutoRefreshSeconds), JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Stored(bool ShowProperties, double PropertiesWidth, double DetailsHeight, List<ColumnSetting>? Columns, double Zoom = 1, bool CheckForUpdatesAutomatically = true, int AutoRefreshSeconds = RefreshInterval.DefaultSeconds);
}
