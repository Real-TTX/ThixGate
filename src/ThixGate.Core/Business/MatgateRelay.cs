using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>
/// "Verbinden über Matgate": a port on 127.0.0.1 whose connections go as WebSockets to Matgate's relay
/// (<c>/app/api/relay/&lt;connection&gt;</c>), where Matgate opens host and port of its connection - through its tunnel, if it has one.
/// The engine (RDP, VNC) and ssh connect to that port as if it were the host; login and password stay ThixGate's. The port closes a
/// minute after its last connection ended (or after a minute without any).
/// </summary>
public static class MatgateRelay
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(1);
    private static readonly List<Forwarder> Forwarders = [];

    /// <summary>Tests connect the WebSocket themselves (uri, token).</summary>
    internal static Func<Uri, string, CancellationToken, Task<WebSocket>>? ConnectForTests { get; set; }

    /// <summary>
    /// Opens the relay of a connection "über Matgate" and returns the local end to connect to. The first WebSocket is opened here, so a
    /// refusal (signed out, no permission, host not reachable from Matgate) arrives as a message before the session starts.
    /// </summary>
    public static async Task<IPEndPoint> OpenAsync(RemoteConnection connection, CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureMatgateAllowed();
        if (connection.Via != ConnectVia.Matgate)
        {
            throw new BusinessException("Diese Verbindung geht direkt, nicht über Matgate.");
        }

        var account = (connection.MatgateAccountId is { } accountId ? MatgateService.Find(accountId) : null)
            ?? throw new BusinessException("Das Matgate dieser Verbindung ist auf diesem Computer nicht eingerichtet (Gateway › Verbinden über).");
        var token = MatgateService.ReadToken(account)
            ?? throw new BusinessException($"Dieser Computer ist bei „{account.Name}“ nicht angemeldet (Matgate › Anmelden).");
        if (connection.MatgateConnectionId is not { } matgateConnection)
        {
            throw new BusinessException("Bitte unter „Gateway“ die Verbindung in Matgate wählen, über die verbunden wird.");
        }

        var uri = RelayUri(account.ServerUrl, matgateConnection);
        var first = await ConnectAsync(uri, token, account, cancellationToken);
        var forwarder = new Forwarder(uri, token, account, first);
        lock (Forwarders)
        {
            Forwarders.Add(forwarder);
        }

        forwarder.Start();
        Log.Info($"Relay over Matgate {account.Id} to {matgateConnection} on 127.0.0.1:{forwarder.EndPoint.Port}.");
        return forwarder.EndPoint;
    }

    /// <summary>Closes every relay (ThixGate ends).</summary>
    public static void CloseAll()
    {
        List<Forwarder> all;
        lock (Forwarders)
        {
            all = [.. Forwarders];
            Forwarders.Clear();
        }

        foreach (var forwarder in all)
        {
            forwarder.Dispose();
        }
    }

    /// <summary>How many relays are open (tests).</summary>
    internal static int OpenCount
    {
        get
        {
            lock (Forwarders)
            {
                return Forwarders.Count;
            }
        }
    }

    internal static Uri RelayUri(string serverUrl, Guid connectionId)
    {
        var builder = new UriBuilder(serverUrl.TrimEnd('/') + "/app/api/relay/" + connectionId.ToString("D"));
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        return builder.Uri;
    }

    // A WebSocket to Matgate's app API with the device token (the relay, and the session on Matgate: MatgateScreen); refusals as messages.
    internal static async Task<WebSocket> ConnectAsync(Uri uri, string token, MatgateAccount account, CancellationToken cancellationToken)
    {
        if (ConnectForTests is { } connect)
        {
            return await connect(uri, token, cancellationToken);
        }

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        socket.Options.SetRequestHeader("User-Agent", CloudClient.UserAgent);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await socket.ConnectAsync(uri, timeout.Token);
            return socket;
        }
        catch (Exception exception) when (exception is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            var status = socket.HttpStatusCode;
            socket.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            Log.Warning($"Relay over Matgate {account.Id}: {(int)status} {exception.Message}");
            throw new BusinessException(status switch
            {
                HttpStatusCode.Unauthorized => $"Dieser Computer ist bei „{account.Name}“ nicht mehr angemeldet – bitte neu anmelden (Matgate › Anmelden).",
                HttpStatusCode.NotFound => $"Diese Verbindung gibt es in „{account.Name}“ nicht (mehr), oder du darfst dich nicht mit ihr verbinden.",
                HttpStatusCode.BadGateway => $"„{account.Name}“ erreicht den Host dieser Verbindung nicht.",
                HttpStatusCode.TooManyRequests => $"„{account.Name}“ nimmt gerade keine weitere Verbindung an – bitte gleich noch einmal.",
                0 => $"„{account.Name}“ ist unter {account.ServerUrl} nicht erreichbar.",
                _ => $"„{account.Name}“ hat die Verbindung abgelehnt ({(int)status}).",
            });
        }
    }

    // Carries bytes both ways until one side ends; the WebSocket is closed with its handshake (a cancelled receive would abort it).
    internal static async Task PumpAsync(WebSocket socket, Stream stream, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task FromSocket()
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(buffer, stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    await stream.WriteAsync(buffer.AsMemory(0, result.Count), stop.Token);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        async Task FromStream()
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, stop.Token);
                    if (read == 0)
                    {
                        return;
                    }

                    await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, true, stop.Token);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        var fromSocket = FromSocket();
        var fromStream = FromStream();
        try
        {
            await Task.WhenAny(fromSocket, fromStream);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closing.Token);
            }

            await Task.WhenAny(fromSocket, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
        }
        catch (Exception exception) when (IsGone(exception))
        {
            // One side went away.
        }
        finally
        {
            stop.Cancel();
            try
            {
                await Task.WhenAll(fromSocket, fromStream);
            }
            catch (Exception exception) when (IsGone(exception))
            {
                // Ended by the cancel above.
            }
        }
    }

    private static bool IsGone(Exception exception) =>
        exception is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException or SocketException;

    // One local port and the WebSockets behind its connections.
    private sealed class Forwarder(Uri uri, string token, MatgateAccount account, WebSocket first) : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private WebSocket? _first = first;
        private int _active;
        private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;

        public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

        public void Start()
        {
            _listener.Start();
            _ = AcceptAsync();
            _ = WatchAsync();
        }

        public void Dispose()
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            _stop.Cancel();
            _listener.Stop();
            Interlocked.Exchange(ref _first, null)?.Dispose();
            lock (Forwarders)
            {
                Forwarders.Remove(this);
            }
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
                catch (Exception exception) when (IsGone(exception))
                {
                    return;
                }

                _ = CarryAsync(client);
            }
        }

        private async Task CarryAsync(TcpClient client)
        {
            Interlocked.Increment(ref _active);
            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    using var socket = Interlocked.Exchange(ref _first, null) ?? await ConnectAsync(uri, token, account, _stop.Token);
                    await PumpAsync(socket, client.GetStream(), _stop.Token);
                }
            }
            catch (Exception exception) when (exception is BusinessException || IsGone(exception))
            {
                Log.Warning($"Relay over Matgate {account.Id}: {exception.Message}");
            }
            finally
            {
                _lastActivity = DateTimeOffset.UtcNow;
                Interlocked.Decrement(ref _active);
            }
        }

        // Closes the port once nothing went through it for a while.
        private async Task WatchAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
                    if (Volatile.Read(ref _active) == 0 && DateTimeOffset.UtcNow - _lastActivity > Idle)
                    {
                        Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Closed.
            }
        }
    }
}
