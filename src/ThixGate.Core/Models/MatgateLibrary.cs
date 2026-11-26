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

    /// <summary>What the user may do in Matgate: Connect, Edit, Manage.</summary>
    public string Level { get; init; } = "";

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
