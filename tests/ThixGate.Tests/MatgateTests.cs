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
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

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
}
