using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace QueueJanne.Core.Updates;

/// <summary>A published build: its version, release notes and the download for this computer.</summary>
/// <param name="DownloadUrl">The zip for this operating system and processor, or null when the release has none.</param>
/// <param name="ChecksumUrl">The zip's SHA-256 checksum file, when the release has one.</param>
public sealed record UpdateInfo(Version Version, string PageUrl, string Notes, string? DownloadUrl, string? ChecksumUrl, long Size);

/// <summary>
/// Finds the newest build in the GitHub releases of the repository QueueJanne was built from. Every push to main
/// publishes one, named <c>v{major}.{minor}.{run}</c>, with a zip per platform and a <c>.sha256</c> next to it.
/// </summary>
public sealed class UpdateChecker(HttpClient http, string repository)
{
    /// <summary>The <c>owner/name</c> GitHub repository, from the <c>UpdateRepository</c> assembly metadata set at build time.</summary>
    public static string? DefaultRepository =>
        typeof(UpdateChecker).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdateRepository")?.Value;

    /// <summary>The start of this platform's zip name, or null where QueueJanne isn't packaged.</summary>
    public static string? AssetPrefix =>
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "QueueJanne-windows-x64-"
        : OperatingSystem.IsMacOS() ? $"QueueJanne-macos-{(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64")}-"
        : null;

    /// <summary>Major, minor and build of a version, so 0.6.12 and 0.6.12.0 compare equal.</summary>
    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    /// <summary>The latest release, or null when the repository has none yet.</summary>
    public async Task<UpdateInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("QueueJanne", "1"));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
        {
            return null;
        }

        var assets = root.TryGetProperty("assets", out var list) ? list.EnumerateArray().ToList() : [];
        var zip = AssetPrefix is { } prefix
            ? assets.FirstOrDefault(a => Name(a).StartsWith(prefix, StringComparison.Ordinal) && Name(a).EndsWith(".zip", StringComparison.Ordinal))
            : default;
        var checksum = zip.ValueKind == JsonValueKind.Object
            ? assets.FirstOrDefault(a => Name(a) == Name(zip) + ".sha256")
            : default;

        return new UpdateInfo(
            Normalize(version),
            root.TryGetProperty("html_url", out var page) ? page.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            zip.ValueKind == JsonValueKind.Object ? zip.GetProperty("browser_download_url").GetString() : null,
            checksum.ValueKind == JsonValueKind.Object ? checksum.GetProperty("browser_download_url").GetString() : null,
            zip.ValueKind == JsonValueKind.Object && zip.TryGetProperty("size", out var size) ? size.GetInt64() : 0);

        static string Name(JsonElement asset) => asset.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty;
    }
}
