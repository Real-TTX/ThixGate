using Avalonia.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>Opens a connection the way its protocol wants: RDP and VNC in a <see cref="SessionWindow"/>, SSH in the terminal.</summary>
public static class SessionLauncher
{
    /// <summary>Tests replace the opening (no windows, no terminals).</summary>
    internal static Action<RemoteConnection>? OpenForTests { get; set; }

    public static void Open(RemoteConnection connection, Window? owner)
    {
        if (OpenForTests is { } open)
        {
            open(connection);
            return;
        }

        if (connection.Protocol == ConnectionProtocol.Ssh)
        {
            SshLauncher.Open(connection);
            return;
        }

        if (connection.Via == ConnectVia.Matgate)
        {
            throw new BusinessException("Verbinden über Matgate kommt mit der App-Schnittstelle von Matgate – bis dahin bitte unter „Gateway“ „Direkt“ wählen oder die Verbindung in Matgate öffnen.");
        }

        SessionWindow.Show(connection);
    }
}
