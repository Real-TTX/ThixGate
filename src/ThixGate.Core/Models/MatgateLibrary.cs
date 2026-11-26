namespace ThixGate.Core.Models;

/// <summary>
/// The library of a Matgate account as Matgate's app API gives it (<c>GET /app/api/library</c>): the RDP, VNC and SSH connections the
/// user may work in, with their folders - never a password. Kept per computer (matgate/&lt;account&gt;.json) so the list is there offline;
/// read-only here, a connection can be taken over into the own library.
/// </summary>
public sealed record MatgateLibrary
{
    public string Revision { get; init; } = "";

    public MatgateLibraryUser User { get; init; } = new();

    public List<MatgateFolderItem> Folders { get; init; } = [];

    public List<MatgateConnectionItem> Connections { get; init; } = [];
}

public sealed record MatgateLibraryUser
{
    public Guid Id { get; init; }

    public string UserName { get; init; } = "";

    public string DisplayName { get; init; } = "";

    /// <summary>May create connections of their own in Matgate ("Verbindungen anlegen").</summary>
    public bool CanCreatePrivate { get; init; }

    /// <summary>May create shared connections in Matgate ("Verbindungen verwalten").</summary>
    public bool CanCreateShared { get; init; }

    public bool CanCreate => CanCreatePrivate || CanCreateShared;
}

/// <summary>
/// A connection in Matgate as ThixGate creates or changes it (Matgate's <c>POST/PUT /app/api/connections</c>): only what the connection
/// IS - never a password, a key, a tunnel or a permission; those stay Matgate's own.
/// </summary>
public sealed record MatgateConnectionDraft
{
    public string Name { get; init; } = "";

    public ConnectionProtocol Protocol { get; init; } = ConnectionProtocol.Rdp;

    public string Host { get; init; } = "";

    /// <summary>0: the protocol's default port.</summary>
    public int Port { get; init; }

    public string UserName { get; init; } = "";

    /// <summary>RDP only.</summary>
    public string Domain { get; init; } = "";

    /// <summary>"Labor/Linux"; empty: none.</summary>
    public string Folder { get; init; } = "";

    public string Notes { get; init; } = "";

    /// <summary>Matgate's layout name ("de-de-qwertz").</summary>
    public string KeyboardLayout { get; init; } = "de-de-qwertz";

    public bool IgnoreCertificate { get; init; } = true;

    /// <summary>New ones: shared with others in Matgate ("Verbindungen verwalten") instead of the user's own.</summary>
    public bool Shared { get; init; }

    public static MatgateConnectionDraft Of(MatgateConnectionItem item) => new()
    {
        Name = item.Name,
        Protocol = item.ProtocolKind ?? ConnectionProtocol.Rdp,
        Host = item.Host,
        Port = item.Port,
        UserName = item.UserName,
        Domain = item.Domain,
        Folder = item.Folder,
        Notes = item.Notes,
        KeyboardLayout = item.KeyboardLayout.Length > 0 ? item.KeyboardLayout : "de-de-qwertz",
        IgnoreCertificate = item.IgnoreCertificate,
        Shared = !item.IsPrivate,
    };
}

/// <summary>A folder of Matgate: "Labor/Linux" (Path), "Linux" (Name), "Labor" (Parent).</summary>
public sealed record MatgateFolderItem
{
    public string Path { get; init; } = "";

    public string Name { get; init; } = "";

    public string Parent { get; init; } = "";
}

/// <summary>A connection in Matgate.</summary>
public sealed record MatgateConnectionItem
{
    public Guid Id { get; init; }

    public string Name { get; init; } = "";

    /// <summary>"rdp", "vnc" or "ssh".</summary>
    public string Protocol { get; init; } = "";

    public string Host { get; init; } = "";

    public int Port { get; init; }

    public string UserName { get; init; } = "";

    public string Domain { get; init; } = "";

    /// <summary>The folder path ("Labor/Linux"); empty: none.</summary>
    public string Folder { get; init; } = "";

    public string Notes { get; init; } = "";

    /// <summary>What the user may do in Matgate: ReadOnly (watch, as a session on Matgate only), Connect, Edit, Manage.</summary>
    public string Level { get; init; } = "";

    /// <summary>Matgate allows deleting it (Manage, or a server manager for a shared one).</summary>
    public bool CanDelete { get; init; }

    /// <summary>Only to watch: opens as a session on Matgate, which passes no key and no pointer on.</summary>
    public bool IsWatchOnly => Level == "ReadOnly";

    /// <summary>Name, address, user, folder may be changed from here.</summary>
    public bool CanEdit => Level is "Edit" or "Manage";

    /// <summary>Matgate's layout name ("de-de-qwertz").</summary>
    public string KeyboardLayout { get; init; } = "";

    public bool IgnoreCertificate { get; init; } = true;

    /// <summary>Matgate has a password or key for it - which stays in Matgate.</summary>
    public bool HasStoredLogin { get; init; }

    public string CredentialName { get; init; } = "";

    /// <summary>The user's own connection in Matgate.</summary>
    public bool IsPrivate { get; init; }

    public ConnectionProtocol? ProtocolKind => Protocol switch
    {
        "rdp" => ConnectionProtocol.Rdp,
        "vnc" => ConnectionProtocol.Vnc,
        "ssh" => ConnectionProtocol.Ssh,
        _ => null,
    };
}
