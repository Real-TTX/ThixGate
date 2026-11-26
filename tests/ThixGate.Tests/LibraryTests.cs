using ThixGate.Core.Business;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Tests;

/// <summary>The business layer of the library: folders, connections, credentials, every check and policy, search and paging.</summary>
public sealed class LibraryTests : IDisposable
{
    private readonly TestData _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void A_connection_is_saved_normalised_and_read_back_after_a_restart()
    {
        var saved = LibraryService.SaveConnection(new RemoteConnection
        {
            Name = "  Terminalserver ", Protocol = ConnectionProtocol.Rdp, Host = " ts01.buero.local ", Tags = "büro, Windows,büro",
            KeyFile = "/should/go", Resolution = " AUTO ",
        });

        LibraryStore.Reset();
        var read = LibraryService.FindConnection(saved.Id)!;
        Assert.Equal(("Terminalserver", "ts01.buero.local", "büro, Windows", "", "auto"), (read.Name, read.Host, read.Tags, read.KeyFile, read.Resolution));
        Assert.Equal(3389, read.EffectivePort);
        Assert.True(read.UpdateDate > DateTimeOffset.MinValue);
    }

    [Theory]
    [InlineData("", "server", 0, nameof(RemoteConnection.Name))]
    [InlineData("Name", "", 0, nameof(RemoteConnection.Host))]
    [InlineData("Name", "mit leerzeichen", 0, nameof(RemoteConnection.Host))]
    [InlineData("Name", "server:3390", 0, nameof(RemoteConnection.Host))]
    [InlineData("Name", "server", 70000, nameof(RemoteConnection.Port))]
    public void Wrong_values_are_refused_at_their_field(string name, string host, int port, string field)
    {
        var exception = Assert.Throws<BusinessException>(() =>
            LibraryService.SaveConnection(new RemoteConnection { Name = name, Host = host, Port = port }));
        Assert.Equal(field, exception.Field);
    }

    [Fact]
    public void Ipv6_and_ipv4_addresses_are_accepted()
    {
        LibraryService.SaveConnection(new RemoteConnection { Name = "v6", Host = "fe80::1" });
        LibraryService.SaveConnection(new RemoteConnection { Name = "v4", Host = "192.168.1.10", Protocol = ConnectionProtocol.Vnc });
        Assert.Equal(2, LibraryService.Connections().Count);
    }

    [Fact]
    public void Fields_of_another_protocol_are_not_kept()
    {
        var ssh = LibraryService.SaveConnection(new RemoteConnection { Name = "ssh", Host = "h", Protocol = ConnectionProtocol.Ssh, Domain = "X", KeyFile = "k", Quality = SessionQuality.Fast });
        Assert.Equal(("", "k", SessionQuality.Auto), (ssh.Domain, ssh.KeyFile, ssh.Quality));
        var vnc = LibraryService.SaveConnection(new RemoteConnection { Name = "vnc", Host = "h", Protocol = ConnectionProtocol.Vnc, Domain = "X", KeyFile = "k" });
        Assert.Equal(("", ""), (vnc.Domain, vnc.KeyFile));
    }

    [Fact]
    public void Search_filter_sort_and_paging()
    {
        SampleData.Create();
        Assert.Equal(6, LibraryService.Query(new ConnectionQuery()).Total);
        Assert.Equal(2, LibraryService.Query(new ConnectionQuery { Protocol = ConnectionProtocol.Ssh }).Total);
        Assert.Equal(2, LibraryService.Query(new ConnectionQuery { FavoritesOnly = true }).Total);
        Assert.Equal(2, LibraryService.Query(new ConnectionQuery { FolderId = SampleData.CustomersFolderId }).Total); // with the sub folder
        Assert.Equal(2, LibraryService.Query(new ConnectionQuery { Search = "mueller-gmbh" }).Total); // the host
        Assert.Single(LibraryService.Query(new ConnectionQuery { Search = "DATEV" }).Items); // the note
        Assert.Equal("Terminalserver", LibraryService.Query(new ConnectionQuery { Sort = ConnectionSort.LastUsed }).Items[0].Name);
        var page = LibraryService.Query(new ConnectionQuery { PageSize = 4, Page = 2 });
        Assert.Equal((2, 2, 6), (page.Items.Count, page.PageCount, page.Total));
    }

    [Fact]
    public void Deleting_a_folder_keeps_its_connections_one_level_up()
    {
        SampleData.Create();
        LibraryService.DeleteFolder(SampleData.CustomersFolderId);
        Assert.Null(LibraryService.FindFolder(SampleData.MuellerFolderId)!.ParentId);
        Assert.Equal(SampleData.MuellerFolderId, LibraryService.FindConnection(SampleData.MuellerErpId)!.FolderId);
        Assert.Contains(LibraryStore.Read().Tombstones, tombstone => tombstone.Id == SampleData.CustomersFolderId);
    }

    [Fact]
    public void A_folder_cannot_lie_in_itself_or_twice_with_the_same_name()
    {
        SampleData.Create();
        var cycle = Assert.Throws<BusinessException>(() => LibraryService.SaveFolder(LibraryService.FindFolder(SampleData.CustomersFolderId)! with { ParentId = SampleData.MuellerFolderId }));
        Assert.Equal(nameof(RemoteFolder.ParentId), cycle.Field);
        var twice = Assert.Throws<BusinessException>(() => LibraryService.SaveFolder(new RemoteFolder { Name = "büro" }));
        Assert.Equal(nameof(RemoteFolder.Name), twice.Field);
    }

    [Fact]
    public void Credentials_keep_the_password_protected_and_out_of_the_library_file()
    {
        var credential = CredentialService.Save(new StoredCredential { Name = "Admin", UserName = "admin", Domain = "BUERO" }, "streng-geheim");
        Assert.True(credential.HasPassword);
        Assert.Equal("streng-geheim", CredentialService.ReadPassword(credential.Id));
        Assert.DoesNotContain("streng-geheim", File.ReadAllText(AppPaths.LibraryFile), StringComparison.Ordinal);

        CredentialService.Save(credential with { Name = "Admin Büro" }, newPassword: null);
        Assert.Equal("streng-geheim", CredentialService.ReadPassword(credential.Id));
        CredentialService.Save(credential, newPassword: "");
        Assert.Null(CredentialService.ReadPassword(credential.Id));
        Assert.False(CredentialService.Find(credential.Id)!.HasPassword);
    }

    [Fact]
    public void Deleting_credentials_leaves_their_user_in_the_connections()
    {
        SampleData.Create();
        CredentialService.Delete(SampleData.AdminCredentialId);
        var terminal = LibraryService.FindConnection(SampleData.TerminalServerId)!;
        Assert.Equal((null, "admin", "BUERO"), (terminal.CredentialId, terminal.UserName, terminal.Domain));
        Assert.Null(CredentialService.ReadPassword(SampleData.AdminCredentialId));
    }

    [Fact]
    public void The_engine_target_carries_login_quality_and_size()
    {
        SampleData.Create();
        var terminal = LibraryService.FindConnection(SampleData.TerminalServerId)!;
        var target = SessionTargets.ScreenTargetOf(terminal, SessionTargets.LoginOf(terminal), 1920, 1080);
        Assert.Equal(("rdp", "ts01.buero.local", 3389, "admin", "sample-password", "BUERO", 1920, 0x407u),
            (target.Protocol, target.Host, target.Port, target.UserName, target.Password, target.Domain, target.FitWidth, target.KeyboardLayout));
        var vnc = LibraryService.FindConnection(SampleData.RaspberryId)!;
        Assert.Equal(("vnc", 5900), (SessionTargets.ScreenTargetOf(vnc, SessionTargets.LoginOf(vnc), 0, 0).Protocol, vnc.EffectivePort));
        Assert.Throws<BusinessException>(() => SessionTargets.ScreenTargetOf(LibraryService.FindConnection(SampleData.MuellerErpId)!, SessionTargets.LoginOf(vnc), 0, 0));
    }

    [Fact]
    public void Ssh_arguments_never_carry_a_password()
    {
        SampleData.Create();
        Assert.Equal(["-p", "2222", "root@build01.lab.local"], SshLauncher.Arguments(LibraryService.FindConnection(SampleData.BuildAgentId)!));
        Assert.Equal("ssh deploy@web.mueller-gmbh.de", SshLauncher.CommandLine(LibraryService.FindConnection(SampleData.MuellerWebId)!));
    }

    [Fact]
    public void A_locked_configuration_refuses_every_change_but_allows_connecting()
    {
        SampleData.Create();
        AccessPolicy.UsePolicyReaderForTests(name => name == "LockConfiguration");
        Assert.Throws<BusinessException>(() => LibraryService.SaveConnection(new RemoteConnection { Name = "x", Host = "y" }));
        Assert.Throws<BusinessException>(() => LibraryService.DeleteFolder(SampleData.LabFolderId));
        Assert.Throws<BusinessException>(() => CredentialService.Delete(SampleData.RootCredentialId));
        LibraryService.MarkUsed(SampleData.RaspberryId);
        Assert.NotNull(LibraryService.FindConnection(SampleData.RaspberryId)!.LastUsed);
    }

    [Fact]
    public void Without_saved_passwords_by_policy_nothing_is_stored_or_read()
    {
        AccessPolicy.UsePolicyReaderForTests(name => name == "DisableSavedPasswords");
        var exception = Assert.Throws<BusinessException>(() => CredentialService.Save(new StoredCredential { Name = "a", UserName = "b" }, "pw"));
        Assert.Equal("Password", exception.Field);
        var credential = CredentialService.Save(new StoredCredential { Name = "a", UserName = "b" }, newPassword: null);
        Assert.Null(CredentialService.ReadPassword(credential.Id));
    }

    [Fact]
    public void Export_and_import_round_trip_without_passwords_and_rdp_files_become_connections()
    {
        SampleData.Create();
        var file = Path.Combine(_data.Folder, "lib" + TransferService.FileExtension);
        TransferService.Export(file);
        Assert.DoesNotContain("sample-password", File.ReadAllText(file), StringComparison.Ordinal);

        var other = Path.Combine(_data.Folder, "other");
        AppPaths.UseDirectoryForTests(other);
        SettingsStore.Reset();
        LibraryStore.Reset();
        var result = TransferService.Import(file);
        Assert.Equal((6, 4, 2, 0), (result.Connections, result.Folders, result.Credentials, result.Skipped));
        Assert.Equal(ConnectVia.Direct, LibraryService.FindConnection(SampleData.MuellerErpId)!.Via); // no Matgate account on this computer
        var again = TransferService.Import(file); // a second import skips everything
        Assert.Equal((0, 12), (again.Connections, again.Skipped));

        var rdp = Path.Combine(_data.Folder, "Kundenserver.rdp");
        File.WriteAllLines(rdp, ["full address:s:kunde.example.com:3390", "username:s:FIRMA\\anna", "screen mode id:i:1", "desktopwidth:i:1600", "desktopheight:i:900"]);
        TransferService.Import(rdp);
        var imported = LibraryService.Connections().Single(connection => connection.Name == "Kundenserver");
        Assert.Equal(("kunde.example.com", 3390, "anna", "FIRMA", "1600x900"), (imported.Host, imported.Port, imported.UserName, imported.Domain, imported.Resolution));
    }

    [Theory]
    [InlineData("host", "host", 0)]
    [InlineData("host:3390", "host", 3390)]
    [InlineData("[fe80::1]:3390", "fe80::1", 3390)]
    [InlineData("fe80::1", "fe80::1", 0)]
    public void Addresses_of_rdp_files_are_split(string address, string host, int port) =>
        Assert.Equal((host, port), TransferService.SplitAddress(address));

    [Fact]
    public void The_tree_shows_folders_open_and_closed_and_a_search_opens_the_way_to_what_matches()
    {
        SampleData.Create();
        LibraryService.SaveConnection(new RemoteConnection { Name = "Zuhause NAS", Protocol = ConnectionProtocol.Ssh, Host = "nas.home" });

        static string Line(LibraryTreeRow row) => $"{new string(' ', row.Depth * 2)}{(row.IsFolder ? (row.IsExpanded ? "- " : "+ ") : "")}{row.Name}{(row.IsFolder ? $" ({row.Count})" : "")}";

        // Closed: the folders at the top, then the connections without a folder, then the signed-in Matgate.
        Assert.Equal(["+ Büro (2)", "+ Kunden (2)", "+ Labor (2)", "Zuhause NAS", "+ Matgate Büro (4)"], LibraryService.Tree("", new HashSet<Guid>()).Select(Line));

        // The Matgate open: its folders (from its folders and the paths of its connections) and connections, straight from its library.
        var rz = LibraryTreeRow.MatgateFolderId(SampleData.MatgateAccountId, "Rechenzentrum");
        var matgate = LibraryService.Tree("", new HashSet<Guid> { SampleData.MatgateAccountId, rz }).SkipWhile(row => row.Matgate is null).ToList();
        Assert.Equal(["- Matgate Büro (4)", "  + Kunden (1)", "  - Rechenzentrum (3)", "    Git", "    Leitstand", "    SQL-Server"], matgate.Select(Line));
        Assert.All(matgate, row => Assert.True(row.IsFromMatgate));
        Assert.Equal(SampleData.MatgateSqlId, matgate[^1].MatgateItem!.Id);
        Assert.Equal(rz, matgate[2].Id);

        // A search finds them too; signed out (or Matgate off by policy) the Matgate is not in the tree.
        Assert.Equal(["- Matgate Büro (1)", "  - Rechenzentrum (1)", "    Leitstand"], LibraryService.Tree("leitstand", new HashSet<Guid>()).Select(Line));
        AccessPolicy.UsePolicyReaderForTests(name => name == "DisableMatgate");
        Assert.DoesNotContain(LibraryService.Tree("", new HashSet<Guid>()), row => row.IsFromMatgate);
        AccessPolicy.UsePolicyReaderForTests(_ => false);
        SettingsService.UpdateMatgateAccounts(list => [.. list.Select(account => account with { DeviceId = null })]);
        Assert.DoesNotContain(LibraryService.Tree("", new HashSet<Guid>()), row => row.IsFromMatgate);

        // Open folders show their sub-folders first, then their connections, each by name; a closed one inside an open one stays closed.
        var open = new HashSet<Guid> { SampleData.OfficeFolderId, SampleData.CustomersFolderId };
        Assert.Equal(
            ["- Büro (2)", "  Dateiserver", "  Terminalserver", "- Kunden (2)", "  + Müller GmbH (2)", "+ Labor (2)", "Zuhause NAS"],
            LibraryService.Tree("", open).Select(Line));

        // A search keeps only what matches and opens the folders above it, whatever is open.
        Assert.Equal(["- Kunden (1)", "  - Müller GmbH (1)", "    ERP Müller"], LibraryService.Tree("erp", new HashSet<Guid>()).Select(Line));
        Assert.Empty(LibraryService.Tree("gibt es nicht", open));

        Assert.Equal([SampleData.CustomersFolderId, SampleData.MuellerFolderId], LibraryService.FolderChain(SampleData.MuellerFolderId));
        Assert.Empty(LibraryService.FolderChain(null));
    }

    [Fact]
    public void The_width_of_the_menu_and_its_open_folders_are_remembered_within_limits()
    {
        SettingsService.Remember(settings => settings with { TreeWidth = 5000, ExpandedFolders = [SampleData.LabFolderId, SampleData.LabFolderId] });
        SettingsStore.Reset();
        var read = SettingsService.Get();
        Assert.Equal(SettingsService.TreeMaxWidth, read.TreeWidth);
        Assert.Equal([SampleData.LabFolderId], read.ExpandedFolders);
    }
}
