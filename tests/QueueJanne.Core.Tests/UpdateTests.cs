using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using QueueJanne.Core.Updates;

namespace QueueJanne.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "qj-update-" + Guid.NewGuid().ToString("N"));

    public UpdateTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Reads_the_latest_release_and_picks_this_platforms_zip()
    {
        var prefix = UpdateChecker.AssetPrefix ?? "QueueJanne-windows-x64-";
        var json = $$"""
            {
              "tag_name": "v0.6.12",
              "html_url": "https://github.com/owner/QueueJanne/releases/tag/v0.6.12",
              "body": "Faster tabs",
              "assets": [
                { "name": "QueueJanne-macos-other-0.6.12.zip", "browser_download_url": "https://example.test/other.zip", "size": 1 },
                { "name": "{{prefix}}0.6.12.zip", "browser_download_url": "https://example.test/app.zip", "size": 1234 },
                { "name": "{{prefix}}0.6.12.zip.sha256", "browser_download_url": "https://example.test/app.zip.sha256", "size": 90 }
              ]
            }
            """;
        using var http = new HttpClient(new Responder(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));

        var latest = await new UpdateChecker(http, "owner/QueueJanne").GetLatestAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(latest);
        Assert.Equal(new Version(0, 6, 12), latest.Version);
        Assert.True(latest.Version > UpdateChecker.Normalize(new Version(0, 6, 0, 0)));
        Assert.Equal("Faster tabs", latest.Notes);
        if (UpdateChecker.AssetPrefix is not null)
        {
            Assert.Equal("https://example.test/app.zip", latest.DownloadUrl);
            Assert.Equal("https://example.test/app.zip.sha256", latest.ChecksumUrl);
            Assert.Equal(1234, latest.Size);
        }
    }

    [Fact]
    public async Task No_releases_yet_means_no_update()
    {
        using var http = new HttpClient(new Responder(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Null(await new UpdateChecker(http, "owner/QueueJanne").GetLatestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_refuses_a_zip_that_doesnt_match_its_checksum()
    {
        var zip = Encoding.UTF8.GetBytes("not really a zip");
        using var http = new HttpClient(new Responder(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                ? new StringContent(new string('0', 64) + "  app.zip")
                : new ByteArrayContent(zip),
        }));
        var update = new UpdateInfo(new Version(0, 6, 12), string.Empty, string.Empty, "https://example.test/app.zip", "https://example.test/app.zip.sha256", zip.Length);

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.DownloadAsync(http, update, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_keeps_a_zip_that_matches_its_checksum()
    {
        var zip = Encoding.UTF8.GetBytes("zip bytes");
        var hash = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        using var http = new HttpClient(new Responder(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) ? new StringContent($"{hash}  app.zip") : new ByteArrayContent(zip),
        }));
        var update = new UpdateInfo(new Version(0, 6, 12), string.Empty, string.Empty, "https://example.test/app.zip", "https://example.test/app.zip.sha256", zip.Length);

        var path = await UpdateInstaller.DownloadAsync(http, update, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(zip, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        File.Delete(path);
    }

    [Fact]
    public void Install_replaces_files_that_are_in_use_and_leaves_the_old_ones_for_later()
    {
        var install = Directory.CreateDirectory(Path.Combine(_folder, "install")).FullName;
        File.WriteAllText(Path.Combine(install, "QueueJanne.exe"), "old app");
        File.WriteAllText(Path.Combine(install, "queuejanne-mcp.exe"), "old mcp");
        File.WriteAllText(Path.Combine(install, "notes.txt"), "kept");
        var zip = Zip(("QueueJanne.exe", "new app"), ("queuejanne-mcp.exe", "new mcp"));

        // A running program's file can be renamed but not overwritten; FileShare.Delete gives the same rules.
        using (new FileStream(Path.Combine(install, "QueueJanne.exe"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            UpdateInstaller.Install(zip, install);
        }

        Assert.Equal("new app", File.ReadAllText(Path.Combine(install, "QueueJanne.exe")));
        Assert.Equal("new mcp", File.ReadAllText(Path.Combine(install, "queuejanne-mcp.exe")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(install, "notes.txt")));
        Assert.Equal(2, Directory.GetFiles(install, "*.old-*").Length);

        UpdateInstaller.RemoveLeftovers(install);
        Assert.Empty(Directory.GetFiles(install, "*.old-*"));
    }

    [Fact]
    public void A_failed_install_puts_the_old_files_back()
    {
        var install = Directory.CreateDirectory(Path.Combine(_folder, "install")).FullName;
        File.WriteAllText(Path.Combine(install, "QueueJanne.exe"), "old app");
        File.WriteAllText(Path.Combine(install, "queuejanne-mcp.exe"), "old mcp");
        var zip = Zip(("QueueJanne.exe", "new app"), ("queuejanne-mcp.exe", "new mcp"));

        // A file held open without sharing can't be renamed, so the second replacement fails.
        using (new FileStream(Path.Combine(install, "queuejanne-mcp.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => UpdateInstaller.Install(zip, install));
        }

        Assert.Equal("old app", File.ReadAllText(Path.Combine(install, "QueueJanne.exe")));
        Assert.Equal("old mcp", File.ReadAllText(Path.Combine(install, "queuejanne-mcp.exe")));
        Assert.Empty(Directory.GetFiles(install, "*.old-*"));
    }

    [Fact]
    public void Development_builds_dont_update_themselves()
    {
        var dev = Directory.CreateDirectory(Path.Combine(_folder, "bin")).FullName;
        File.WriteAllText(Path.Combine(dev, "QueueJanne.dll"), string.Empty);

        Assert.Null(UpdateInstaller.InstalledLocation(Path.Combine(dev, "QueueJanne.exe")));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(_folder, UpdateInstaller.InstalledLocation(Path.Combine(_folder, "QueueJanne.exe")));
        }
    }

    private string Zip(params (string Name, string Text)[] files)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(text);
        }

        return path;
    }

    private sealed class Responder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
