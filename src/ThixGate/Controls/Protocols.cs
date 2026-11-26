using ThixGate.Core.Models;

namespace ThixGate.Controls;

/// <summary>How a protocol looks in the UI: its name and icon (one place for lists, forms, tray and session windows).</summary>
public static class Protocols
{
    public static readonly IReadOnlyList<string> Names = ["Remote Desktop (RDP)", "VNC", "SSH"];

    public static (string Name, string Icon) Of(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Vnc => ("VNC", "tv"),
        ConnectionProtocol.Ssh => ("SSH", "terminal"),
        _ => ("RDP", "monitor"),
    };

    /// <summary>"server.example.com" or "server.example.com:3390" (the port only when it is not the protocol's own).</summary>
    public static string Address(RemoteConnection connection) =>
        connection.Port > 0 && connection.Port != RemoteConnection.DefaultPort(connection.Protocol)
            ? (connection.Host.Contains(':') ? $"[{connection.Host}]:{connection.Port}" : $"{connection.Host}:{connection.Port}")
            : connection.Host;
}
