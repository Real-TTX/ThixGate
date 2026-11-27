using System.Text.Json;
using System.Text.Json.Serialization;
using MatCloud.Contract;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>
/// ThixGate's items in the general sync of ThixCloud (app "gate"): collections "folders", "connections" and "credentials"
/// (key = the GUID, data = JSON of the item). A credential travels WITHOUT its password; "last used" and "is a password here" stay on
/// each computer. Deleting is a tombstone. The last writer wins by UpdatedAt (MatCloud's rule).
/// </summary>
internal static class CloudSyncItems
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The order in which pulled items are applied: folders before what lives in them.</summary>
    public static int OrderOf(string collection) => collection switch
    {
        SyncCollections.Folders => 0,
        SyncCollections.Credentials => 1,
        _ => 2,
    };

    /// <summary>The item for a pending row (null when the row is gone meanwhile or too large for the cloud).</summary>
    public static SyncItemDto? ToItem(PendingRow row)
    {
        var key = row.Id.ToString("D");
        if (row.IsDeleted)
        {
            return new SyncItemDto { Collection = row.Collection, Key = key, UpdatedAt = row.ClientUpdateDate, IsDeleted = true };
        }

        var data = LibraryStore.Read();
        string? json = row.Collection switch
        {
            SyncCollections.Folders => data.Folders.FirstOrDefault(folder => folder.Id == row.Id) is { } folder
                ? JsonSerializer.Serialize(folder, Json) : null,
            SyncCollections.Connections => data.Connections.FirstOrDefault(connection => connection.Id == row.Id) is { } connection
                ? JsonSerializer.Serialize(connection with { LastUsed = null }, Json) : null,
            SyncCollections.Credentials => data.Credentials.FirstOrDefault(credential => credential.Id == row.Id) is { } credential
                ? JsonSerializer.Serialize(credential with { HasPassword = false }, Json) : null,
            _ => null,
        };

        if (json is null || json.Length > SyncContract.MaxDataLength)
        {
            return null;
        }

        return new SyncItemDto { Collection = row.Collection, Key = key, Data = json, UpdatedAt = row.ClientUpdateDate };
    }

    /// <summary>Applies an item of the cloud here (when it is newer than what this computer has).</summary>
    public static void Apply(SyncItemDto item)
    {
        if (!Guid.TryParse(item.Key, out var id))
        {
            return;
        }

        try
        {
            switch (item.Collection)
            {
                case SyncCollections.Folders:
                    ApplyFolder(id, item);
                    break;
                case SyncCollections.Connections:
                    ApplyConnection(id, item);
                    break;
                case SyncCollections.Credentials:
                    ApplyCredential(id, item);
                    break;
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException or NotSupportedException)
        {
            Log.Warning($"Sync: item {item.Collection}/{item.Key} could not be read ({exception.GetType().Name})");
        }
    }

    private static void ApplyFolder(Guid id, SyncItemDto item)
    {
        var local = LibraryStore.Read().Folders.FirstOrDefault(folder => folder.Id == id);
        if (!IsNewer(item, local?.UpdateDate, SyncCollections.Folders, id))
        {
            return;
        }

        if (item.IsDeleted || item.Data is null)
        {
            Remove(SyncCollections.Folders, id, item.UpdatedAt, library =>
            {
                library.Folders.RemoveAll(folder => folder.Id == id);

                // What still points to it goes to the top here; the deleting computer's moves arrive as changes of their own.
                for (var index = 0; index < library.Folders.Count; index++)
                {
                    if (library.Folders[index].ParentId == id)
                    {
                        library.Folders[index] = library.Folders[index] with { ParentId = null };
                    }
                }

                for (var index = 0; index < library.Connections.Count; index++)
                {
                    if (library.Connections[index].FolderId == id)
                    {
                        library.Connections[index] = library.Connections[index] with { FolderId = null };
                    }
                }
            });
            return;
        }

        var folder = JsonSerializer.Deserialize<RemoteFolder>(item.Data, Json) ?? throw new JsonException("empty");
        Store(SyncCollections.Folders, item, library =>
        {
            library.Folders.RemoveAll(existing => existing.Id == id);
            library.Folders.Add(folder with { Id = id, UpdateDate = item.UpdatedAt });
        });
    }

    private static void ApplyConnection(Guid id, SyncItemDto item)
    {
        var local = LibraryStore.Read().Connections.FirstOrDefault(connection => connection.Id == id);
        if (!IsNewer(item, local?.UpdateDate, SyncCollections.Connections, id))
        {
            return;
        }

        if (item.IsDeleted || item.Data is null)
        {
            Remove(SyncCollections.Connections, id, item.UpdatedAt, library => library.Connections.RemoveAll(connection => connection.Id == id));
            SshKeys.Store(id, ""); // the key stored here goes with it
            return;
        }

        var connection = JsonSerializer.Deserialize<RemoteConnection>(item.Data, Json) ?? throw new JsonException("empty");
        if (!Enum.IsDefined(connection.Protocol))
        {
            return; // a protocol of a newer ThixGate: it stays in the cloud until this computer knows it
        }

        // A Matgate account is an account of a computer: unknown here, the connection goes direct.
        var accounts = SettingsService.Get().MatgateAccounts;
        if (connection.MatgateAccountId is { } account && accounts.All(known => known.Id != account))
        {
            connection = connection with { Via = ConnectVia.Direct, MatgateAccountId = null, MatgateConnectionId = null, SessionOnMatgate = false };
        }

        Store(SyncCollections.Connections, item, library =>
        {
            library.Connections.RemoveAll(existing => existing.Id == id);
            library.Connections.Add(connection with { Id = id, UpdateDate = item.UpdatedAt, LastUsed = local?.LastUsed });
        });
    }

    private static void ApplyCredential(Guid id, SyncItemDto item)
    {
        var local = LibraryStore.Read().Credentials.FirstOrDefault(credential => credential.Id == id);
        if (!IsNewer(item, local?.UpdateDate, SyncCollections.Credentials, id))
        {
            return;
        }

        if (item.IsDeleted || item.Data is null)
        {
            Remove(SyncCollections.Credentials, id, item.UpdatedAt, library => library.Credentials.RemoveAll(credential => credential.Id == id));
            LibraryStore.SetProtectedPassword(id, null);
            TokenStore.Delete(id.ToString("D"), SecretKind.Vault);
            return;
        }

        var credential = JsonSerializer.Deserialize<StoredCredential>(item.Data, Json) ?? throw new JsonException("empty");
        var hasPasswordHere = LibraryStore.ProtectedPassword(id) is not null || (local?.HasPassword ?? false);
        Store(SyncCollections.Credentials, item, library =>
        {
            library.Credentials.RemoveAll(existing => existing.Id == id);
            library.Credentials.Add(credential with { Id = id, UpdateDate = item.UpdatedAt, HasPassword = hasPasswordHere });
        });
    }

    /// <summary>Whether the cloud's item is newer than this computer's state (an item or a tombstone of it).</summary>
    private static bool IsNewer(SyncItemDto item, DateTimeOffset? localUpdate, string collection, Guid id)
    {
        if (localUpdate is { } known && known >= item.UpdatedAt)
        {
            return false;
        }

        var tombstone = LibraryStore.Read().Tombstones.FirstOrDefault(entry => entry.Collection == collection && entry.Id == id);
        return tombstone is null || tombstone.DeletedAt < item.UpdatedAt;
    }

    private static void Store(string collection, SyncItemDto item, Action<LibraryData> change) =>
        LibraryService.ApplyRemote(library =>
        {
            change(library);
            library.Tombstones.RemoveAll(entry => entry.Collection == collection && entry.Id.ToString("D") == item.Key);
            library.Sync[LibraryStore.KeyOf(collection, Guid.Parse(item.Key))] = new SyncMark(item.UpdatedAt, SyncState.Synced);
        });

    private static void Remove(string collection, Guid id, DateTimeOffset at, Action<LibraryData> change) =>
        LibraryService.ApplyRemote(library =>
        {
            change(library);
            library.Tombstones.RemoveAll(entry => entry.Collection == collection && entry.Id == id);
            library.Sync[LibraryStore.KeyOf(collection, id)] = new SyncMark(at, SyncState.Synced);
        });
}
