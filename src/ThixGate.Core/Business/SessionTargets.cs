using Matgate.Display;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>What is known before connecting: the user, the domain and - if stored - the password.</summary>
public sealed record SessionLogin(string UserName, string Domain, string Password, Guid? CredentialId);

/// <summary>
/// From a connection of the library to the options of the shared engine (Mat.Remote's <see cref="ScreenTarget"/>, the same type
/// Matgate builds from its servers), so that a connection means the same in ThixGate and in Matgate.
/// </summary>
public static class SessionTargets
{
    /// <summary>The login of a connection: its saved credentials (with the password when this computer has it) or its own user.</summary>
    public static SessionLogin LoginOf(RemoteConnection connection)
    {
        if (connection.CredentialId is { } credentialId && CredentialService.Find(credentialId) is { } credential)
        {
            return new SessionLogin(credential.UserName, credential.Domain, CredentialService.ReadPassword(credentialId) ?? "", credentialId);
        }

        return new SessionLogin(connection.UserName, connection.Domain, "", null);
    }

    /// <summary>The engine's target for an RDP or VNC connection.</summary>
    public static ScreenTarget ScreenTargetOf(RemoteConnection connection, SessionLogin login, int fitWidth, int fitHeight)
    {
        if (connection.Protocol == ConnectionProtocol.Ssh)
        {
            throw new BusinessException("SSH-Verbindungen öffnen ein Terminal, keinen Bildschirm.");
        }

        if (connection.Via == ConnectVia.Matgate)
        {
            throw new BusinessException("Verbinden über Matgate kommt mit der App-Schnittstelle von Matgate – bis dahin bitte „Direkt“ wählen oder die Verbindung in Matgate öffnen.");
        }

        return new ScreenTarget
        {
            Protocol = connection.Protocol == ConnectionProtocol.Vnc ? "vnc" : "rdp",
            Host = connection.Host,
            Port = connection.EffectivePort,
            UserName = login.UserName,
            Password = login.Password,
            Domain = login.Domain,
            IgnoreCertificate = connection.IgnoreCertificate,
            KeyboardLayout = connection.KeyboardLayout,
            Quality = QualityName(connection.Quality),
            Resolution = connection.Resolution,
            FitWidth = fitWidth,
            FitHeight = fitHeight,
        };
    }

    /// <summary>The names of Matgate's quality levels (ScreenQuality.From).</summary>
    public static string QualityName(SessionQuality quality) => quality switch
    {
        SessionQuality.Medium => "medium",
        SessionQuality.Low => "low",
        SessionQuality.Fast => "fast",
        _ => "auto",
    };
}
