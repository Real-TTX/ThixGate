using System.Net;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>What the user typed when the server asked (user name, password or the key's passphrase).</summary>
public sealed record SshAnswer(string UserName, string Password, bool Remember);

/// <summary>The view of an SSH session: it asks the user (sign-in, a new or changed host key). Called from a worker thread.</summary>
public interface ISshHost
{
    /// <summary>The server wants a password (or the key its passphrase): null = cancelled.</summary>
    Task<SshAnswer?> AskAsync(string userName, string prompt, bool failed, bool canRemember);

    /// <summary>A server's key that is new (<paramref name="previous"/> null) or another than last time: true = trust it.</summary>
    Task<bool> TrustHostKeyAsync(string host, int port, string algorithm, string fingerprint, KnownHost? previous);
}

/// <summary>
/// An SSH session in ThixGate's own terminal (instead of the system's): signs in with the connection's credentials (the key file, its
/// passphrase being the password; the password; or what the user types), checks the server's key against <see cref="SshHostKeys"/>,
/// opens a shell as "xterm-256color" and passes bytes both ways. A connection "über Matgate" goes through its relay; the host key is kept
/// under the host's own name. The password never leaves the vault except to the server.
/// </summary>
public sealed class SshSession : IAsyncDisposable
{
    private const int Attempts = 4;

    private readonly RemoteConnection _connection;
    private readonly ISshHost _host;
    private readonly TaskCompletionSource<string?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SshClient? _client;
    private ShellStream? _stream;
    private string? _hostKeyRefused;
    private SshAnswer? _typed;
    private bool _canRemember;

    public SshSession(RemoteConnection connection, ISshHost host)
    {
        if (connection.Protocol != ConnectionProtocol.Ssh)
        {
            throw new BusinessException("Nur SSH-Verbindungen öffnen ein Terminal.");
        }

        _connection = connection;
        _host = host;
    }

    /// <summary>What the server sent (any thread): the terminal reads it.</summary>
    public event Action<byte[]>? Received;

    public bool IsConnected => _stream is not null && _client?.IsConnected == true;

    /// <summary>Connects, signs in and opens the shell with the size of the terminal.</summary>
    public async Task ConnectAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        if (_connection.KeyFile.Length > 0 && !File.Exists(_connection.KeyFile))
        {
            throw new BusinessException($"Die Schlüsseldatei {_connection.KeyFile} gibt es nicht.", nameof(RemoteConnection.KeyFile));
        }

        var through = _connection.Via == ConnectVia.Matgate ? await MatgateRelay.OpenAsync(_connection, cancellationToken) : null;
        var login = SessionTargets.LoginOf(_connection);
        var userName = login.UserName;
        var password = login.Password;
        _canRemember = login.CredentialId is not null && !AccessPolicy.ArePasswordsDisabled;
        var needsKeyPassphrase = KeyNeedsPassphrase(_connection.KeyFile);
        var refused = false;

        // Without a password the first try goes like ssh: the server's key first, then the server asks (keyboard-interactive) and the user
        // answers then. A server that only takes "password" refuses that try: ThixGate asks and tries again.
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (userName.Length == 0 || (password.Length == 0 && (needsKeyPassphrase || attempt > 0)))
            {
                var answer = await _host.AskAsync(userName, needsKeyPassphrase ? "Passphrase der Schlüsseldatei" : "Passwort", refused, _canRemember)
                             ?? throw new OperationCanceledException(cancellationToken);
                (userName, password) = (answer.UserName.Trim(), answer.Password);
                _typed = answer;
            }

            var client = new SshClient(Info(through, userName, password));
            client.HostKeyReceived += (_, e) => e.CanTrust = TrustKey(e);
            try
            {
                await client.ConnectAsync(cancellationToken);
                _client = client;
                break;
            }
            catch (SshAuthenticationException) when (attempt + 1 < Attempts && _hostKeyRefused is null)
            {
                client.Dispose();
                Log.Info($"SSH {_connection.Id}: sign-in refused (attempt {attempt + 1})");
                refused = password.Length > 0 || _typed is not null;
                password = ""; // ask again
                _typed = null;
            }
            catch (SshConnectionException) when (_hostKeyRefused is not null)
            {
                client.Dispose();
                throw new BusinessException(_hostKeyRefused);
            }
            catch (SshAuthenticationException)
            {
                client.Dispose();
                throw new BusinessException("Die Anmeldung wurde abgelehnt: Benutzer, Passwort oder Schlüssel stimmen nicht.");
            }
            catch (Exception exception) when (exception is SocketException or SshOperationTimeoutException or ProxyException)
            {
                client.Dispose();
                throw new BusinessException($"{Address} ist nicht erreichbar ({exception.Message}).");
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        if (_typed is { Remember: true, Password.Length: > 0 } typed && login.CredentialId is { } credentialId)
        {
            try
            {
                CredentialService.RememberPassword(credentialId, typed.Password);
            }
            catch (BusinessException exception)
            {
                Log.Warning($"Password not kept: {exception.Message}");
            }
        }

        var stream = _client.CreateShellStream("xterm-256color", (uint)Math.Max(2, columns), (uint)Math.Max(1, rows), 0, 0, 64 * 1024);
        stream.DataReceived += (_, e) => Received?.Invoke(e.Data);
        stream.Closed += (_, _) => _closed.TrySetResult(null);
        stream.ErrorOccurred += (_, e) => _closed.TrySetResult(e.Exception.Message);
        _client.ErrorOccurred += (_, e) => _closed.TrySetResult(e.Exception.Message);
        _stream = stream;
        LibraryService.MarkUsed(_connection.Id);
    }

    /// <summary>Waits until the server ends the shell (exit) or the connection breaks: the reason, null for a normal end.</summary>
    public async Task<string?> RunAsync(CancellationToken cancellationToken)
    {
        await using var registration = cancellationToken.Register(() => _closed.TrySetCanceled(cancellationToken));
        return await _closed.Task;
    }

    /// <summary>Keys and pasted text for the server.</summary>
    public void Send(ReadOnlySpan<byte> bytes)
    {
        if (_stream is not { } stream)
        {
            return;
        }

        try
        {
            stream.Write(bytes);
            stream.Flush();
        }
        catch (Exception exception) when (exception is ObjectDisposedException or SshException or IOException or InvalidOperationException)
        {
            _closed.TrySetResult(exception.Message);
        }
    }

    public void Send(string text) => Send(Encoding.UTF8.GetBytes(text));

    /// <summary>The terminal changed its size: the server's programs follow.</summary>
    public void Resize(int columns, int rows)
    {
        try
        {
            _stream?.ChangeWindowSize((uint)Math.Max(2, columns), (uint)Math.Max(1, rows), 0, 0);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or SshException or InvalidOperationException)
        {
            Log.Warning($"SSH {_connection.Id}: resize: {exception.Message}");
        }
    }

    public ValueTask DisposeAsync()
    {
        _closed.TrySetResult(null);
        try
        {
            _stream?.Dispose();
            if (_client is { IsConnected: true })
            {
                _client.Disconnect();
            }
        }
        catch (Exception exception) when (exception is ObjectDisposedException or SshException or IOException or SocketException)
        {
            Log.Warning($"SSH {_connection.Id}: closing: {exception.Message}");
        }
        finally
        {
            _client?.Dispose();
            _stream = null;
            _client = null;
        }

        return ValueTask.CompletedTask;
    }

    private string Address => $"{_connection.Host}:{_connection.EffectivePort}";

    private Renci.SshNet.ConnectionInfo Info(IPEndPoint? through, string userName, string password)
    {
        var methods = new List<AuthenticationMethod>();
        if (_connection.KeyFile.Length > 0)
        {
            var key = password.Length > 0 && KeyNeedsPassphrase(_connection.KeyFile)
                ? new PrivateKeyFile(_connection.KeyFile, password)
                : new PrivateKeyFile(_connection.KeyFile);
            methods.Add(new PrivateKeyAuthenticationMethod(userName, key));
        }

        if (password.Length == 0 && !(_connection.KeyFile.Length > 0 && KeyNeedsPassphrase(_connection.KeyFile)))
        {
            // No password yet: the server's questions go to the user (after the host key was checked).
            var asking = new KeyboardInteractiveAuthenticationMethod(userName);
            asking.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    var answer = _host.AskAsync(userName, prompt.Request.Trim().TrimEnd(':'), _typed is not null, _canRemember).GetAwaiter().GetResult();
                    if (answer is null)
                    {
                        prompt.Response = "";
                        continue;
                    }

                    _typed = answer with { UserName = userName };
                    prompt.Response = answer.Password;
                }
            };
            methods.Add(asking);
        }
        else if (password.Length > 0 && !(_connection.KeyFile.Length > 0 && KeyNeedsPassphrase(_connection.KeyFile)))
        {
            methods.Add(new PasswordAuthenticationMethod(userName, password));
            var keyboard = new KeyboardInteractiveAuthenticationMethod(userName);
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = password;
                }
            };
            methods.Add(keyboard);
        }

        if (methods.Count == 0)
        {
            methods.Add(new NoneAuthenticationMethod(userName));
        }

        var host = through?.Address.ToString() ?? _connection.Host;
        var port = through?.Port ?? _connection.EffectivePort;
        return new Renci.SshNet.ConnectionInfo(host, port, userName, [.. methods]) { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>The server's key against the known ones (on SSH.NET's thread: it waits for the user's answer).</summary>
    private bool TrustKey(HostKeyEventArgs e)
    {
        var host = _connection.Host;
        var port = _connection.EffectivePort;
        var fingerprint = "SHA256:" + e.FingerPrintSHA256;
        var check = SshHostKeys.Check(host, port, e.HostKeyName, fingerprint, out var known);
        if (check == HostKeyCheck.Known)
        {
            return true;
        }

        var trusted = _host.TrustHostKeyAsync(host, port, e.HostKeyName, fingerprint, known).GetAwaiter().GetResult();
        if (!trusted)
        {
            _hostKeyRefused = check == HostKeyCheck.Changed
                ? "Der Schlüssel des Servers hat sich geändert und wurde nicht angenommen: nicht verbunden."
                : "Der Schlüssel des Servers wurde nicht angenommen: nicht verbunden.";
            return false;
        }

        SshHostKeys.Trust(host, port, e.HostKeyName, fingerprint);
        return true;
    }

    /// <summary>Whether the key file is encrypted (it then needs its passphrase).</summary>
    private static bool KeyNeedsPassphrase(string keyFile)
    {
        if (keyFile.Length == 0)
        {
            return false;
        }

        try
        {
            using var _ = new PrivateKeyFile(keyFile);
            return false;
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            return true;
        }
        catch (Exception exception) when (exception is SshException or IOException or ArgumentException or InvalidOperationException)
        {
            throw new BusinessException($"Die Schlüsseldatei {keyFile} lässt sich nicht lesen: {exception.Message}", nameof(RemoteConnection.KeyFile));
        }
    }
}
