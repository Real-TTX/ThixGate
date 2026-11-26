using System.Text.RegularExpressions;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>
/// The library: folders and connections. Every change is checked here (values and <see cref="AccessPolicy"/>), stamped with the
/// time (the cloud's rule: the last writer wins) and wakes the cloud sync. The UI only offers.
/// </summary>
public static partial class LibraryService
{
    public const int MaxNameLength = 120;

    /// <summary>Raised after every saved change of folders, connections or credentials (any thread; also for changes from the cloud).</summary>
    public static event EventHandler? Changed;

    // ===== Reading =====

    public static IReadOnlyList<RemoteFolder> Folders() =>
        [.. LibraryStore.Read().Folders.OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)];

    public static IReadOnlyList<RemoteConnection> Connections() => LibraryStore.Read().Connections;

    public static RemoteConnection? FindConnection(Guid id) => LibraryStore.Read().Connections.FirstOrDefault(connection => connection.Id == id);

    public static RemoteFolder? FindFolder(Guid id) => LibraryStore.Read().Folders.FirstOrDefault(folder => folder.Id == id);

    /// <summary>The path of a folder for people: "Kunden › Müller GmbH".</summary>
    public static string FolderPath(Guid? folderId)
    {
        if (folderId is null)
        {
            return "";
        }

        var folders = LibraryStore.Read().Folders.ToDictionary(folder => folder.Id);
        var names = new List<string>();
        var current = folderId;
        while (current is { } id && folders.TryGetValue(id, out var folder) && names.Count < 32)
        {
            names.Insert(0, folder.Name);
            current = folder.ParentId;
        }

        return string.Join(" › ", names);
    }

    /// <summary>The connections of the list page: search (name, host, user, notes, tags), filter, sort and one page.</summary>
    public static PagedList<RemoteConnection> Query(ConnectionQuery query)
    {
        var data = LibraryStore.Read();
        IEnumerable<RemoteConnection> items = data.Connections;

        if (query.Protocol is { } protocol)
        {
            items = items.Where(connection => connection.Protocol == protocol);
        }

        if (query.FavoritesOnly)
        {
            items = items.Where(connection => connection.IsFavorite);
        }

        if (query.FolderId is { } folderId)
        {
            var inside = FolderAndChildren(data.Folders, folderId);
            items = items.Where(connection => connection.FolderId is { } id && inside.Contains(id));
        }

        var search = query.Search.Trim();
        if (search.Length > 0)
        {
            items = items.Where(connection => Matches(connection, search));
        }

        items = query.Sort switch
        {
            ConnectionSort.LastUsed => items.OrderBy(connection => connection.LastUsed ?? DateTimeOffset.MinValue).ThenBy(connection => connection.Name, StringComparer.CurrentCultureIgnoreCase),
            ConnectionSort.Host => items.OrderBy(connection => connection.Host, StringComparer.OrdinalIgnoreCase),
            ConnectionSort.Protocol => items.OrderBy(connection => connection.Protocol).ThenBy(connection => connection.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => items.OrderBy(connection => connection.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        // "Zuletzt verwendet" is newest first unless the user turned it round.
        var descending = query.Sort == ConnectionSort.LastUsed ? !query.Descending : query.Descending;
        var list = items.ToList();
        if (descending)
        {
            list.Reverse();
        }

        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var page = Math.Clamp(query.Page, 1, Math.Max(1, (list.Count + pageSize - 1) / pageSize));
        return new PagedList<RemoteConnection>([.. list.Skip((page - 1) * pageSize).Take(pageSize)], list.Count, page, pageSize);
    }

    /// <summary>The last connections used (start page, tray menu).</summary>
    public static IReadOnlyList<RemoteConnection> Recent(int count = 8) =>
        [.. LibraryStore.Read().Connections.Where(connection => connection.LastUsed is not null)
            .OrderByDescending(connection => connection.LastUsed).Take(count)];

    // ===== Connections =====

    /// <summary>Creates or updates a connection (by its id) after every check; returns what was saved.</summary>
    public static RemoteConnection SaveConnection(RemoteConnection connection)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var data = LibraryStore.Read();
        var normalized = Normalize(connection);
        Validate(normalized, data);

        var now = AppClock.UtcNow;
        var existing = data.Connections.FirstOrDefault(item => item.Id == normalized.Id);
        var saved = normalized with
        {
            CreateDate = existing?.CreateDate ?? now,
            UpdateDate = NextStamp(existing?.UpdateDate, now),
            LastUsed = existing?.LastUsed ?? normalized.LastUsed,
        };

        LibraryStore.Update(library =>
        {
            library.Connections.RemoveAll(item => item.Id == saved.Id);
            library.Connections.Add(saved);
        });
        Raise();
        return saved;
    }

    /// <summary>A copy named "… (Kopie)" in the same folder.</summary>
    public static RemoteConnection DuplicateConnection(Guid id)
    {
        var original = FindConnection(id) ?? throw new BusinessException("Diese Verbindung gibt es nicht mehr.");
        var name = original.Name + " (Kopie)";
        return SaveConnection(original with { Id = Guid.NewGuid(), Name = name.Length > MaxNameLength ? name[..MaxNameLength] : name, LastUsed = null, IsFavorite = false });
    }

    public static void DeleteConnection(Guid id)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var removed = false;
        LibraryStore.Update(library =>
        {
            removed = library.Connections.RemoveAll(item => item.Id == id) > 0;
            if (removed)
            {
                library.Tombstones.Add(new Tombstone(SyncCollections.Connections, id, AppClock.UtcNow));
            }
        });

        if (!removed)
        {
            throw new BusinessException("Diese Verbindung gibt es nicht mehr.");
        }

        Raise();
    }

    public static void SetFavorite(Guid id, bool isFavorite)
    {
        var connection = FindConnection(id) ?? throw new BusinessException("Diese Verbindung gibt es nicht mehr.");
        SaveConnection(connection with { IsFavorite = isFavorite });
    }

    /// <summary>Remembers that a connection was opened now (no configuration lock: using is allowed; stays on this computer).</summary>
    public static void MarkUsed(Guid id)
    {
        var now = AppClock.UtcNow;
        LibraryStore.Update(library =>
        {
            var index = library.Connections.FindIndex(item => item.Id == id);
            if (index >= 0)
            {
                library.Connections[index] = library.Connections[index] with { LastUsed = now };
            }
        });
        Raise(syncToo: false);
    }

    // ===== Folders =====

    public static RemoteFolder SaveFolder(RemoteFolder folder)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var data = LibraryStore.Read();
        var name = folder.Name.Trim();
        if (name.Length == 0)
        {
            throw new BusinessException("Bitte geben Sie dem Ordner einen Namen.", nameof(RemoteFolder.Name));
        }

        if (name.Length > MaxNameLength)
        {
            throw new BusinessException($"Der Name darf höchstens {MaxNameLength} Zeichen lang sein.", nameof(RemoteFolder.Name));
        }

        if (folder.ParentId is { } parentId)
        {
            if (data.Folders.All(item => item.Id != parentId))
            {
                throw new BusinessException("Den übergeordneten Ordner gibt es nicht mehr.", nameof(RemoteFolder.ParentId));
            }

            if (FolderAndChildren(data.Folders, folder.Id).Contains(parentId))
            {
                throw new BusinessException("Ein Ordner kann nicht in sich selbst liegen.", nameof(RemoteFolder.ParentId));
            }
        }

        if (data.Folders.Any(item => item.Id != folder.Id && item.ParentId == folder.ParentId
            && string.Equals(item.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new BusinessException($"Den Ordner „{name}“ gibt es hier schon.", nameof(RemoteFolder.Name));
        }

        var now = AppClock.UtcNow;
        var existing = data.Folders.FirstOrDefault(item => item.Id == folder.Id);
        var saved = folder with { Name = name, CreateDate = existing?.CreateDate ?? now, UpdateDate = NextStamp(existing?.UpdateDate, now) };
        LibraryStore.Update(library =>
        {
            library.Folders.RemoveAll(item => item.Id == saved.Id);
            library.Folders.Add(saved);
        });
        Raise();
        return saved;
    }

    /// <summary>Deletes a folder. Its connections and sub folders move to the folder above it (nothing is lost).</summary>
    public static void DeleteFolder(Guid id)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var folder = FindFolder(id) ?? throw new BusinessException("Diesen Ordner gibt es nicht mehr.");
        var now = AppClock.UtcNow;
        LibraryStore.Update(library =>
        {
            library.Folders.RemoveAll(item => item.Id == id);
            library.Tombstones.Add(new Tombstone(SyncCollections.Folders, id, now));
            for (var index = 0; index < library.Folders.Count; index++)
            {
                if (library.Folders[index].ParentId == id)
                {
                    library.Folders[index] = library.Folders[index] with { ParentId = folder.ParentId, UpdateDate = NextStamp(library.Folders[index].UpdateDate, now) };
                }
            }

            for (var index = 0; index < library.Connections.Count; index++)
            {
                if (library.Connections[index].FolderId == id)
                {
                    library.Connections[index] = library.Connections[index] with { FolderId = folder.ParentId, UpdateDate = NextStamp(library.Connections[index].UpdateDate, now) };
                }
            }
        });
        Raise();
    }

    /// <summary>How many connections a folder holds (with its sub folders).</summary>
    public static int CountIn(Guid folderId)
    {
        var data = LibraryStore.Read();
        var inside = FolderAndChildren(data.Folders, folderId);
        return data.Connections.Count(connection => connection.FolderId is { } id && inside.Contains(id));
    }

    // ===== Cloud (only CloudSyncItems) =====

    internal static void ApplyRemote(Action<LibraryData> change)
    {
        LibraryStore.Update(change);
        Raise(syncToo: false);
    }

    internal static void Raise(bool syncToo = true)
    {
        Changed?.Invoke(null, EventArgs.Empty);
        if (syncToo)
        {
            CloudSyncService.Kick();
        }
    }

    /// <summary>A time stamp after the old one (two saves in the same tick must still be two changes).</summary>
    internal static DateTimeOffset NextStamp(DateTimeOffset? previous, DateTimeOffset now) =>
        previous is { } old && old >= now ? old.AddTicks(1) : now;

    internal static HashSet<Guid> FolderAndChildren(IReadOnlyList<RemoteFolder> folders, Guid folderId)
    {
        var result = new HashSet<Guid> { folderId };
        var added = true;
        while (added)
        {
            added = false;
            foreach (var folder in folders)
            {
                if (folder.ParentId is { } parent && result.Contains(parent) && result.Add(folder.Id))
                {
                    added = true;
                }
            }
        }

        return result;
    }

    private static bool Matches(RemoteConnection connection, string search) =>
        connection.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || connection.Host.Contains(search, StringComparison.OrdinalIgnoreCase)
        || connection.UserName.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || connection.Notes.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || connection.Tags.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    private static RemoteConnection Normalize(RemoteConnection connection)
    {
        var tags = string.Join(", ", connection.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase));
        var normalized = connection with
        {
            Name = connection.Name.Trim(),
            Host = connection.Host.Trim(),
            UserName = connection.UserName.Trim(),
            Domain = connection.Domain.Trim(),
            Notes = connection.Notes.Trim(),
            Tags = tags,
            KeyFile = connection.KeyFile.Trim(),
            Resolution = connection.Resolution.Trim().ToLowerInvariant() is { Length: > 0 } resolution ? resolution : "auto",
        };

        // Fields that do not apply to the protocol are not kept (the form hides them, the business layer clears them).
        normalized = normalized.Protocol switch
        {
            ConnectionProtocol.Ssh => normalized with { Domain = "", Resolution = "auto", Quality = SessionQuality.Auto },
            ConnectionProtocol.Vnc => normalized with { Domain = "", KeyFile = "" },
            _ => normalized with { KeyFile = "" },
        };

        // A session on Matgate is for remote desktops über Matgate only.
        if (normalized.Via == ConnectVia.Direct || normalized.Protocol == ConnectionProtocol.Ssh)
        {
            normalized = normalized with { SessionOnMatgate = false };
        }

        return normalized.Via == ConnectVia.Direct ? normalized with { MatgateAccountId = null, MatgateConnectionId = null } : normalized;
    }

    private static void Validate(RemoteConnection connection, LibraryData data)
    {
        if (!Enum.IsDefined(connection.Protocol))
        {
            throw new BusinessException("Dieses Protokoll gibt es nicht.", nameof(RemoteConnection.Protocol));
        }

        if (connection.Name.Length == 0)
        {
            throw new BusinessException("Bitte geben Sie der Verbindung einen Namen.", nameof(RemoteConnection.Name));
        }

        if (connection.Name.Length > MaxNameLength)
        {
            throw new BusinessException($"Der Name darf höchstens {MaxNameLength} Zeichen lang sein.", nameof(RemoteConnection.Name));
        }

        if (connection.Host.Length == 0)
        {
            throw new BusinessException("Bitte geben Sie die Adresse (Rechnername oder IP) an.", nameof(RemoteConnection.Host));
        }

        if (connection.Host.Length > 253 || !HostRegex().IsMatch(connection.Host))
        {
            throw new BusinessException("Die Adresse ist ungültig (Rechnername, IPv4 oder IPv6, ohne Leerzeichen und ohne Port).", nameof(RemoteConnection.Host));
        }

        if (connection.Port is < 0 or > 65535)
        {
            throw new BusinessException("Der Port muss zwischen 1 und 65535 liegen (leer: Standard).", nameof(RemoteConnection.Port));
        }

        if (connection.UserName.Length > 256 || connection.Domain.Length > 256)
        {
            throw new BusinessException("Benutzername und Domäne dürfen höchstens 256 Zeichen lang sein.", nameof(RemoteConnection.UserName));
        }

        if (connection.Protocol == ConnectionProtocol.Ssh && connection.UserName.Any(character => char.IsWhiteSpace(character) || character is '@' or '"' or '\''))
        {
            throw new BusinessException("Der Benutzername für SSH darf keine Leerzeichen, @ oder Anführungszeichen enthalten.", nameof(RemoteConnection.UserName));
        }

        if (connection.Notes.Length > 4000)
        {
            throw new BusinessException("Die Notiz darf höchstens 4000 Zeichen lang sein.", nameof(RemoteConnection.Notes));
        }

        if (connection.Resolution is not ("auto" or "native") && !ResolutionRegex().IsMatch(connection.Resolution))
        {
            throw new BusinessException("Auflösung: „auto“, „native“ oder Breite x Höhe (z. B. 1920x1080).", nameof(RemoteConnection.Resolution));
        }

        if (connection.FolderId is { } folderId && data.Folders.All(folder => folder.Id != folderId))
        {
            throw new BusinessException("Diesen Ordner gibt es nicht mehr.", nameof(RemoteConnection.FolderId));
        }

        if (connection.CredentialId is { } credentialId && data.Credentials.All(credential => credential.Id != credentialId))
        {
            throw new BusinessException("Diese Zugangsdaten gibt es nicht mehr.", nameof(RemoteConnection.CredentialId));
        }

        if (connection.Via == ConnectVia.Matgate)
        {
            AccessPolicy.EnsureMatgateAllowed();
            if (connection.MatgateAccountId is not { } accountId || SettingsService.Get().MatgateAccounts.All(account => account.Id != accountId))
            {
                throw new BusinessException("Bitte wählen Sie das Matgate, über das verbunden wird.", nameof(RemoteConnection.MatgateAccountId));
            }

            // Matgate opens only host and port of one of its connections (no open proxy): which one, is part of the connection.
            if (connection.MatgateConnectionId is null)
            {
                throw new BusinessException("Bitte wählen Sie die Verbindung in Matgate, über die verbunden wird.", nameof(RemoteConnection.MatgateConnectionId));
            }
        }
    }

    // A host name (letters, digits, '-', '.', '_'), an IPv4 or an IPv6 address (with ':' - no brackets, no port).
    [GeneratedRegex(@"^([A-Za-z0-9_]([A-Za-z0-9_\-]*[A-Za-z0-9_])?\.?)+$|^[0-9A-Fa-f:.%]+$")]
    private static partial Regex HostRegex();

    [GeneratedRegex(@"^\d{3,5}x\d{3,5}$")]
    private static partial Regex ResolutionRegex();
}
