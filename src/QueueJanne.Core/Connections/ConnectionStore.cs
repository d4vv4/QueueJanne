using System.Text.Json;
using System.Text.Json.Serialization;

namespace QueueJanne.Core.Connections;

/// <summary>
/// Saved connections, shared by the desktop app and the MCP server. The file lives in <c>~/.queuejanne</c>,
/// or in <c>QUEUEJANNE_HOME</c> when set.
/// </summary>
public sealed class ConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly ISecretProtector _protector;

    public ConnectionStore(string? folder = null, ISecretProtector? protector = null)
    {
        Folder = folder ?? DefaultFolder();
        _protector = protector ?? SecretProtector.CreateDefault();
    }

    public string Folder { get; }

    public string FilePath => Path.Combine(Folder, "connections.json");

    /// <summary>
    /// <c>~/.queuejanne</c>. Not AppData: Windows gives packaged apps (such as the Claude desktop app) a private copy of AppData,
    /// so an MCP server they start wouldn't see the connections the desktop app saved there.
    /// </summary>
    public static string DefaultFolder()
    {
        var overridden = System.Environment.GetEnvironmentVariable("QUEUEJANNE_HOME");
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".queuejanne")
            : overridden;
    }

    /// <summary>Where version 0.3 and earlier kept their files.</summary>
    public static string LegacyFolder() =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "QueueJanne");

    /// <summary>
    /// Copies connections that only exist in <paramref name="legacyFolder"/> into this store, and other files there that don't exist here yet.
    /// Only the desktop app calls this, because it sees the real AppData.
    /// </summary>
    public int MigrateFrom(string legacyFolder)
    {
        var legacy = new ConnectionStore(legacyFolder, _protector);
        if (Path.GetFullPath(legacy.Folder).Equals(Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(legacy.Folder))
        {
            return 0;
        }

        lock (_gate)
        {
            var current = Load().ToList();
            var added = legacy.Load().Where(old => current.All(p => p.Id != old.Id)).ToList();
            if (added.Count > 0)
            {
                Write(current.Concat(added).ToList());
            }

            foreach (var file in Directory.GetFiles(legacy.Folder).Where(f => !Path.GetFileName(f).Equals("connections.json", StringComparison.OrdinalIgnoreCase)))
            {
                var destination = Path.Combine(Folder, Path.GetFileName(file));
                if (!File.Exists(destination))
                {
                    Directory.CreateDirectory(Folder);
                    File.Copy(file, destination);
                }
            }

            return added.Count;
        }
    }

    public IReadOnlyList<ConnectionProfile> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            var document = JsonSerializer.Deserialize<StoreDocument>(File.ReadAllText(FilePath), JsonOptions);
            return document?.Connections ?? [];
        }
    }

    /// <summary>Finds a connection by id, or else by name (case-insensitive).</summary>
    public ConnectionProfile? Find(string idOrName)
    {
        var profiles = Load();
        return profiles.FirstOrDefault(p => p.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
            ?? profiles.FirstOrDefault(p => p.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds the profile, or replaces the one with the same id.</summary>
    public ConnectionProfile Save(ConnectionProfile profile)
    {
        lock (_gate)
        {
            var profiles = Load().Where(p => p.Id != profile.Id).Append(profile).ToList();
            Write(profiles);
            return profile;
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var profiles = Load().ToList();
            if (profiles.RemoveAll(p => p.Id == id) == 0)
            {
                return false;
            }

            Write(profiles);
            return true;
        }
    }

    public string Protect(string secret) => _protector.Protect(secret);

    public string RevealConnectionString(ConnectionProfile profile) =>
        profile.ProtectedConnectionString is { Length: > 0 } value
            ? _protector.Unprotect(value)
            : throw new InvalidOperationException($"Connection '{profile.Name}' has no connection string.");

    private void Write(List<ConnectionProfile> profiles)
    {
        Directory.CreateDirectory(Folder);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new StoreDocument(1, profiles), JsonOptions));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, FilePath, overwrite: true);
    }

    private sealed record StoreDocument(int Version, List<ConnectionProfile> Connections);
}
