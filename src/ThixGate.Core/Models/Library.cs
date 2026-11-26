namespace ThixGate.Core.Models;

/// <summary>The protocol of a connection.</summary>
public enum ConnectionProtocol
{
    Rdp = 0,
    Vnc = 1,
    Ssh = 2,
}

/// <summary>How ThixGate reaches the host of a connection.</summary>
public enum ConnectVia
{
    /// <summary>Straight from this computer.</summary>
    Direct = 0,

    /// <summary>Through a Matgate gateway (an account in the settings).</summary>
    Matgate = 1,
}

/// <summary>The picture quality of an RDP/VNC session (the levels of Matgate's engine).</summary>
public enum SessionQuality
{
    Auto = 0,
    Medium = 1,
    Low = 2,
    Fast = 3,
}

/// <summary>Where a sync item stands with the cloud.</summary>
public enum SyncState
{
    Pending = 0,
    Synced = 1,

    /// <summary>The cloud refused it (too large): it stays on this computer.</summary>
    LocalOnly = 2,
}

/// <summary>A folder of the library. Folders nest (<see cref="ParentId"/>).</summary>
public sealed record RemoteFolder
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "";

    public Guid? ParentId { get; init; }

    public DateTimeOffset CreateDate { get; init; }

    public DateTimeOffset UpdateDate { get; init; }
}

/// <summary>
/// A connection of the library. What it IS (protocol, host, port, options) - the engine options are built from it
/// (<see cref="Business.SessionTargets"/>). The password is never here: it lives in a <see cref="StoredCredential"/>
/// (or is asked for at connect).
/// </summary>
public sealed record RemoteConnection
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "";

    public ConnectionProtocol Protocol { get; init; } = ConnectionProtocol.Rdp;

    public string Host { get; init; } = "";

    /// <summary>0: the protocol's default port.</summary>
    public int Port { get; init; }

    public Guid? FolderId { get; init; }

    /// <summary>The saved credentials to use; null: <see cref="UserName"/> / <see cref="Domain"/> below, password asked at connect.</summary>
    public Guid? CredentialId { get; init; }

    public string UserName { get; init; } = "";

    /// <summary>RDP only.</summary>
    public string Domain { get; init; } = "";

    public string Notes { get; init; } = "";

    /// <summary>Comma separated tags.</summary>
    public string Tags { get; init; } = "";

    public bool IsFavorite { get; init; }

    // ---- RDP / VNC ----

    public SessionQuality Quality { get; init; } = SessionQuality.Auto;

    /// <summary>"auto" (follow the window), "native" or "WxH".</summary>
    public string Resolution { get; init; } = "auto";

    /// <summary>RDP: accept the server's certificate without asking (most servers have a self-signed one).</summary>
    public bool IgnoreCertificate { get; init; } = true;

    /// <summary>RDP: Windows keyboard layout id (0x407 German, 0x409 US).</summary>
    public uint KeyboardLayout { get; init; } = 0x407;

    // ---- SSH ----

    /// <summary>SSH: a private key file instead of a password.</summary>
    public string KeyFile { get; init; } = "";

    // ---- Gateway ----

    public ConnectVia Via { get; init; } = ConnectVia.Direct;

    /// <summary>The Matgate account (<see cref="MatgateAccount.Id"/>) when <see cref="Via"/> is Matgate.</summary>
    public Guid? MatgateAccountId { get; init; }

    /// <summary>
    /// The connection in that Matgate the relay goes to (Matgate opens host and port of ITS connection - no open proxy). Login and
    /// password stay ThixGate's own.
    /// </summary>
    public Guid? MatgateConnectionId { get; init; }

    /// <summary>
    /// RDP, VNC über Matgate: the session runs on Matgate (with Matgate's login, the picture comes to ThixGate) instead of ThixGate's
    /// engine going through the relay - for a connection whose password only Matgate knows, or that the user may only watch.
    /// </summary>
    public bool SessionOnMatgate { get; init; }

    // ---- Kept by the app ----

    public DateTimeOffset? LastUsed { get; init; }

    public DateTimeOffset CreateDate { get; init; }

    public DateTimeOffset UpdateDate { get; init; }

    public int EffectivePort => Port > 0 ? Port : DefaultPort(Protocol);

    public static int DefaultPort(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Vnc => 5900,
        ConnectionProtocol.Ssh => 22,
        _ => 3389,
    };
}

/// <summary>
/// Saved credentials, usable by many connections ("gespeicherte Zugangsdaten" as in Matgate). The password is protected for
/// this user (DPAPI / Keychain) and never leaves the computer: the cloud gets name, user and domain only.
/// </summary>
public sealed record StoredCredential
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "";

    public string UserName { get; init; } = "";

    public string Domain { get; init; } = "";

    /// <summary>Whether a password is stored on THIS computer.</summary>
    public bool HasPassword { get; init; }

    public string Notes { get; init; } = "";

    public DateTimeOffset CreateDate { get; init; }

    public DateTimeOffset UpdateDate { get; init; }
}

/// <summary>A deleted item, kept until the cloud has it.</summary>
public sealed record Tombstone(string Collection, Guid Id, DateTimeOffset DeletedAt);

/// <summary>The library file (library.json). Only <see cref="Infrastructure.LibraryStore"/> reads and writes it.</summary>
public sealed record LibraryData
{
    public List<RemoteFolder> Folders { get; init; } = [];

    public List<RemoteConnection> Connections { get; init; } = [];

    public List<StoredCredential> Credentials { get; init; } = [];

    public List<Tombstone> Tombstones { get; init; } = [];

    /// <summary>"collection/id" → the UpdateDate the cloud has (or <see cref="SyncState.LocalOnly"/>).</summary>
    public Dictionary<string, SyncMark> Sync { get; init; } = [];
}

public sealed record SyncMark(DateTimeOffset UpdateDate, SyncState State);

/// <summary>A row the cloud does not have yet.</summary>
public sealed record PendingRow(string Collection, Guid Id, bool IsDeleted, DateTimeOffset ClientUpdateDate);

/// <summary>How the connection list is sorted.</summary>
public enum ConnectionSort
{
    Name = 0,
    LastUsed = 1,
    Host = 2,
    Protocol = 3,
}

/// <summary>Search, filter and sort of the connection list (the toolbar above the list).</summary>
public sealed record ConnectionQuery
{
    public string Search { get; init; } = "";

    public ConnectionProtocol? Protocol { get; init; }

    /// <summary>Only this folder and its sub folders; null: all.</summary>
    public Guid? FolderId { get; init; }

    public bool FavoritesOnly { get; init; }

    public ConnectionSort Sort { get; init; } = ConnectionSort.Name;

    public bool Descending { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 50;
}

/// <summary>A page of a list.</summary>
public sealed record PagedList<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);
}

/// <summary>One row of the tree column (<see cref="Business.LibraryService.Tree"/>): a folder or a connection, indented by its depth.</summary>
public sealed record LibraryTreeRow
{
    public RemoteFolder? Folder { get; init; }

    public RemoteConnection? Connection { get; init; }

    public int Depth { get; init; }

    /// <summary>A folder: the connections in it and below it.</summary>
    public int Count { get; init; }

    /// <summary>A folder with sub-folders or connections (it has a chevron).</summary>
    public bool HasChildren { get; init; }

    public bool IsExpanded { get; init; }

    public Guid Id => Folder?.Id ?? Connection!.Id;

    public string Name => Folder?.Name ?? Connection!.Name;

    public bool IsFolder => Folder is not null;
}
