using QueueJanne.Core.Connections;

namespace QueueJanne.Core.Tests;

public sealed class ConnectionStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "queuejanne-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Saves_and_loads_profiles_without_storing_secrets_in_plain_text()
    {
        var store = new ConnectionStore(_folder);
        const string connectionString = "Endpoint=sb://my-ns.servicebus.windows.net/;SharedAccessKeyName=Root;SharedAccessKey=secret-key=";
        var profile = new ConnectionProfile
        {
            Id = ConnectionProfile.NewId(),
            Name = "Orders test",
            Auth = ConnectionAuth.ConnectionString,
            Namespace = "my-ns.servicebus.windows.net",
            ProtectedConnectionString = store.Protect(connectionString),
            Environment = "test",
        };

        store.Save(profile);
        var reloaded = new ConnectionStore(_folder);

        Assert.Equal(profile, Assert.Single(reloaded.Load()));
        Assert.Equal(connectionString, reloaded.RevealConnectionString(reloaded.Find("orders TEST")!));
        Assert.DoesNotContain("secret-key", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Save_replaces_by_id_and_remove_deletes()
    {
        var store = new ConnectionStore(_folder);
        var profile = new ConnectionProfile { Id = "a", Name = "First", Auth = ConnectionAuth.EntraId, Namespace = "a.servicebus.windows.net" };

        store.Save(profile);
        store.Save(profile with { Name = "Renamed" });

        Assert.Equal("Renamed", Assert.Single(store.Load()).Name);
        Assert.True(store.Remove("a"));
        Assert.Empty(store.Load());
        Assert.False(store.Remove("a"));
    }

    [Fact]
    public void Migration_adds_missing_connections_and_files_without_overwriting()
    {
        var legacyFolder = Path.Combine(_folder, "legacy");
        var legacy = new ConnectionStore(legacyFolder);
        legacy.Save(new ConnectionProfile { Id = "dev", Name = "dev", Auth = ConnectionAuth.EntraId, Namespace = "dev.servicebus.windows.net" });
        legacy.Save(new ConnectionProfile { Id = "same", Name = "Old name", Auth = ConnectionAuth.EntraId, Namespace = "a.servicebus.windows.net" });
        File.WriteAllText(Path.Combine(legacyFolder, "settings.json"), "{}");
        var store = new ConnectionStore(Path.Combine(_folder, "new"));
        store.Save(new ConnectionProfile { Id = "same", Name = "New name", Auth = ConnectionAuth.EntraId, Namespace = "a.servicebus.windows.net" });

        var added = store.MigrateFrom(legacyFolder);

        Assert.Equal(1, added);
        Assert.Equal(["New name", "dev"], store.Load().Select(p => p.Name));
        Assert.True(File.Exists(Path.Combine(store.Folder, "settings.json")));
        Assert.Equal(0, store.MigrateFrom(legacyFolder));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
