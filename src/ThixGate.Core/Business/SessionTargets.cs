using System.Net;
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

    /// <summary>
    /// The engine's target for an RDP or VNC connection. A connection "über Matgate" goes to the local end of its relay
    /// (<paramref name="through"/>, from <see cref="MatgateRelay.OpenAsync"/>) - <see cref="ResolveAsync"/> opens it.
    /// </summary>
    public static ScreenTarget ScreenTargetOf(RemoteConnection connection, SessionLogin login, int fitWidth, int fitHeight, IPEndPoint? through = null)
    {
        if (connection.Protocol == ConnectionProtocol.Ssh)
        {
            throw new BusinessException("SSH-Verbindungen öffnen ein Terminal, keinen Bildschirm.");
        }

        if (connection.Via == ConnectVia.Matgate && through is null)
        {
            throw new BusinessException("Diese Verbindung geht über Matgate: erst die Durchleitung öffnen.");
        }

        return new ScreenTarget
        {
            Protocol = connection.Protocol == ConnectionProtocol.Vnc ? "vnc" : "rdp",
            Host = through?.Address.ToString() ?? connection.Host,
            Port = through?.Port ?? connection.EffectivePort,
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

    /// <summary>The target to connect to: directly, or through the relay of its Matgate.</summary>
    public static async Task<ScreenTarget> ResolveAsync(RemoteConnection connection, SessionLogin login, int fitWidth, int fitHeight, CancellationToken cancellationToken)
    {
        var through = connection.Via == ConnectVia.Matgate ? await MatgateRelay.OpenAsync(connection, cancellationToken) : null;
        return ScreenTargetOf(connection, login, fitWidth, fitHeight, through);
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
