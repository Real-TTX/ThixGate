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
    /// (<paramref name="through"/>, from <see cref="MatgateRelay.OpenAsync"/>) - <see cref="ResolveAsync"/> opens it. RDP spans the
    /// <paramref name="monitors"/> (two or more, <see cref="SessionDisplay.MonitorsFor"/>) and brings this computer's folders as drives.
    /// </summary>
    public static ScreenTarget ScreenTargetOf(RemoteConnection connection, SessionLogin login, int fitWidth, int fitHeight, IPEndPoint? through = null, IReadOnlyList<ScreenMonitor>? monitors = null)
    {
        if (!connection.Protocol.IsScreen())
        {
            throw new BusinessException("Nur Remote Desktop und VNC zeigen einen Bildschirm.");
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
            Monitors = monitors is { Count: > 1 } && connection.Protocol == ConnectionProtocol.Rdp ? monitors : [],
            Drives = SessionDisplay.DrivesFor(connection),
        };
    }

    /// <summary>The target to connect to: directly, or through the relay of its Matgate.</summary>
    public static async Task<ScreenTarget> ResolveAsync(RemoteConnection connection, SessionLogin login, int fitWidth, int fitHeight, CancellationToken cancellationToken, IReadOnlyList<ScreenMonitor>? monitors = null)
    {
        var through = connection.Via == ConnectVia.Matgate ? await MatgateRelay.OpenAsync(connection, cancellationToken) : null;
        return ScreenTargetOf(connection, login, fitWidth, fitHeight, through, monitors);
    }

    /// <summary>
    /// The backend of a session: a session on Matgate (<see cref="RemoteConnection.SessionOnMatgate"/>, shown by <see cref="MatgateScreen"/>),
    /// or the engine's own RDP/VNC client - directly or through the relay of its Matgate.
    /// </summary>
    public static async Task<IScreenBackend> BackendAsync(RemoteConnection connection, SessionLogin login, int fitWidth, int fitHeight, IScreenHost host, CancellationToken cancellationToken, IReadOnlyList<ScreenMonitor>? monitors = null)
    {
        if (connection.Via == ConnectVia.Matgate && connection.SessionOnMatgate)
        {
            return MatgateScreen.For(connection, fitWidth, fitHeight, host);
        }

        return ScreenBackends.Create(await ResolveAsync(connection, login, fitWidth, fitHeight, cancellationToken, monitors), host);
    }

    /// <summary>
    /// The target of a file connection for Mat.Remote's <see cref="Matgate.Files.RemoteFiles"/> - the same Matgate builds - with the user
    /// and password given (asked or from the vault); SFTP with its key file as text (the password is then its passphrase). Über Matgate:
    /// through its relay, the host name stays the server's (WebDAV's URL and certificate). FTP cannot go through the relay.
    /// </summary>
    public static async Task<Matgate.Files.FileTarget> FileTargetAsync(RemoteConnection connection, string userName, string password, CancellationToken cancellationToken, string domain = "")
    {
        var protocol = connection.Protocol switch
        {
            ConnectionProtocol.Sftp => Matgate.Files.FileProtocol.Sftp,
            ConnectionProtocol.Ftp => Matgate.Files.FileProtocol.Ftp,
            ConnectionProtocol.Smb => Matgate.Files.FileProtocol.Smb,
            ConnectionProtocol.WebDav => Matgate.Files.FileProtocol.WebDav,
            _ => throw new BusinessException("Diese Verbindung ist keine Dateiverbindung."),
        };
        if (connection.Via == ConnectVia.Matgate && protocol == Matgate.Files.FileProtocol.Ftp)
        {
            throw new BusinessException("FTP geht nicht über Matgate (es öffnet weitere Verbindungen für seine Daten) – bitte direkt verbinden oder SFTP nehmen.");
        }

        var key = "";
        if (connection.KeyFile.Length > 0)
        {
            if (!File.Exists(connection.KeyFile))
            {
                throw new BusinessException($"Die Schlüsseldatei {connection.KeyFile} gibt es nicht.", nameof(RemoteConnection.KeyFile));
            }

            key = await File.ReadAllTextAsync(connection.KeyFile, cancellationToken);
        }

        var through = connection.Via == ConnectVia.Matgate ? await MatgateRelay.OpenAsync(connection, cancellationToken) : null;
        return new Matgate.Files.FileTarget
        {
            Protocol = protocol,
            Host = connection.Host,
            Port = connection.EffectivePort,
            UserName = userName,
            Password = password,
            Domain = domain.Length > 0 ? domain : connection.Domain.Length > 0 ? connection.Domain : LoginOf(connection).Domain,
            PrivateKey = key,
            FileRootPath = connection.RootPath,
            IgnoreCertificate = connection.IgnoreCertificate,
            DialHost = through?.Address.ToString() ?? "",
            DialPort = through?.Port ?? 0,
        };
    }

    /// <summary>The target of a database connection for Mat.Remote's DatabaseGateway; über Matgate through its relay.</summary>
    public static async Task<Matgate.Services.Database.DatabaseTarget> DatabaseTargetAsync(RemoteConnection connection, CancellationToken cancellationToken)
    {
        var engine = connection.Protocol switch
        {
            ConnectionProtocol.MsSql => Matgate.Services.Database.DatabaseEngine.MsSql,
            ConnectionProtocol.MySql => Matgate.Services.Database.DatabaseEngine.MySql,
            ConnectionProtocol.Postgres => Matgate.Services.Database.DatabaseEngine.Postgres,
            _ => throw new BusinessException("Diese Verbindung ist keine Datenbank."),
        };
        var through = connection.Via == ConnectVia.Matgate ? await MatgateRelay.OpenAsync(connection, cancellationToken) : null;
        return new Matgate.Services.Database.DatabaseTarget
        {
            Protocol = engine,
            Host = connection.Host,
            Port = connection.EffectivePort,
            DatabaseName = connection.Database,
            IgnoreCertificate = connection.IgnoreCertificate,
            ApplicationName = "ThixGate",
            DialHost = through?.Address.ToString() ?? "",
            DialPort = through?.Port ?? 0,
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
