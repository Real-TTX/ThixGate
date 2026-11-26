using System.Text.Json;
using System.Text.Json.Serialization;
using ThixGate.Core.Models;

namespace ThixGate.Core.Infrastructure;

/// <summary>
/// Reads and writes library.json and vault.json. Only the business layer uses this class. One lock for both files; every write
/// goes to a temporary file first and replaces the old one, so a crash never leaves half a file. The cloud's bookkeeping (what is
/// pending, tombstones) lives here too, so the sync loop and the services never disagree.
/// </summary>
internal static class LibraryStore
{
    private static readonly object Gate = new();
    private static LibraryData? _cache;
    private static Dictionary<Guid, string>? _vault;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>A snapshot of the library (records and fresh lists: callers may not change it).</summary>
    public static LibraryData Read()
    {
        lock (Gate)
        {
            return Copy(_cache ??= Load());
        }
    }

    /// <summary>Changes the library in one step and saves it.</summary>
    public static LibraryData Update(Action<LibraryData> change)
    {
        lock (Gate)
        {
            var data = Copy(_cache ??= Load());
            change(data);
            WriteAtomically(AppPaths.LibraryFile, JsonSerializer.Serialize(data, SerializerOptions));
            _cache = data;
            return Copy(data);
        }
    }

    /// <summary>Drops the caches (tests, sample data).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _cache = null;
            _vault = null;
        }
    }

    // ===== Vault (protected passwords, never synced) =====

    public static string? ProtectedPassword(Guid credentialId)
    {
        lock (Gate)
        {
            return (_vault ??= LoadVault()).GetValueOrDefault(credentialId);
        }
    }

    public static void SetProtectedPassword(Guid credentialId, string? protectedValue)
    {
        lock (Gate)
        {
            var vault = new Dictionary<Guid, string>(_vault ??= LoadVault());
            if (protectedValue is null)
            {
                vault.Remove(credentialId);
            }
            else
            {
                vault[credentialId] = protectedValue;
            }

            WriteAtomically(AppPaths.VaultFile, JsonSerializer.Serialize(vault, SerializerOptions));
            _vault = vault;
        }
    }

    // ===== Cloud bookkeeping =====

    public static string KeyOf(string collection, Guid id) => $"{collection}/{id:D}";

    /// <summary>What the cloud does not have yet (changed items and tombstones), oldest first.</summary>
    public static IReadOnlyList<PendingRow> Pending(int limit)
    {
        var data = Read();
        var rows = new List<PendingRow>();
        AddPending(rows, data, SyncCollections.Folders, data.Folders.Select(folder => (folder.Id, folder.UpdateDate)));
        AddPending(rows, data, SyncCollections.Connections, data.Connections.Select(connection => (connection.Id, connection.UpdateDate)));
        AddPending(rows, data, SyncCollections.Credentials, data.Credentials.Select(credential => (credential.Id, credential.UpdateDate)));
        foreach (var tombstone in data.Tombstones)
        {
            var mark = data.Sync.GetValueOrDefault(KeyOf(tombstone.Collection, tombstone.Id));
            if (mark is null || mark.UpdateDate < tombstone.DeletedAt)
            {
                rows.Add(new PendingRow(tombstone.Collection, tombstone.Id, true, tombstone.DeletedAt));
            }
        }

        return [.. rows.OrderBy(row => row.ClientUpdateDate).Take(limit)];
    }

    /// <summary>The cloud has this state of the item now.</summary>
    public static void SetSynced(string collection, Guid id, DateTimeOffset updateDate, SyncState state = SyncState.Synced) =>
        Update(data =>
        {
            data.Sync[KeyOf(collection, id)] = new SyncMark(updateDate, state);
            if (state == SyncState.Synced)
            {
                data.Tombstones.RemoveAll(tombstone => tombstone.Collection == collection && tombstone.Id == id && tombstone.DeletedAt <= updateDate);
            }
        });

    /// <summary>A new sign-in: everything goes up again (the cloud keeps the newer one).</summary>
    public static void MarkAllPending() => Update(data => data.Sync.Clear());

    /// <summary>Signed out: deletions are not carried anywhere any more.</summary>
    public static void RemoveTombstones() => Update(data =>
    {
        data.Tombstones.Clear();
        data.Sync.Clear();
    });

    private static void AddPending(List<PendingRow> rows, LibraryData data, string collection, IEnumerable<(Guid Id, DateTimeOffset UpdateDate)> items)
    {
        foreach (var (id, updateDate) in items)
        {
            var mark = data.Sync.GetValueOrDefault(KeyOf(collection, id));
            if (mark is null || (mark.State != SyncState.LocalOnly && mark.UpdateDate != updateDate))
            {
                rows.Add(new PendingRow(collection, id, false, updateDate));
            }
        }
    }

    private static LibraryData Copy(LibraryData data) => data with
    {
        Folders = [.. data.Folders],
        Connections = [.. data.Connections],
        Credentials = [.. data.Credentials],
        Tombstones = [.. data.Tombstones],
        Sync = new Dictionary<string, SyncMark>(data.Sync, StringComparer.Ordinal),
    };

    private static LibraryData Load()
    {
        var path = AppPaths.LibraryFile;
        if (!File.Exists(path))
        {
            return new LibraryData();
        }

        try
        {
            using var stream = File.OpenRead(path);
            var data = JsonSerializer.Deserialize<LibraryData>(stream, SerializerOptions) ?? new LibraryData();
            return data with { Sync = new Dictionary<string, SyncMark>(data.Sync ?? [], StringComparer.Ordinal) };
        }
        catch (JsonException exception)
        {
            // Keep the broken file for the user instead of silently overwriting it.
            Log.Warning($"library.json could not be read ({exception.Message}); starting with an empty library");
            File.Copy(path, path + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            return new LibraryData();
        }
    }

    private static Dictionary<Guid, string> LoadVault()
    {
        var path = AppPaths.VaultFile;
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<Guid, string>>(File.ReadAllText(path), SerializerOptions) ?? [];
        }
        catch (JsonException exception)
        {
            Log.Warning($"vault.json could not be read ({exception.GetType().Name}); saved passwords are asked again");
            return [];
        }
    }

    private static void WriteAtomically(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, text);
        File.Move(temporaryPath, path, overwrite: true);
    }
}

/// <summary>The collections of ThixGate in the general sync of ThixCloud (app "gate").</summary>
public static class SyncCollections
{
    public const string Folders = "folders";
    public const string Connections = "connections";
    public const string Credentials = "credentials";
}
