using System.Net;
using System.Text;
using MatCloud.Contract;
using ThixGate.Core.Business;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Tests;

/// <summary>
/// The sync with ThixCloud's general sync (app "gate") against <see cref="FakeCloud"/>: two computers (two data folders and computer
/// names) that keep folders, connections and credentials the same – new, changed, deleted – while a password never leaves its computer.
/// </summary>
public sealed class SyncTests : IDisposable
{
    private const string Server = "https://cloud.example.test";

    private readonly TestData _data = new();
    private readonly FakeCloud _cloud = new();
    private readonly Device _anna;
    private readonly Device _laptop;

    public SyncTests()
    {
        CloudClient.HandlerForTests = () => _cloud;
        _anna = new Device(Path.Combine(_data.Folder, "anna-pc"), "ANNA-PC");
        _laptop = new Device(Path.Combine(_data.Folder, "laptop"), "LAPTOP");
    }

    public void Dispose()
    {
        AppInfo.Pin(null);
        _data.Dispose();
    }

    [Fact]
    public async Task Two_computers_share_the_library_but_never_a_password()
    {
        _anna.Use();
        SampleData.Create();
        await _anna.ConnectAsync();
        await _anna.SyncAsync();

        Assert.Equal(4, _cloud.Live(SyncCollections.Folders).Count);
        Assert.Equal(6, _cloud.Live(SyncCollections.Connections).Count);
        Assert.Equal(2, _cloud.Live(SyncCollections.Credentials).Count);
        Assert.DoesNotContain(_cloud.Items.Values, item => item.Data?.Contains("sample-password", StringComparison.Ordinal) == true);

        await _laptop.ConnectAsync();
        await _laptop.SyncAsync();
        Assert.Equal(6, LibraryService.Connections().Count);
        Assert.Equal("Kunden › Müller GmbH", LibraryService.FolderPath(SampleData.MuellerFolderId));
        var admin = CredentialService.Find(SampleData.AdminCredentialId)!;
        Assert.False(admin.HasPassword, "the password stays on Anna's computer");
        Assert.Null(CredentialService.ReadPassword(admin.Id));
        Assert.Equal(ConnectVia.Direct, LibraryService.FindConnection(SampleData.MuellerErpId)!.Via); // the Matgate account is Anna's

        // Changed and deleted on the laptop: arrives on Anna's computer, which keeps its password.
        LibraryService.SaveConnection(LibraryService.FindConnection(SampleData.TerminalServerId)! with { Name = "Terminalserver Büro" });
        LibraryService.DeleteConnection(SampleData.RaspberryId);
        CredentialService.Save(admin with { Name = "Admin Büro" }, newPassword: null);
        await _laptop.SyncAsync();

        _anna.Use();
        await _anna.SyncAsync();
        Assert.Equal("Terminalserver Büro", LibraryService.FindConnection(SampleData.TerminalServerId)!.Name);
        Assert.Null(LibraryService.FindConnection(SampleData.RaspberryId));
        Assert.Equal("Admin Büro", CredentialService.Find(SampleData.AdminCredentialId)!.Name);
        Assert.Equal("sample-password", CredentialService.ReadPassword(SampleData.AdminCredentialId));
        Assert.Equal(ConnectVia.Matgate, LibraryService.FindConnection(SampleData.MuellerErpId)!.Via);
        Assert.Empty(LibraryStore.Pending(100));
    }

    [Fact]
    public async Task A_part_switched_off_stays_on_the_computer_and_catches_up_when_switched_on()
    {
        _anna.Use();
        SampleData.Create();
        CloudSyncService.SetParts(library: true, credentials: false);
        await _anna.ConnectAsync();
        Assert.False(SettingsService.Get().Cloud.SyncCredentials); // the sign-in keeps the choice
        await _anna.SyncAsync();

        // Credentials stay on Anna's computer; folders and connections go.
        Assert.Equal(6, _cloud.Live(SyncCollections.Connections).Count);
        Assert.Empty(_cloud.Live(SyncCollections.Credentials));
        Assert.Empty(LibraryStore.Pending(100, CloudSyncService.Syncs));
        Assert.NotEmpty(LibraryStore.Pending(100)); // still waiting, for when they are switched on

        // On the laptop the library alone is synced; its own connection stays there.
        _laptop.Use();
        CloudSyncService.SetParts(library: false, credentials: true);
        var own = LibraryService.SaveConnection(new RemoteConnection { Name = "Nur am Laptop", Host = "laptop-only" });
        await _laptop.ConnectAsync();
        await _laptop.SyncAsync();
        Assert.Single(LibraryService.Connections());
        Assert.DoesNotContain(_cloud.Live(SyncCollections.Connections), item => item.Key == own.Id.ToString("D"));

        // Switched on: what changed meanwhile goes up, and everything of the cloud is read again.
        _laptop.Use();
        CloudSyncService.SetParts(library: true, credentials: true);
        Assert.Equal(0, SettingsService.Get().Cloud.LastRevision);
        await _laptop.SyncAsync();
        _laptop.Use();
        Assert.Equal(7, LibraryService.Connections().Count);
        Assert.Contains(_cloud.Live(SyncCollections.Connections), item => item.Key == own.Id.ToString("D"));

        _anna.Use();
        CloudSyncService.SetParts(library: true, credentials: true);
        await _anna.SyncAsync();
        Assert.Equal(2, _cloud.Live(SyncCollections.Credentials).Count);
        Assert.DoesNotContain(_cloud.Items.Values, item => item.Data?.Contains("sample-password", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task The_newer_change_wins()
    {
        var start = new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);
        _anna.Use();
        AppClock.Pin(start);
        var connection = LibraryService.SaveConnection(new RemoteConnection { Name = "Server", Host = "srv" });
        await _anna.ConnectAsync();
        await _anna.SyncAsync();
        await _laptop.ConnectAsync();
        await _laptop.SyncAsync();

        // the laptop changes it later than Anna
        _anna.Use();
        AppClock.Pin(start.AddMinutes(1));
        LibraryService.SaveConnection(LibraryService.FindConnection(connection.Id)! with { Host = "srv-anna" });
        _laptop.Use();
        AppClock.Pin(start.AddMinutes(2));
        LibraryService.SaveConnection(LibraryService.FindConnection(connection.Id)! with { Host = "srv-laptop" });
        await _laptop.SyncAsync();
        await _anna.SyncAsync();

        Assert.Equal("srv-laptop", LibraryService.FindConnection(connection.Id)!.Host);
    }

    [Fact]
    public async Task Signing_out_keeps_the_library_on_the_computer()
    {
        _anna.Use();
        SampleData.Create();
        await _anna.ConnectAsync();
        await CloudSyncService.DisconnectAsync();
        Assert.False(SettingsService.Get().Cloud.IsConnected);
        Assert.Equal(6, LibraryService.Connections().Count);
    }

    [Fact]
    public async Task The_sign_in_ThixLauncher_left_for_gate_connects_without_the_browser()
    {
        _anna.Use();
        var token = _cloud.LeaveHandoff("ANNA-PC");
        CloudHandoff.Write(CloudApps.Sync, new DeviceHandoff(Server, token, FakeCloud.UserName, DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.Null(await CloudSyncService.ConnectFromLauncherAsync()); // the one of ThixSync is not ThixGate's
        Assert.True(CloudHandoff.Exists(CloudApps.Sync));

        CloudHandoff.Write(CloudApps.Gate, new DeviceHandoff(Server, token, FakeCloud.UserName, DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.NotNull(await CloudSyncService.ConnectFromLauncherAsync());
        CloudSyncService.Stop();
        Assert.True(SettingsService.Get().Cloud.IsConnected);
        Assert.False(CloudHandoff.Exists(CloudApps.Gate));
    }

    [Fact]
    public async Task Matgate_is_recognised_by_its_health_answer()
    {
        MatgateService.HandlerForTests = () => new Answer("""{"status":"ok","app":"matgate","version":"0.55.1"}""");
        var account = MatgateService.Save(new MatgateAccount { Name = "Büro", ServerUrl = "matgate.example.test/" });
        Assert.Equal("https://matgate.example.test", account.ServerUrl);
        var check = await MatgateService.CheckAsync(account.ServerUrl, account.Id);
        Assert.True(check.IsMatgate);
        Assert.Equal("0.55.1", MatgateService.Find(account.Id)!.LastVersion);

        MatgateService.HandlerForTests = () => new Answer("<html>nope</html>");
        Assert.False((await MatgateService.CheckAsync(account.ServerUrl)).IsMatgate);

        var connection = LibraryService.SaveConnection(new RemoteConnection { Name = "ERP", Host = "erp", Via = ConnectVia.Matgate, MatgateAccountId = account.Id, MatgateConnectionId = Guid.NewGuid() });
        MatgateService.Delete(account.Id);
        Assert.Equal(ConnectVia.Direct, LibraryService.FindConnection(connection.Id)!.Via);

        AccessPolicy.UsePolicyReaderForTests(name => name == "DisableMatgate");
        Assert.Throws<BusinessException>(() => MatgateService.Save(new MatgateAccount { Name = "x", ServerUrl = "https://x" }));
    }

    private sealed class Answer(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class Device(string folder, string name)
    {
        public void Use()
        {
            CloudSyncService.Stop();
            AppPaths.UseDirectoryForTests(folder);
            Directory.CreateDirectory(folder);
            SettingsStore.Reset();
            LibraryStore.Reset();
            AppInfo.Pin("1.0.0", "release", 1, name);
        }

        public async Task ConnectAsync()
        {
            Use();
            await CloudSyncService.ConnectAsync(Server, FakeCloud.UserName, FakeCloud.Password, name);
            CloudSyncService.Stop(); // the tests sync step by step
        }

        public async Task SyncAsync()
        {
            Use();
            await CloudSyncService.SyncOnceAsync();
            LibraryStore.Reset();
        }
    }
}
