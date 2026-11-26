using Avalonia.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>
/// Opens a connection the way its protocol wants: RDP and VNC in a <see cref="SessionWindow"/> (which opens the relay of a connection
/// "über Matgate" itself), SSH in the terminal (after opening its relay).
/// </summary>
public static class SessionLauncher
{
    /// <summary>Tests replace the opening (no windows, no terminals).</summary>
    internal static Action<RemoteConnection>? OpenForTests { get; set; }

    public static async Task OpenAsync(RemoteConnection connection, Window? owner)
    {
        if (OpenForTests is { } open)
        {
            open(connection);
            return;
        }

        if (connection.Protocol == ConnectionProtocol.Ssh)
        {
            await SshLauncher.OpenAsync(connection);
            return;
        }

        SessionWindow.Show(connection);
    }
}
