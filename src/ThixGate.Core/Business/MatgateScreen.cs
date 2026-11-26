using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Matgate.Display;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>
/// "Sitzung auf Matgate" (Matgate's docs/thixgate-plan.md, stage 3): the remote desktop runs in Matgate - with Matgate's login, the one
/// only Matgate knows - and this backend shows it. It is an <see cref="IScreenBackend"/> like the engine's own RDP and VNC clients, so the
/// session window does not know the difference. The WebSocket (<c>/app/api/screen/&lt;connection&gt;</c>) speaks Matgate's screen
/// protocol (frames of a 4-byte header length, a JSON header and a payload); the picture comes as "screen.tiles" - pieces of the screen,
/// BGRX rows in zlib - each acknowledged, which paces Matgate. Keys and pointer go as "screen.input" in the engine's own form; for a
/// connection the user may only watch, Matgate passes nothing on. No clipboard and no sound yet.
/// </summary>
public sealed class MatgateScreen : IScreenBackend
{
    /// <summary>The largest message accepted: a whole 4096 x 2304 screen that did not compress at all.</summary>
    private const int MaxMessageBytes = 48 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Uri _uri;
    private readonly string _token;
    private readonly MatgateAccount _account;
    private readonly IScreenHost _host;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _frameGate = new();

    private WebSocket? _socket;
    private Task? _reading;
    private byte[]? _pixels;
    private int _width;
    private int _height;
    private int _colours;

    private MatgateScreen(Uri uri, string token, MatgateAccount account, IScreenHost host)
    {
        _uri = uri;
        _token = token;
        _account = account;
        _host = host;
    }

    /// <summary>The session of a connection "über Matgate" with <see cref="RemoteConnection.SessionOnMatgate"/>, sized for the window.</summary>
    public static MatgateScreen For(RemoteConnection connection, int fitWidth, int fitHeight, IScreenHost host)
    {
        AccessPolicy.EnsureMatgateAllowed();
        var account = (connection.MatgateAccountId is { } accountId ? MatgateService.Find(accountId) : null)
            ?? throw new BusinessException("Das Matgate dieser Verbindung ist auf diesem Computer nicht eingerichtet (Gateway › Verbinden über).");
        var token = MatgateService.ReadToken(account)
            ?? throw new BusinessException($"Dieser Computer ist bei „{account.Name}“ nicht angemeldet (Matgate › Anmelden).");
        if (connection.MatgateConnectionId is not { } matgateConnection)
        {
            throw new BusinessException("Bitte unter „Gateway“ die Verbindung in Matgate wählen.");
        }

        if (!connection.Protocol.IsScreen())
        {
            throw new BusinessException("Eine Sitzung auf Matgate gibt es für Remote Desktop und VNC.");
        }

        return new MatgateScreen(ScreenUri(account.ServerUrl, matgateConnection, SessionTargets.QualityName(connection.Quality), connection.Resolution, fitWidth, fitHeight), token, account, host);
    }

    internal static Uri ScreenUri(string serverUrl, Guid connectionId, string quality, string resolution, int fitWidth, int fitHeight)
    {
        var builder = new UriBuilder(serverUrl.TrimEnd('/') + "/app/api/screen/" + connectionId.ToString("D"))
        {
            Query = $"quality={Uri.EscapeDataString(quality)}&resolution={Uri.EscapeDataString(resolution)}&fitWidth={Math.Max(0, fitWidth)}&fitHeight={Math.Max(0, fitHeight)}",
        };
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        return builder.Uri;
    }

    /// <summary>Only to watch: Matgate passes no key and no pointer on.</summary>
    public bool WatchOnly { get; private set; }

    public string Name => _account.Name;

    public bool ClipImages => false;

    public bool ClipFiles => false;

    public bool Sound => false;

    public int ColorDepth => _colours;

    public bool QualityColours => false;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _socket = await MatgateRelay.ConnectAsync(_uri, _token, _account, cancellationToken);
        _reading = Task.Run(ReadAsync, CancellationToken.None);
        using var registration = cancellationToken.Register(() => _ready.TrySetCanceled(cancellationToken));
        await _ready.Task;
    }

    public async Task<string?> RunAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => _ended.TrySetResult(null));
        var reason = await _ended.Task;
        if (cancellationToken.IsCancellationRequested)
        {
            await SendAsync("screen.close", []);
        }

        return reason;
    }

    public bool WithFrame(FrameReader reader)
    {
        lock (_frameGate)
        {
            if (_pixels is null)
            {
                return false;
            }

            reader(_pixels, _width * 4, _width, _height);
            return true;
        }
    }

    public void Input(JsonObject data) => Post("screen.input", data);

    public void Resize(int width, int height) => Post("screen.config", new JsonObject { ["fitWidth"] = width, ["fitHeight"] = height });

    public void Clip(string op, JsonObject data, ReadOnlyMemory<byte> payload)
    {
        // The clipboard of a session on Matgate comes with the clipboard of the session window.
    }

    public Task<ClipPiece> ClipReadAsync(string token, int index, long offset, CancellationToken cancellationToken) =>
        Task.FromException<ClipPiece>(new ScreenException("Die Zwischenablage einer Sitzung auf Matgate gibt es noch nicht."));

    public void SecureAttention() => Post("screen.sas", []);

    public void SoundEnabled(bool on)
    {
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _ready.TrySetCanceled();
        _ended.TrySetResult(null);
        if (_socket is { } socket)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
                }
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
            }

            if (_reading is { } reading)
            {
                await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(3)));
            }

            socket.Dispose();
        }
    }

    // ---- Matgate's messages ----

    private async Task ReadAsync()
    {
        string? reason = "Matgate hat die Sitzung beendet.";
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var message = await ReceiveAsync(_socket!, _stop.Token);
                if (message is null)
                {
                    break;
                }

                var (op, data, payload) = Decode(message.Value);
                if (op == "screen.ended")
                {
                    reason = ScreenText(data, "message");
                    if (reason is null && ScreenText(data, "reason") == "taken")
                    {
                        reason = "Die Sitzung wird jetzt an anderer Stelle angezeigt.";
                    }

                    break;
                }

                if (op == "screen.error")
                {
                    reason = ScreenText(data, "message") ?? "Matgate konnte die Sitzung nicht öffnen.";
                    _ready.TrySetException(new ScreenException(reason));
                    break;
                }

                Handle(op, data, payload);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or InvalidDataException or JsonException or ObjectDisposedException)
        {
            if (!_stop.IsCancellationRequested)
            {
                Log.Warning($"Session on Matgate {_account.Id}: {exception.GetType().Name}: {exception.Message}");
                reason = "Die Verbindung zu Matgate ist abgerissen.";
            }
        }

        _ready.TrySetException(new ScreenException(reason ?? "Matgate hat die Sitzung beendet."));
        _ended.TrySetResult(reason);
    }

    private void Handle(string op, JsonObject data, ReadOnlyMemory<byte> payload)
    {
        switch (op)
        {
            case "screen.ready":
                WatchOnly = data["watchOnly"]?.GetValue<bool>() == true;
                _colours = Number(data, "colours");
                lock (_frameGate)
                {
                    Allocate(Number(data, "width"), Number(data, "height"));
                }

                if (WatchOnly)
                {
                    _host.Status("watch-only", "Nur ansehen");
                }

                _ready.TrySetResult();
                break;
            case "screen.monitor":
                lock (_frameGate)
                {
                    Allocate(Number(data, "width"), Number(data, "height"));
                }

                break;
            case "screen.tiles":
                if (Tile(data, payload) is { } changed)
                {
                    _host.Dirty(changed.X, changed.Y, changed.Width, changed.Height);
                }

                Post("screen.ack", new JsonObject { ["seq"] = data["seq"]?.GetValue<long>() ?? 0 });
                break;
            case "screen.cursor":
                if (ScreenText(data, "format") == "rgba")
                {
                    var (width, height) = (Number(data, "width"), Number(data, "height"));
                    if (width > 0 && height > 0 && payload.Length >= width * height * 4)
                    {
                        _host.Cursor(Number(data, "x"), Number(data, "y"), width, height, payload.Span[..(width * height * 4)]);
                    }
                }
                else
                {
                    _host.CursorDefault();
                }

                break;
            case "screen.pointer":
                _host.CursorHidden(data["visible"]?.GetValue<bool>() == false);
                break;
            case "screen.status":
                _host.Status(ScreenText(data, "state") ?? "", ScreenText(data, "message"));
                break;
            case "screen.auth":
                _ = AnswerAsync(data);
                break;
        }
    }

    // Matgate asks for what is missing to sign in; the answer goes to Matgate (which signs in), never into ThixGate's vault.
    private async Task AnswerAsync(JsonObject data)
    {
        try
        {
            var need = data["need"] is JsonArray fields ? fields.Select(field => field?.GetValue<string>() ?? "").Where(field => field.Length > 0).ToArray() : [];
            var answer = await _host.AskAsync(need, ScreenText(data, "username") ?? "", ScreenText(data, "domain") ?? "", data["failed"]?.GetValue<bool>() == true, _stop.Token);
            await SendAsync("screen.auth", answer is null
                ? new JsonObject { ["cancel"] = true }
                : new JsonObject { ["username"] = answer.UserName, ["password"] = answer.Password, ["domain"] = answer.Domain });
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or FormatException)
        {
        }
    }

    // A piece of the screen into the picture; returns where it went.
    private (int X, int Y, int Width, int Height)? Tile(JsonObject data, ReadOnlyMemory<byte> payload)
    {
        if (ScreenText(data, "format") != "bgrx-zlib")
        {
            return null;
        }

        var (x, y, width, height) = (Number(data, "x"), Number(data, "y"), Number(data, "width"), Number(data, "height"));
        var (screenWidth, screenHeight) = (Number(data, "screenWidth"), Number(data, "screenHeight"));
        lock (_frameGate)
        {
            if (screenWidth != _width || screenHeight != _height)
            {
                Allocate(screenWidth, screenHeight);
            }

            if (_pixels is null || width <= 0 || height <= 0 || x < 0 || y < 0 || x + width > _width || y + height > _height)
            {
                return null;
            }

            using var zlib = new ZLibStream(new MemoryStream(payload.ToArray(), writable: false), CompressionMode.Decompress);
            var row = width * 4;
            for (var line = 0; line < height; line++)
            {
                zlib.ReadExactly(_pixels.AsSpan((y + line) * _width * 4 + x * 4, row));
            }
        }

        return (x, y, width, height);
    }

    private void Allocate(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || (width == _width && height == _height && _pixels is not null))
        {
            return;
        }

        _pixels = new byte[width * height * 4];
        _width = width;
        _height = height;
    }

    private void Post(string op, JsonObject data) => _ = SendAsync(op, data);

    private async Task SendAsync(string op, JsonObject data)
    {
        if (_socket is not { State: WebSocketState.Open } socket)
        {
            return;
        }

        var message = Encode(op, data);
        try
        {
            await _sending.WaitAsync(_stop.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, _stop.Token);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _sending.Release();
        }
    }

    // ---- the wire (Matgate's AgentWire) ----

    internal static byte[] Encode(string op, JsonObject data, ReadOnlySpan<byte> payload = default)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["kind"] = "evt", ["op"] = op, ["data"] = data }, Json);
        var message = new byte[4 + header.Length + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(message, header.Length);
        header.CopyTo(message, 4);
        payload.CopyTo(message.AsSpan(4 + header.Length));
        return message;
    }

    internal static (string Op, JsonObject Data, ReadOnlyMemory<byte> Payload) Decode(ReadOnlyMemory<byte> message)
    {
        if (message.Length < 4)
        {
            throw new InvalidDataException("Message too short.");
        }

        var headerLength = BinaryPrimitives.ReadInt32BigEndian(message.Span);
        if (headerLength < 2 || headerLength > message.Length - 4)
        {
            throw new InvalidDataException("Invalid header length.");
        }

        var header = JsonNode.Parse(message.Span.Slice(4, headerLength)) as JsonObject ?? throw new InvalidDataException("No header.");
        var data = header["data"] as JsonObject ?? [];
        header.Remove("data");
        return (header["op"]?.GetValue<string>() ?? "", data, message[(4 + headerLength)..]);
    }

    private static async Task<ReadOnlyMemory<byte>?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                throw new InvalidDataException("Message too large.");
            }

            if (result.EndOfMessage)
            {
                return message.GetBuffer().AsMemory(0, (int)message.Length);
            }
        }
    }

    private static int Number(JsonObject data, string name) =>
        data[name] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? (int)Math.Clamp(number, int.MinValue, int.MaxValue) : 0;

    private static string? ScreenText(JsonObject data, string name) =>
        data[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
