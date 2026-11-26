namespace ThixGate.Core.Models;

/// <summary>The protocol of a connection (stored by name; new ones are appended).</summary>
public enum ConnectionProtocol
{
    Rdp = 0,
    Vnc = 1,
    Ssh = 2,

    // files
    Sftp = 3,
    Ftp = 4,
    Smb = 5,
    WebDav = 6,

    /// <summary>A website (shown in ThixGate's browser).</summary>
    Web = 7,

    // databases
    MsSql = 8,
    MySql = 9,
    Postgres = 10,
}

/// <summary>What kind of session a protocol opens.</summary>
public enum ConnectionKind
{
    /// <summary>A remote desktop: RDP, VNC.</summary>
    Screen,

    /// <summary>A terminal: SSH.</summary>
    Terminal,

    /// <summary>A file manager: SFTP, FTP, SMB, WebDAV.</summary>
    Files,

    /// <summary>A browser: a website.</summary>
    Web,

    /// <summary>A database browser: SQL Server, MySQL, PostgreSQL.</summary>
    Database,
}

public static class ConnectionKinds
{
    public static ConnectionKind KindOf(this ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Ssh => ConnectionKind.Terminal,
        ConnectionProtocol.Sftp or ConnectionProtocol.Ftp or ConnectionProtocol.Smb or ConnectionProtocol.WebDav => ConnectionKind.Files,
        ConnectionProtocol.Web => ConnectionKind.Web,
        ConnectionProtocol.MsSql or ConnectionProtocol.MySql or ConnectionProtocol.Postgres => ConnectionKind.Database,
        _ => ConnectionKind.Screen,
    };

    public static bool IsScreen(this ConnectionProtocol protocol) => protocol.KindOf() == ConnectionKind.Screen;

    /// <summary>Matgate's name of the protocol in its app API ("rdp", "sftp", "website", "mssql", …).</summary>
    public static string MatgateName(this ConnectionProtocol protocol) => protocol == ConnectionProtocol.Web ? "website" : protocol.ToString().ToLowerInvariant();

    /// <summary>The protocol of Matgate's name; null: one ThixGate does not open.</summary>
    public static ConnectionProtocol? FromMatgateName(string name) => name switch
    {
        "rdp" => ConnectionProtocol.Rdp,
        "vnc" => ConnectionProtocol.Vnc,
        "ssh" => ConnectionProtocol.Ssh,
        "sftp" => ConnectionProtocol.Sftp,
        "ftp" => ConnectionProtocol.Ftp,
        "smb" => ConnectionProtocol.Smb,
        "webdav" => ConnectionProtocol.WebDav,
        "website" => ConnectionProtocol.Web,
        "mssql" => ConnectionProtocol.MsSql,
        "mysql" => ConnectionProtocol.MySql,
        "postgres" => ConnectionProtocol.Postgres,
        _ => null,
    };

    /// <summary>The protocols with a domain: RDP and SMB.</summary>
    public static bool HasDomain(this ConnectionProtocol protocol) => protocol is ConnectionProtocol.Rdp or ConnectionProtocol.Smb;

    /// <summary>The protocols with a key file: SSH and SFTP.</summary>
    public static bool HasKeyFile(this ConnectionProtocol protocol) => protocol is ConnectionProtocol.Ssh or ConnectionProtocol.Sftp;

    /// <summary>The protocols that check a certificate: RDP, WebDAV, a website, SQL Server.</summary>
    public static bool HasCertificate(this ConnectionProtocol protocol) =>
        protocol is ConnectionProtocol.Rdp or ConnectionProtocol.WebDav or ConnectionProtocol.Web or ConnectionProtocol.MsSql;
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

    /// <summary>SSH, SFTP: a private key file instead of a password (the password is then its passphrase).</summary>
    public string KeyFile { get; init; } = "";

    // ---- Files, websites, databases ----

    /// <summary>A website: its address (https://…); Host is its host name.</summary>
    public string Url { get; init; } = "";

    /// <summary>Files: the folder the connection starts in and cannot leave ("/home/anna", SMB "share/folder"; empty: the top).</summary>
    public string RootPath { get; init; } = "";

    /// <summary>Databases: the database (empty: every one the login may see).</summary>
    public string Database { get; init; } = "";

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
        ConnectionProtocol.Ssh or ConnectionProtocol.Sftp => 22,
        ConnectionProtocol.Ftp => 21,
        ConnectionProtocol.Smb => 445,
        ConnectionProtocol.WebDav or ConnectionProtocol.Web => 443,
        ConnectionProtocol.MsSql => 1433,
        ConnectionProtocol.MySql => 3306,
        ConnectionProtocol.Postgres => 5432,
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

/// <summary>
/// One row of the tree of the menu (<see cref="Business.LibraryService.Tree"/>), indented by its depth: a folder or a connection of the own
/// library, or - from a signed-in Matgate, straight from its library copy, nothing taken over - the Matgate itself, one of its folders or one
/// of its connections.
/// </summary>
public sealed record LibraryTreeRow
{
    public RemoteFolder? Folder { get; init; }

    public RemoteConnection? Connection { get; init; }

    /// <summary>The node of a Matgate account (its folders and connections below it).</summary>
    public MatgateAccount? Matgate { get; init; }

    /// <summary>A folder or connection of a Matgate: the account it comes from.</summary>
    public Guid? MatgateAccountId { get; init; }

    /// <summary>A folder of a Matgate: its path ("Labor/Linux").</summary>
    public string MatgatePath { get; init; } = "";

    /// <summary>A connection of a Matgate.</summary>
    public MatgateConnectionItem? MatgateItem { get; init; }

    public int Depth { get; init; }

    /// <summary>A folder (or Matgate): the connections in it and below it.</summary>
    public int Count { get; init; }

    /// <summary>A folder with sub-folders or connections (it has a chevron).</summary>
    public bool HasChildren { get; init; }

    public bool IsExpanded { get; init; }

    public Guid Id => Folder?.Id ?? Connection?.Id ?? Matgate?.Id ?? MatgateItem?.Id ?? MatgateFolderId(MatgateAccountId ?? Guid.Empty, MatgatePath);

    public string Name => Folder?.Name ?? Connection?.Name ?? Matgate?.Name ?? MatgateItem?.Name ?? MatgatePath.Split('/')[^1];

    /// <summary>Something with rows below it: a folder, a Matgate, a folder of a Matgate.</summary>
    public bool IsFolder => Folder is not null || Matgate is not null || MatgatePath.Length > 0;

    /// <summary>A Matgate, or a folder or connection of one.</summary>
    public bool IsFromMatgate => Matgate is not null || MatgateAccountId is not null;

    /// <summary>The fixed id of a Matgate folder in the tree (its open state is remembered by it).</summary>
    public static Guid MatgateFolderId(Guid accountId, string path)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{accountId:N}/{path.ToLowerInvariant()}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
