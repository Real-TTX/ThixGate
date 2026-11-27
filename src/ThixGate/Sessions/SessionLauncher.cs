using Avalonia.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>
/// Opens a connection the way its protocol wants: RDP and VNC as a session of the <see cref="SessionHub"/> (a tab of the main window or a window of its own; it opens the relay of a connection
/// "über Matgate" itself); SSH as well, in ThixGate's own terminal - or, when the settings say so, in the system's terminal (after opening its relay).
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

        // A pasted key never goes to another program (it would have to be a file or on a command line): such a connection opens here.
        if (connection.Protocol == ConnectionProtocol.Ssh && SettingsService.Get().SshInSystemTerminal && !SshKeys.IsStored(connection.Id))
        {
            await SshLauncher.OpenAsync(connection);
            return;
        }

        SessionHub.Open(connection);
    }
}
