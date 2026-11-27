using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThixGate.Core.Business;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Tests;

/// <summary>Matgate's app API from ThixGate's side: the sign-in through the browser, the library, taking over, the relay.</summary>
public sealed class MatgateTests
{
    private const string Server = "https://matgate.test";

    /// <summary>Matgate's app API as far as ThixGate uses it: /app/api/token, /library, /logout.</summary>
    private sealed class FakeMatgate : HttpMessageHandler
    {
        public const string Token = "mgd_test-token";
        public static readonly Guid DeviceId = Guid.Parse("0d000000-0000-4000-8000-000000000001");
        public string? Challenge { get; set; }
        public string Code { get; } = "one-time-code";
        public bool TokenValid { get; set; } = true;
        public List<string?> IfNoneMatch { get; } = [];
        public int LogoutCalls { get; private set; }

        public MatgateLibrary Library { get; set; } = new()
        {
            Revision = "r1",
            User = new MatgateLibraryUser { UserName = "anna", DisplayName = "Anna Schmidt" },
            Folders = [new() { Path = "Labor", Name = "Labor" }, new() { Path = "Labor/Linux", Name = "Linux", Parent = "Labor" }],
            Connections =
            [
                new() { Id = Guid.Parse("0c000000-0000-4000-8000-000000000001"), Name = "Web", Protocol = "rdp", Host = "web.lab", Port = 3390, UserName = "admin", Domain = "LAB", Folder = "Labor/Linux", KeyboardLayout = "en-us-qwerty", Level = "Connect" },
                new() { Id = Guid.Parse("0c000000-0000-4000-8000-000000000002"), Name = "Shell", Protocol = "ssh", Host = "ssh.lab", Port = 22, UserName = "root", Level = "Connect" },
            ],
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/app/api/token")
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement;
                var verifier = body.GetProperty("codeVerifier").GetString()!;
                var computed = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                if (body.GetProperty("code").GetString() != Code || computed != Challenge || !body.GetProperty("redirectUri").GetString()!.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }

                return Json(new { token = Token, deviceId = DeviceId, user = new { userName = "anna", displayName = "Anna Schmidt" } });
            }

            if (request.Headers.Authorization?.Parameter != Token || !TokenValid)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            switch (path)
            {
                case "/app/api/library":
                    var tag = request.Headers.IfNoneMatch.FirstOrDefault()?.Tag;
                    IfNoneMatch.Add(tag);
                    return tag == "\"" + Library.Revision + "\""
                        ? new HttpResponseMessage(HttpStatusCode.NotModified)
                        : Json(Library);
                case "/app/api/logout":
                    LogoutCalls++;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case "/app/api/relay/0c000000-0000-4000-8000-000000000001/latency":
                    return Json(new { hostMs = 23.5 });
            }

            // Writing back (stage 3): every request kept as it came; a new or changed connection lands in the library.
            if (path.StartsWith("/app/api/connections", StringComparison.Ordinal))
            {
                var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                Writes.Add((request.Method, path, text));
                if (WriteAnswer is { } answer)
                {
                    return answer;
                }

                if (request.Method == HttpMethod.Delete)
                {
                    var gone = Guid.Parse(path[(path.LastIndexOf('/') + 1)..]);
                    Library = Library with { Revision = Library.Revision + "-", Connections = [.. Library.Connections.Where(item => item.Id != gone)] };
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                var body = JsonDocument.Parse(text).RootElement;
                var id = request.Method == HttpMethod.Put ? Guid.Parse(path[(path.LastIndexOf('/') + 1)..]) : Guid.NewGuid();
                var item = new MatgateConnectionItem
                {
                    Id = id, Name = body.GetProperty("name").GetString()!, Protocol = body.GetProperty("protocol").GetString()!,
                    Host = body.GetProperty("host").GetString()!, Folder = body.GetProperty("folder").GetString()!, Level = "Manage", CanDelete = true,
                    IsPrivate = body.TryGetProperty("shared", out var shared) && shared.ValueKind == JsonValueKind.False,
                };
                Library = Library with { Revision = Library.Revision + "+", Connections = [.. Library.Connections.Where(existing => existing.Id != id), item] };
                return new HttpResponseMessage(request.Method == HttpMethod.Put ? HttpStatusCode.OK : HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(item, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        public List<(HttpMethod Method, string Path, string Body)> Writes { get; } = [];

        /// <summary>Set: the answer to every write (a refusal).</summary>
        public HttpResponseMessage? WriteAnswer { get; set; }

        private static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
    }

    private static MatgateAccount AddAccount() =>
        MatgateService.Save(new MatgateAccount { Name = "Matgate Labor", ServerUrl = Server });

    // The browser: reads the question's address, "allows" and goes back to ThixGate's address on this computer.
    private static Action<string> Browser(FakeMatgate matgate, Func<IReadOnlyDictionary<string, string>, string>? answer = null) => url =>
    {
        var query = LoopbackCallback.ParseQuery(new Uri(url).Query.TrimStart('?'));
        Assert.Equal("thixgate", query["client_id"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(AppInfo.MachineName, query["device"]);
        Assert.StartsWith(Server + "/app/authorize?", url, StringComparison.Ordinal);
        matgate.Challenge = query["code_challenge"];
        var back = query["redirect_uri"] + "?" + (answer?.Invoke(query) ?? $"code={matgate.Code}&state={Uri.EscapeDataString(query["state"])}");
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient();
            var page = await http.GetStringAsync(back);
            Assert.Contains("schließen", page, StringComparison.Ordinal);
        });
    };

    [Fact]
    public async Task MatgateTellsHowFarTheHostIsFromIt()
    {
        using var data = new TestData();
        var matgate = new FakeMatgate();
        MatgateService.HandlerForTests = () => matgate;
        var account = await MatgateService.SignInAsync(AddAccount().Id, Browser(matgate));
        var connection = new RemoteConnection { Name = "Web", Host = "web.lab", Via = ConnectVia.Matgate, MatgateAccountId = account.Id, MatgateConnectionId = Guid.Parse("0c000000-0000-4000-8000-000000000001") };

        Assert.Equal(TimeSpan.FromMilliseconds(23.5), await MatgateRelay.HostRoundTripAsync(connection));

        // A Matgate without the question (older), or a connection it does not know: nothing to tell, no error.
        Assert.Null(await MatgateRelay.HostRoundTripAsync(connection with { MatgateConnectionId = Guid.NewGuid() }));
        Assert.Null(await MatgateRelay.HostRoundTripAsync(connection with { Via = ConnectVia.Direct, MatgateAccountId = null }));
    }

    [Fact]
    public async Task SignInGoesThroughTheBrowserAndKeepsTheTokenProtected()
    {
        using var data = new TestData();
        var matgate = new FakeMatgate();
        MatgateService.HandlerForTests = () => matgate;
        var account = AddAccount();

        var signedIn = await MatgateService.SignInAsync(account.Id, Browser(matgate));

        Assert.True(signedIn.IsSignedIn);
        Assert.Equal((FakeMatgate.DeviceId, "anna", "Anna Schmidt"), (signedIn.DeviceId!.Value, signedIn.UserName, signedIn.DisplayName));
        Assert.Equal(FakeMatgate.Token, MatgateService.ReadToken(signedIn));
        Assert.DoesNotContain(FakeMatgate.Token, File.ReadAllText(AppPaths.SettingsFile));

        await MatgateService.SignOutAsync(account.Id);
        Assert.Equal(1, matgate.LogoutCalls);
        Assert.False(MatgateService.Find(account.Id)!.IsSignedIn);
        Assert.Null(MatgateService.ReadToken(MatgateService.Find(account.Id)!));
    }

    [Fact]
    public async Task ACancelledOrForeignAnswerSignsNobodyIn()
    {
        using var data = new TestData();
        var matgate = new FakeMatgate();
        MatgateService.HandlerForTests = () => matgate;
        var account = AddAccount();

        var denied = await Assert.ThrowsAsync<BusinessException>(() => MatgateService.SignInAsync(account.Id,
            Browser(matgate, query => $"error=access_denied&state={Uri.EscapeDataString(query["state"])}")));
        Assert.Contains("abgebrochen", denied.Message, StringComparison.Ordinal);

        var foreign = await Assert.ThrowsAsync<BusinessException>(() => MatgateService.SignInAsync(account.Id,
            Browser(matgate, _ => $"code={matgate.Code}&state=someone-elses")));
        Assert.Contains("passt nicht", foreign.Message, StringComparison.Ordinal);
        Assert.False(MatgateService.Find(account.Id)!.IsSignedIn);
    }

    [Fact]
    public async Task TheLibraryIsKeptAndOnlyReadAgainWhenItChanged()
    {
        using var data = new TestData();
        var matgate = new FakeMatgate();
        MatgateService.HandlerForTests = () => matgate;
        var account = AddAccount();
        await MatgateService.SignInAsync(account.Id, Browser(matgate));

        var library = await MatgateService.RefreshLibraryAsync(account.Id);
        Assert.Equal(["Web", "Shell"], library.Connections.Select(connection => connection.Name));
        Assert.Equal(library, MatgateService.Library(account.Id));

        await MatgateService.RefreshLibraryAsync(account.Id);
        Assert.Equal([null, "\"r1\""], matgate.IfNoneMatch);
        Assert.NotNull(MatgateService.Find(account.Id)!.LibraryFetched);

        // Signed out in Matgate (Konto › Geräte): the next read says so, and this computer forgets the token.
        matgate.TokenValid = false;
        var signedOut = await Assert.ThrowsAsync<BusinessException>(() => MatgateService.RefreshLibraryAsync(account.Id));
        Assert.Contains("nicht mehr angemeldet", signedOut.Message, StringComparison.Ordinal);
        Assert.False(MatgateService.Find(account.Id)!.IsSignedIn);
        Assert.Null(MatgateService.Library(account.Id));
    }

    [Fact]
    public async Task TakingOverCreatesTheFoldersAndConnectsOverMatgate()
    {
        using var data = new TestData();
        var matgate = new FakeMatgate();
        MatgateService.HandlerForTests = () => matgate;
        var account = AddAccount();
        await MatgateService.SignInAsync(account.Id, Browser(matgate));
        await MatgateService.RefreshLibraryAsync(account.Id);
        var web = matgate.Library.Connections[0];

        var taken = MatgateService.TakeOver(account.Id, web.Id);

        Assert.Equal(("Web", ConnectionProtocol.Rdp, "web.lab", 3390, "admin", "LAB"), (taken.Name, taken.Protocol, taken.Host, taken.Port, taken.UserName, taken.Domain));
        Assert.Equal((ConnectVia.Matgate, account.Id, web.Id), (taken.Via, taken.MatgateAccountId!.Value, taken.MatgateConnectionId!.Value));
        Assert.Equal(0x409u, taken.KeyboardLayout);
        Assert.Equal("Labor › Linux", LibraryService.FolderPath(taken.FolderId));
        Assert.Equal(taken.Id, MatgateService.TakeOver(account.Id, web.Id).Id);
        Assert.Single(LibraryService.Connections());

        // A second one into the same folder tree makes no second "Labor".
        MatgateService.TakeOver(account.Id, matgate.Library.Connections[1].Id);
        Assert.Equal(2, LibraryService.Folders().Count);
    }

    [Fact]
    public void AConnectionOverMatgateNamesItsConnectionThere()
    {
        using var data = new TestData();
        var account = AddAccount();
        var connection = new RemoteConnection { Name = "X", Host = "x.lab", Via = ConnectVia.Matgate, MatgateAccountId = account.Id };

        var missing = Assert.Throws<BusinessException>(() => LibraryService.SaveConnection(connection));
        Assert.Equal(nameof(RemoteConnection.MatgateConnectionId), missing.Field);

        var saved = LibraryService.SaveConnection(connection with { MatgateConnectionId = Guid.NewGuid() });
        var direct = LibraryService.SaveConnection(saved with { Via = ConnectVia.Direct });
        Assert.Null(direct.MatgateAccountId);
        Assert.Null(direct.MatgateConnectionId);
    }

    [Fact]
    public void SshOverTheRelayGoesToTheLocalPortAndKeepsTheHostKeyName()
    {
        using var data = new TestData();
        var connection = new RemoteConnection { Name = "Git", Protocol = ConnectionProtocol.Ssh, Host = "git.rz.local", UserName = "git" };

        var arguments = SshLauncher.Arguments(connection, new IPEndPoint(IPAddress.Loopback, 50555));

        Assert.Equal(["-p", "50555", "-o", "HostKeyAlias=git.rz.local", "git@127.0.0.1"], arguments);
        Assert.Equal(["git@git.rz.local"], SshLauncher.Arguments(connection));
    }

    [Fact]
    public async Task TheRelayCarriesTheBytesOfALocalConnectionOverAWebSocket()
    {
        using var data = new TestData();
        var matgate = new FakeMatgate();
        MatgateService.HandlerForTests = () => matgate;
        var account = AddAccount();
        await MatgateService.SignInAsync(account.Id, Browser(matgate));
        var connection = new RemoteConnection
        {
            Name = "Web", Host = "web.lab", Via = ConnectVia.Matgate, MatgateAccountId = account.Id, MatgateConnectionId = Guid.Parse("0c000000-0000-4000-8000-000000000001"),
        };

        // "Matgate": the far end of a WebSocket pair answers every message in upper case.
        var asked = new List<(Uri Uri, string Token)>();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        MatgateRelay.ConnectForTests = async (uri, token, cancellationToken) =>
        {
            asked.Add((uri, token));
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
            var far = await listener.AcceptTcpClientAsync(cancellationToken);
            var matgateSide = WebSocket.CreateFromStream(far.GetStream(), isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
            _ = Task.Run(async () =>
            {
                var buffer = new byte[1024];
                while (true)
                {
                    var result = await matgateSide.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await matgateSide.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        return;
                    }

                    var upper = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(buffer, 0, result.Count).ToUpperInvariant());
                    await matgateSide.SendAsync(upper, WebSocketMessageType.Binary, true, CancellationToken.None);
                }
            });
            return WebSocket.CreateFromStream(client.GetStream(), isServer: false, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        };

        var local = await MatgateRelay.OpenAsync(connection);
        Assert.Equal(IPAddress.Loopback, local.Address);
        Assert.Equal(new Uri("wss://matgate.test/app/api/relay/0c000000-0000-4000-8000-000000000001"), asked[0].Uri);
        Assert.Equal(FakeMatgate.Token, asked[0].Token);

        using (var engine = new TcpClient())
        {
            await engine.ConnectAsync(local);
            var stream = engine.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("hello rdp"));
            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("HELLO RDP", Encoding.ASCII.GetString(buffer, 0, read));
        }

        // The first WebSocket was opened before the session (so a refusal shows at once); a second connection gets one of its own.
        using (var again = new TcpClient())
        {
            await again.ConnectAsync(local);
            await again.GetStream().WriteAsync(Encoding.ASCII.GetBytes("vnc"));
            var buffer = new byte[16];
            var read = await again.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("VNC", Encoding.ASCII.GetString(buffer, 0, read));
        }

        Assert.Equal(2, asked.Count);
        Assert.Equal(1, MatgateRelay.OpenCount);
        MatgateRelay.CloseAll();
        Assert.Equal(0, MatgateRelay.OpenCount);
        listener.Stop();
    }

    [Fact]
    public async Task WithoutASignInTheRelayExplainsWhatIsMissing()
    {
        using var data = new TestData();
        var account = AddAccount();
        var connection = new RemoteConnection { Name = "Web", Host = "web.lab", Via = ConnectVia.Matgate, MatgateAccountId = account.Id, MatgateConnectionId = Guid.NewGuid() };

        var problem = await Assert.ThrowsAsync<BusinessException>(() => MatgateRelay.OpenAsync(connection));
        Assert.Contains("nicht angemeldet", problem.Message, StringComparison.Ordinal);

        var target = await Assert.ThrowsAsync<BusinessException>(() => SessionTargets.ResolveAsync(connection, SessionTargets.LoginOf(connection), 0, 0, CancellationToken.None));
        Assert.Equal(problem.Message, target.Message);
    }

    // ---- stage 3: writing back, the session on Matgate ----

    [Fact]
    public async Task WritingBackSendsWhatAConnectionIsNeverAPasswordAndReadsTheLibraryAgain()
    {
        using var data = new TestData();
        var matgate = new FakeMatgate();
        MatgateService.HandlerForTests = () => matgate;
        var account = AddAccount();
        await MatgateService.SignInAsync(account.Id, Browser(matgate));
        await MatgateService.RefreshLibraryAsync(account.Id);

        // The form's checks come first: nothing goes to Matgate.
        var missing = await Assert.ThrowsAsync<BusinessException>(() => MatgateService.SaveConnectionAsync(account.Id, null, new MatgateConnectionDraft { Host = "x" }));
        Assert.Equal(nameof(MatgateConnectionDraft.Name), missing.Field);
        Assert.Empty(matgate.Writes);

        var created = await MatgateService.SaveConnectionAsync(account.Id, null, new MatgateConnectionDraft
        {
            Name = " Neu ", Protocol = ConnectionProtocol.Vnc, Host = "vnc.lab", Domain = "IGNORED", Folder = "Labor\\ Neu /", Shared = false,
        });
        var (method, path, body) = matgate.Writes.Single();
        Assert.Equal((HttpMethod.Post, "/app/api/connections"), (method, path));
        var sent = JsonDocument.Parse(body).RootElement;
        Assert.Equal(("Neu", "vnc", "", "Labor/Neu", false), (sent.GetProperty("name").GetString(), sent.GetProperty("protocol").GetString(),
            sent.GetProperty("domain").GetString(), sent.GetProperty("folder").GetString(), sent.GetProperty("shared").GetBoolean()));
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(MatgateService.Library(account.Id)!.Connections, item => item.Id == created.Id);

        await MatgateService.SaveConnectionAsync(account.Id, created.Id, MatgateConnectionDraft.Of(created) with { Name = "Umbenannt" });
        Assert.Equal((HttpMethod.Put, $"/app/api/connections/{created.Id:D}"), (matgate.Writes[1].Method, matgate.Writes[1].Path));
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(matgate.Writes[1].Body).RootElement.GetProperty("shared").ValueKind);
        Assert.Equal("Umbenannt", MatgateService.Library(account.Id)!.Connections.Single(item => item.Id == created.Id).Name);

        await MatgateService.DeleteConnectionAsync(account.Id, created.Id);
        Assert.DoesNotContain(MatgateService.Library(account.Id)!.Connections, item => item.Id == created.Id);

        // Matgate's refusals, at the field they are about.
        matgate.WriteAnswer = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid", field = "host" }) };
        var invalid = await Assert.ThrowsAsync<BusinessException>(() => MatgateService.SaveConnectionAsync(account.Id, null, new MatgateConnectionDraft { Name = "n", Host = "h" }));
        Assert.Equal(nameof(MatgateConnectionDraft.Host), invalid.Field);
        matgate.WriteAnswer = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { error = "forbidden" }) };
        var forbidden = await Assert.ThrowsAsync<BusinessException>(() => MatgateService.DeleteConnectionAsync(account.Id, Guid.NewGuid()));
        Assert.Contains("darfst du", forbidden.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConnectionWhoseLoginOnlyMatgateHasOrThatMayOnlyBeWatchedRunsOnMatgate()
    {
        using var data = new TestData();
        var account = AddAccount();
        var stored = new MatgateConnectionItem { Id = Guid.NewGuid(), Name = "ERP", Protocol = "rdp", Host = "erp", Level = "Connect", HasStoredLogin = true };
        var watched = new MatgateConnectionItem { Id = Guid.NewGuid(), Name = "Leitstand", Protocol = "vnc", Host = "ls", Level = "ReadOnly" };
        var own = new MatgateConnectionItem { Id = Guid.NewGuid(), Name = "Web", Protocol = "rdp", Host = "web", Level = "Connect" };

        Assert.True(MatgateService.AsConnection(account.Id, stored).SessionOnMatgate);
        Assert.True(MatgateService.AsConnection(account.Id, watched).SessionOnMatgate);
        Assert.False(MatgateService.AsConnection(account.Id, own).SessionOnMatgate);
        Assert.Throws<BusinessException>(() => MatgateService.AsConnection(account.Id, watched with { Protocol = "ssh" }));

        // Direct, or SSH: no session on Matgate.
        var saved = LibraryService.SaveConnection(MatgateService.AsConnection(account.Id, stored) with { Id = Guid.NewGuid() });
        Assert.True(saved.SessionOnMatgate);
        Assert.False(LibraryService.SaveConnection(saved with { Via = ConnectVia.Direct }).SessionOnMatgate);
        Assert.False(LibraryService.SaveConnection(saved with { Protocol = ConnectionProtocol.Ssh }).SessionOnMatgate);
    }

    [Fact]
    public async Task ASessionOnMatgateShowsItsPiecesOfTheScreenAnswersItsQuestionsAndPassesKeysOn()
    {
        using var data = new TestData();
        var matgate = new FakeMatgate();
        MatgateService.HandlerForTests = () => matgate;
        var account = AddAccount();
        await MatgateService.SignInAsync(account.Id, Browser(matgate));
        var matgateConnection = Guid.Parse("0c000000-0000-4000-8000-000000000001");
        var connection = new RemoteConnection
        {
            Name = "ERP", Host = "erp", Via = ConnectVia.Matgate, MatgateAccountId = account.Id, MatgateConnectionId = matgateConnection, SessionOnMatgate = true,
        };

        // "Matgate": the server end of a WebSocket pair, driven by the test.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var matgateSide = new TaskCompletionSource<WebSocket>();
        Uri? asked = null;
        MatgateRelay.ConnectForTests = async (uri, token, cancellationToken) =>
        {
            asked = uri;
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
            var far = await listener.AcceptTcpClientAsync(cancellationToken);
            matgateSide.SetResult(WebSocket.CreateFromStream(far.GetStream(), isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero));
            return WebSocket.CreateFromStream(client.GetStream(), isServer: false, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        };

        var host = new RecordingHost();
        await using var screen = (MatgateScreen)await SessionTargets.BackendAsync(connection, SessionTargets.LoginOf(connection), 1280, 720, host, CancellationToken.None);
        var connecting = screen.ConnectAsync(CancellationToken.None);
        var far = await matgateSide.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal($"wss://matgate.test/app/api/screen/{matgateConnection:D}?quality=auto&resolution=auto&fitWidth=1280&fitHeight=720", asked!.ToString());

        // Matgate asks for the password first; the answer goes back to Matgate.
        await Send(far, "screen.auth", new() { ["need"] = new System.Text.Json.Nodes.JsonArray("password"), ["username"] = "anna" });
        var auth = await Receive(far);
        Assert.Equal(("screen.auth", "geheim"), (auth.Op, auth.Data["password"]!.GetValue<string>()));

        await Send(far, "screen.ready", new() { ["width"] = 4, ["height"] = 2, ["watchOnly"] = true, ["colours"] = 32 });
        await connecting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(screen.WatchOnly);
        Assert.Contains("Nur ansehen", host.Statuses);

        // A piece of the screen: 2 x 1 pixels at (1, 1).
        var pixels = new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 };
        using var packed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(pixels);
        }

        await Send(far, "screen.tiles", new() { ["seq"] = 7, ["format"] = "bgrx-zlib", ["x"] = 1, ["y"] = 1, ["width"] = 2, ["height"] = 1, ["screenWidth"] = 4, ["screenHeight"] = 2 }, packed.ToArray());
        var ack = await Receive(far);
        Assert.Equal(("screen.ack", 7L), (ack.Op, ack.Data["seq"]!.GetValue<long>()));
        Assert.Equal((1, 1, 2, 1), host.Dirty.Single());
        byte[]? frame = null;
        Assert.True(screen.WithFrame((bgrx, stride, width, height) => frame = bgrx.Slice(stride + 4, 8).ToArray()));
        Assert.Equal(pixels, frame);

        screen.Input(new() { ["t"] = "k", ["code"] = 30 });
        var input = await Receive(far);
        Assert.Equal(("screen.input", 30), (input.Op, input.Data["code"]!.GetValue<int>()));

        // The clipboard: text copied there arrives here; while watching nothing goes there, else this computer's text goes as screen.clip.set.
        await Send(far, "screen.clip", new() { ["kind"] = "text" }, System.Text.Encoding.UTF8.GetBytes("von dort"));
        for (var wait = 0; wait < 50 && host.Clips.Count == 0; wait++)
        {
            await Task.Delay(20);
        }

        Assert.Equal("von dort", Assert.Single(host.Clips));

        // Matgate's statistics tell how far the remote desktop is from it.
        await Send(far, "screen.stats", new() { ["fps"] = 30, ["rtt"] = 8, ["hostRtt"] = 31.5 });
        for (var wait = 0; wait < 50 && screen.HostRoundTrip is null; wait++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(TimeSpan.FromMilliseconds(31.5), screen.HostRoundTrip);
        screen.Clip("screen.clip.set", new() { ["paste"] = false }, System.Text.Encoding.UTF8.GetBytes("nicht beim Zusehen"));
        await Send(far, "screen.ready", new() { ["width"] = 4, ["height"] = 2, ["watchOnly"] = false, ["colours"] = 32 });
        for (var wait = 0; wait < 50 && screen.WatchOnly; wait++)
        {
            await Task.Delay(20);
        }

        screen.Clip("screen.clip.set", new() { ["paste"] = false }, System.Text.Encoding.UTF8.GetBytes("von hier"));
        var clip = await ReceiveWithPayload(far);
        Assert.Equal(("screen.clip.set", "von hier"), (clip.Op, System.Text.Encoding.UTF8.GetString(clip.Payload.Span)));

        var running = screen.RunAsync(CancellationToken.None);
        await Send(far, "screen.ended", new() { ["message"] = "Abgemeldet." });
        Assert.Equal("Abgemeldet.", await running.WaitAsync(TimeSpan.FromSeconds(5)));
        listener.Stop();
    }

    private static Task Send(WebSocket socket, string op, System.Text.Json.Nodes.JsonObject data, byte[]? payload = null) =>
        socket.SendAsync(MatgateScreen.Encode(op, data, payload), WebSocketMessageType.Binary, true, CancellationToken.None);

    private static async Task<(string Op, System.Text.Json.Nodes.JsonObject Data)> Receive(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var (op, data, _) = MatgateScreen.Decode(buffer.AsMemory(0, result.Count));
        return (op, data);
    }

    private static async Task<(string Op, System.Text.Json.Nodes.JsonObject Data, ReadOnlyMemory<byte> Payload)> ReceiveWithPayload(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        return MatgateScreen.Decode(buffer.AsMemory(0, result.Count).ToArray());
    }

    private sealed class RecordingHost : Matgate.Display.IScreenHost
    {
        public List<string> Clips { get; } = [];

        public List<(int X, int Y, int Width, int Height)> Dirty { get; } = [];

        public List<string> Statuses { get; } = [];

        void Matgate.Display.IScreenHost.Dirty(int x, int y, int width, int height) => Dirty.Add((x, y, width, height));

        public void Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba)
        {
        }

        public void CursorHidden(bool hidden)
        {
        }

        public void CursorDefault()
        {
        }

        public void Event(string op, System.Text.Json.Nodes.JsonObject data, ReadOnlyMemory<byte> payload = default)
        {
            if (op == "screen.clip")
            {
                Clips.Add(System.Text.Encoding.UTF8.GetString(payload.Span));
            }
        }

        public void Status(string state, string? message = null) => Statuses.Add(message ?? state);

        public Task<Matgate.Display.ScreenCredentials?> AskAsync(string[] need, string userName, string domain, bool failed, CancellationToken cancellationToken) =>
            Task.FromResult<Matgate.Display.ScreenCredentials?>(new Matgate.Display.ScreenCredentials(userName, "geheim", domain));

        public void Audio(ReadOnlySpan<byte> pcm, int rate, int channels)
        {
        }

        public (int Width, int Height) WantedSize() => (0, 0);

        public void Log(string message)
        {
        }
    }
}
