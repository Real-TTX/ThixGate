using ThixGate.Core.Models;

namespace ThixGate.Controls;

/// <summary>How a protocol looks in the UI: its name and icon (one place for lists, forms, tray and session windows).</summary>
public static class Protocols
{
    /// <summary>The choices of the form, in the order of <see cref="ConnectionProtocol"/>.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "Remote Desktop (RDP)", "VNC", "SSH (Terminal)",
        "SFTP (Dateien über SSH)", "FTP", "SMB (Windows-Freigabe)", "WebDAV",
        "Website",
        "SQL Server", "MySQL / MariaDB", "PostgreSQL",
    ];

    public static (string Name, string Icon) Of(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Vnc => ("VNC", "tv"),
        ConnectionProtocol.Ssh => ("SSH", "terminal"),
        ConnectionProtocol.Sftp => ("SFTP", "folder"),
        ConnectionProtocol.Ftp => ("FTP", "folder"),
        ConnectionProtocol.Smb => ("SMB", "hard-drive"),
        ConnectionProtocol.WebDav => ("WebDAV", "cloud"),
        ConnectionProtocol.Web => ("Website", "globe"),
        ConnectionProtocol.MsSql => ("SQL Server", "database"),
        ConnectionProtocol.MySql => ("MySQL", "database"),
        ConnectionProtocol.Postgres => ("PostgreSQL", "database"),
        _ => ("RDP", "monitor"),
    };

    /// <summary>"server.example.com" or "server.example.com:3390" (the port only when it is not the protocol's own).</summary>
    public static string Address(RemoteConnection connection) =>
        connection.Protocol == ConnectionProtocol.Web && connection.Url.Length > 0 ? connection.Url
        : connection.Port > 0 && connection.Port != RemoteConnection.DefaultPort(connection.Protocol)
            ? (connection.Host.Contains(':') ? $"[{connection.Host}]:{connection.Port}" : $"{connection.Host}:{connection.Port}")
            : connection.Host;
}
