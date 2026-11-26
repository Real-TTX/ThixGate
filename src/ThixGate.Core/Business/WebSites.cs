using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>A cookie the browser of a website tab sets before it opens the site (an automatic sign-in).</summary>
public sealed record WebCookie(string Name, string Value, string Domain, string Path);

/// <summary>
/// What a website tab needs besides the browser: where the browser keeps its data (per connection, so a sign-in stays), the proxy that
/// takes the site through Matgate's relay, and the sign-ins Matgate does for a site - HTTP Basic with the connection's login (the browser
/// asks <see cref="BasicCredentials"/>) and Proxmox VE / Backup Server (a ticket as cookie, realms pam, pve, pbs).
/// </summary>
public static class WebSites
{
    /// <summary>The browser's data folder of a connection (cookies, cache).</summary>
    public static string DataFolder(RemoteConnection connection) => Path.Combine(AppPaths.DataDirectory, "web", connection.Id.ToString("N"));

    /// <summary>The address to open (a website connection's own; https:// in front where it has no scheme).</summary>
    public static Uri StartAddress(RemoteConnection connection) =>
        Uri.TryCreate(LibraryService.WebAddress(connection.Url.Length > 0 ? connection.Url : connection.Host), UriKind.Absolute, out var url)
            ? url
            : throw new BusinessException("Die Adresse der Website ist ungültig.", nameof(RemoteConnection.Url));

    /// <summary>The login for HTTP Basic (asked by the browser on a 401): the connection's saved user and password, if there are.</summary>
    public static (string UserName, string Password)? BasicCredentials(RemoteConnection connection)
    {
        var login = SessionTargets.LoginOf(connection);
        return login.UserName.Length > 0 && login.Password.Length > 0 ? (login.UserName, login.Password) : null;
    }

    /// <summary>
    /// Proxmox VE (port 8006) or Backup Server (8007): signs in with the connection's login as Matgate does and gives the cookie
    /// (PVEAuthCookie / PBSAuthCookie); null when it is no Proxmox, there is no login, or it was refused.
    /// </summary>
    public static async Task<WebCookie?> ProxmoxSignInAsync(RemoteConnection connection, HttpMessageHandler? handler, CancellationToken cancellationToken)
    {
        var url = StartAddress(connection);
        var login = SessionTargets.LoginOf(connection);
        if (url.Port is not (8006 or 8007) || login.UserName.Length == 0 || login.Password.Length == 0)
        {
            return null;
        }

        var backup = url.Port == 8007;
        var cookieName = backup ? "PBSAuthCookie" : "PVEAuthCookie";
        using var http = new HttpClient(handler ?? new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = connection.IgnoreCertificate ? (_, _, _, _) => true : null,
        }, disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(15) };
        var realms = login.UserName.Contains('@', StringComparison.Ordinal) ? [""] : backup ? new[] { "@pam", "@pbs" } : ["@pam", "@pve"];
        foreach (var realm in realms)
        {
            try
            {
                using var response = await http.PostAsync(new Uri(url, "/api2/json/access/ticket"), new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = login.UserName + realm,
                    ["password"] = login.Password,
                }), cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (json.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("ticket", out var ticket) && ticket.GetString() is { Length: > 0 } value)
                {
                    Log.Info($"Web {connection.Id}: signed in at Proxmox ({realm.TrimStart('@')}).");
                    return new WebCookie(cookieName, value, url.Host, "/");
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                Log.Warning($"Web {connection.Id}: Proxmox sign-in: {exception.Message}");
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// A website über Matgate: a proxy on 127.0.0.1 for the browser. Connections to the site's host and port go through Matgate's relay
    /// (the browser still speaks TLS with the site itself), everything else goes directly. Null for a direct connection.
    /// </summary>
    public static async Task<WebRelayProxy?> ProxyAsync(RemoteConnection connection, CancellationToken cancellationToken)
    {
        if (connection.Via != ConnectVia.Matgate)
        {
            return null;
        }

        var url = StartAddress(connection);
        var relay = await MatgateRelay.OpenAsync(connection, cancellationToken);
        return WebRelayProxy.Start(url.Host, url.Port, relay);
    }
}

/// <summary>
/// An HTTP proxy on 127.0.0.1 for one website über Matgate: CONNECT (https) and plain requests (http) to the site's host and port go to the
/// local end of its relay, every other host directly. Ends with <see cref="Dispose"/>.
/// </summary>
public sealed class WebRelayProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _host;
    private readonly int _port;
    private readonly IPEndPoint _relay;

    private WebRelayProxy(string host, int port, IPEndPoint relay)
    {
        _host = host;
        _port = port;
        _relay = relay;
    }

    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>"http://127.0.0.1:port" for the browser's proxy setting.</summary>
    public string Address => $"http://127.0.0.1:{EndPoint.Port}";

    public static WebRelayProxy Start(string host, int port, IPEndPoint relay)
    {
        var proxy = new WebRelayProxy(host, port, relay);
        proxy._listener.Start();
        _ = proxy.AcceptAsync();
        return proxy;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        try
        {
            var head = await ReadHeadAsync(stream);
            if (head is null)
            {
                return;
            }

            var firstLine = head[..head.IndexOf("\r\n", StringComparison.Ordinal)];
            var parts = firstLine.Split(' ');
            if (parts.Length < 3)
            {
                return;
            }

            string host;
            int port;
            var isConnect = parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase);
            if (isConnect)
            {
                var cut = parts[1].LastIndexOf(':');
                host = parts[1][..cut].Trim('[', ']');
                port = int.Parse(parts[1][(cut + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (Uri.TryCreate(parts[1], UriKind.Absolute, out var target))
            {
                host = target.Host;
                port = target.Port;
            }
            else
            {
                return;
            }

            using var upstream = new TcpClient { NoDelay = true };
            var throughRelay = host.Equals(_host, StringComparison.OrdinalIgnoreCase) && port == _port;
            if (throughRelay)
            {
                await upstream.ConnectAsync(_relay.Address, _relay.Port, _stop.Token);
            }
            else
            {
                await upstream.ConnectAsync(host, port, _stop.Token);
            }

            var remote = upstream.GetStream();
            if (isConnect)
            {
                await stream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray(), _stop.Token);
            }
            else
            {
                // plain http: the request goes on as it came (absolute form is fine for servers)
                await remote.WriteAsync(Encoding.Latin1.GetBytes(head), _stop.Token);
            }

            var up = stream.CopyToAsync(remote, _stop.Token);
            var down = remote.CopyToAsync(stream, _stop.Token);
            await Task.WhenAny(up, down);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException or FormatException)
        {
            // the browser or the site closed - nothing to do
        }
    }

    /// <summary>The request head (up to the empty line), at most 64 KB.</summary>
    private async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (buffer.Count < 65536)
        {
            if (await stream.ReadAsync(one, _stop.Token) == 0)
            {
                return null;
            }

            buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer[^1] == '\n' && buffer[^2] == '\r' && buffer[^3] == '\n' && buffer[^4] == '\r')
            {
                return Encoding.Latin1.GetString([.. buffer]);
            }
        }

        return null;
    }
}
