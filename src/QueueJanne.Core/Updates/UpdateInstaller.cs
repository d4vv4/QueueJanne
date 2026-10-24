using System.IO.Compression;
using System.Security.Cryptography;

namespace QueueJanne.Core.Updates;

/// <summary>
/// Downloads a release and puts it in place of the running installation. A running program can't be overwritten, but on
/// both Windows and macOS it can be renamed: the old files are moved aside as <c>*.old-*</c>, the new ones take their
/// names, and the leftovers are removed the next time the app starts. A failure part way puts the old files back.
/// </summary>
public static class UpdateInstaller
{
    private const string OldMarker = ".old-";

    /// <summary>
    /// Where the running app is installed and can be updated: the folder with QueueJanne.exe on Windows, the QueueJanne.app
    /// bundle on macOS. Null for development builds, which run from a folder of separate assemblies.
    /// </summary>
    public static string? InstalledLocation(string? processPath = null)
    {
        processPath ??= Environment.ProcessPath;
        var folder = Path.GetDirectoryName(processPath);
        if (folder is null || File.Exists(Path.Combine(folder, "QueueJanne.dll")) || File.Exists(Path.Combine(folder, "QueueJanne.Core.dll")))
        {
            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            var bundle = Path.GetFullPath(Path.Combine(folder, "..", ".."));
            return bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle : null;
        }

        return folder;
    }

    /// <summary>Downloads the release's zip to a temporary file and checks it against the published SHA-256 checksum.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var url = update.DownloadUrl ?? throw new InvalidOperationException("This release has no download for this computer.");
        var path = Path.Combine(Path.GetTempPath(), $"queuejanne-{update.Version}-{Guid.NewGuid():N}.zip");
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.Size;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                done += read;
                if (total > 0)
                {
                    progress?.Report((double)done / total);
                }
            }
        }

        if (update.ChecksumUrl is { } checksumUrl)
        {
            var expected = (await http.GetStringAsync(checksumUrl, cancellationToken).ConfigureAwait(false)).Split(' ', '\t', '\n', '\r')[0].Trim();
            string actual;
            await using (var file = File.OpenRead(path))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
            }

            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new InvalidDataException("The downloaded update doesn't match its published checksum, so it wasn't installed.");
            }
        }

        return path;
    }

    /// <summary>
    /// Replaces the installation with the zip's contents. On Windows <paramref name="location"/> is the install folder and
    /// each file in the zip replaces the file of the same name; on macOS it's the .app bundle, replaced as a whole.
    /// </summary>
    public static void Install(string zipPath, string location)
    {
        var staging = Path.Combine(Path.GetTempPath(), "queuejanne-update-" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(zipPath, staging);
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var replacements = location.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                ? [(Directory.GetDirectories(staging, "*.app").Single(), location)]
                : Directory.GetFiles(staging).Select(file => (file, Path.Combine(location, Path.GetFileName(file)))).ToList();
            if (replacements.Count == 0)
            {
                throw new InvalidDataException("The update contains no files.");
            }

            var done = new List<(string Target, string? Old)>();
            try
            {
                foreach (var (source, target) in replacements)
                {
                    var old = Exists(target) ? target + OldMarker + stamp : null;
                    if (old is not null)
                    {
                        Move(target, old);
                    }

                    done.Add((target, old));
                    Move(source, target);
                }
            }
            catch
            {
                foreach (var (target, old) in Enumerable.Reverse(done))
                {
                    if (old is not null)
                    {
                        if (Exists(target))
                        {
                            Delete(target);
                        }

                        Move(old, target);
                    }
                }

                throw;
            }
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Removes the files an earlier update moved aside. Ones still in use (a running MCP server) stay until next time.</summary>
    public static void RemoveLeftovers(string location)
    {
        var folder = location.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(location) : location;
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        foreach (var leftover in Directory.EnumerateFileSystemEntries(folder, "*" + OldMarker + "*"))
        {
            try
            {
                Delete(leftover);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void Move(string from, string to)
    {
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else
        {
            File.Delete(path);
        }
    }
}
