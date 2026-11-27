using System.Net;
using System.Net.Sockets;
using System.Text;
using Matgate.Files;
using Matgate.Models;
using Matgate.Services.Database;
using ThixGate.Core.Business;
using ThixGate.Core.Models;

namespace ThixGate.Tests;

/// <summary>
/// Files (SFTP, FTP, SMB, WebDAV), websites and databases (SQL Server, MySQL, PostgreSQL): the model, the file operations of Mat.Remote,
/// the proxy for websites über Matgate - and, opt-in, everything against real servers (THIXGATE_E2E_FILES=1, THIXGATE_E2E_DB=1; the
/// containers and their test-only logins are in CLAUDE.md).
/// </summary>
public sealed class FilesDatabasesWebTests
{
    // ================================================================= the model

    [Fact]
    public void Select_top_rows_is_written_as_management_studio_writes_it()
    {
        Assert.Equal("SELECT TOP (100) [Id]\n      ,[Name]\n  FROM [dbo].[Kunden]", DatabaseSession.SelectTop(DatabaseEngine.MsSql, "dbo", "Kunden", ["Id", "Name"]));
        Assert.Equal("SELECT \"id\"\n  FROM \"public\".\"kunden\"\n LIMIT 100", DatabaseSession.SelectTop(DatabaseEngine.Postgres, "public", "kunden", ["id"]));
        var mysql = DatabaseSession.SelectTop(DatabaseEngine.MySql, "shop", "kunden", []);
        Assert.StartsWith("SELECT *\n  FROM ", mysql);
        Assert.EndsWith("`kunden`\n LIMIT 100", mysql);
        Assert.Equal("SELECT [Id]\n  FROM [dbo].[Kunden]", DatabaseSession.SelectTop(DatabaseEngine.MsSql, "dbo", "Kunden", ["Id"], count: null));
    }

    [Fact]
    public void A_website_keeps_its_address_and_takes_host_and_port_from_it()
    {
        using var data = new TestData();
        var site = LibraryService.SaveConnection(new RemoteConnection { Name = "Wiki", Protocol = ConnectionProtocol.Web, Url = "wiki.lab:8443/start", KeyFile = "x", Domain = "D", RootPath = "/x" });
        Assert.Equal(("https://wiki.lab:8443/start", "wiki.lab", 8443), (site.Url, site.Host, site.Port));
        Assert.Equal(("", "", ""), (site.KeyFile, site.Domain, site.RootPath)); // what does not apply is not kept
        var error = Assert.Throws<BusinessException>(() => LibraryService.SaveConnection(new RemoteConnection { Name = "Leer", Protocol = ConnectionProtocol.Web }));
        Assert.Equal(nameof(RemoteConnection.Url), error.Field);
        Assert.Equal(ConnectionKind.Web, ConnectionProtocol.Web.KindOf());
    }

    [Fact]
    public void Files_and_databases_keep_their_own_fields_only()
    {
        using var data = new TestData();
        var share = LibraryService.SaveConnection(new RemoteConnection { Name = "NAS", Protocol = ConnectionProtocol.Smb, Host = "nas", Domain = "LAB", RootPath = " daten/projekte ", Database = "x", KeyFile = "k" });
        Assert.Equal(("LAB", "daten/projekte", "", ""), (share.Domain, share.RootPath, share.Database, share.KeyFile));
        Assert.Equal(445, share.EffectivePort);
        var sftp = LibraryService.SaveConnection(new RemoteConnection { Name = "Dateien", Protocol = ConnectionProtocol.Sftp, Host = "files", KeyFile = "C:/keys/id", Domain = "X" });
        Assert.Equal(("C:/keys/id", "", 22), (sftp.KeyFile, sftp.Domain, sftp.EffectivePort));
        var database = LibraryService.SaveConnection(new RemoteConnection { Name = "ERP", Protocol = ConnectionProtocol.MsSql, Host = "sql", Database = "erp", Resolution = "1920x1080", Via = ConnectVia.Direct });
        Assert.Equal(("erp", "auto", 1433, false), (database.Database, database.Resolution, database.EffectivePort, database.SessionOnMatgate));
    }

    [Fact]
    public void Matgate_names_the_new_kinds_and_a_matgate_item_opens_with_its_fields()
    {
        foreach (var protocol in Enum.GetValues<ConnectionProtocol>())
        {
            Assert.Equal(protocol, ConnectionKinds.FromMatgateName(protocol.MatgateName()));
        }

        Assert.Equal("website", ConnectionProtocol.Web.MatgateName());
        var item = new MatgateConnectionItem { Id = Guid.NewGuid(), Name = "Ablage", Protocol = "smb", Host = "nas", Port = 445, Domain = "LAB", RootPath = "daten", Level = "Connect", HasStoredLogin = true };
        var connection = MatgateService.AsConnection(Guid.NewGuid(), item);
        Assert.Equal((ConnectionProtocol.Smb, "LAB", "daten", 0, false), (connection.Protocol, connection.Domain, connection.RootPath, connection.Port, connection.SessionOnMatgate));
        var draft = MatgateService.CheckDraft(new MatgateConnectionDraft { Name = "Wiki", Protocol = ConnectionProtocol.Web, Url = "wiki.lab" });
        Assert.Equal(("https://wiki.lab/", "wiki.lab"), (draft.Url, draft.Host));
        Assert.Throws<BusinessException>(() => MatgateService.AsConnection(Guid.NewGuid(), item with { Level = "ReadOnly" }));
    }

    // ================================================================= Mat.Remote's file operations

    [Fact]
    public async Task Copy_move_rename_zip_and_extract_work_on_any_store()
    {
        var store = new MemoryStore();
        await store.CreateDirectoryAsync("/", "a", default);
        await store.UploadAsync("/a", new MemoryStream("eins"u8.ToArray()), "x.txt", default);
        await store.CreateDirectoryAsync("/a", "inner", default);
        await store.UploadAsync("/a/inner", new MemoryStream("zwei"u8.ToArray()), "y.txt", default);
        await store.CreateDirectoryAsync("/", "b", default);

        var copied = await FileOperations.CopyAsync(store, ["/a"], store, "/b", sameStore: true);
        Assert.Equal((2, 0), (copied.Copied, copied.Failed.Count));
        Assert.Equal("zwei", store.Text("/b/a/inner/y.txt"));
        Assert.Single((await FileOperations.CopyAsync(store, ["/a"], store, "/a/inner", sameStore: true)).Failed); // not into itself

        await FileOperations.CopyAsync(store, ["/a/x.txt"], store, "/a", sameStore: true);
        Assert.Equal("eins", store.Text("/a/x (2).txt")); // a name that is taken gets " (2)"

        await FileOperations.RenameAsync(store, "/a/x (2).txt", "z.txt");
        Assert.True(store.Exists("/a/z.txt"));
        Assert.False(store.Exists("/a/x (2).txt"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => FileOperations.RenameAsync(store, "/a/z.txt", "x.txt"));

        await FileOperations.MoveAsync(store, ["/a/z.txt"], store, "/b", sameStore: true);
        Assert.True(store.Exists("/b/z.txt"));
        Assert.False(store.Exists("/a/z.txt"));

        var zip = await FileOperations.ZipIntoAsync(store, ["/a"], "/", "paket");
        Assert.Equal("paket.zip", zip);
        await store.CreateDirectoryAsync("/", "c", default);
        var count = await FileOperations.ExtractAsync(store, "/paket.zip", "/c");
        Assert.Equal(2, count);
        Assert.Equal("zwei", store.Text("/c/a/inner/y.txt"));
        Assert.True(FileOperations.IsArchive("x.tar.gz"));
    }

    // ================================================================= websites über Matgate

    [Fact]
    public async Task The_proxy_takes_the_site_through_the_relay_and_everything_else_directly()
    {
        // "relay": a server that answers with "RELAY"; "direct": one that answers "DIRECT"
        using var relay = Echo("RELAY");
        using var direct = Echo("DIRECT");
        using var proxy = WebRelayProxy.Start("site.lab", 443, (IPEndPoint)relay.LocalEndpoint);

        async Task<string> Through(string target)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(proxy.EndPoint);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n\r\n"));
            var buffer = new byte[256];
            var text = "";
            while (!text.Contains("RELAY", StringComparison.Ordinal) && !text.Contains("DIRECT", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                text += Encoding.ASCII.GetString(buffer, 0, read);
            }

            return text;
        }

        Assert.Contains("200 Connection established", await Through("site.lab:443"));
        Assert.Contains("RELAY", await Through("site.lab:443"));
        Assert.Contains("DIRECT", await Through($"127.0.0.1:{((IPEndPoint)direct.LocalEndpoint).Port}"));
    }

    private static TcpListener Echo(string answer)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync();
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(answer));
                    client.Dispose();
                }
                catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
                {
                    return;
                }
            }
        });
        return listener;
    }

    // ================================================================= real servers (opt-in)

    public static TheoryData<ConnectionProtocol, int, string> FileServers => new()
    {
        { ConnectionProtocol.Sftp, 12222, "" },
        { ConnectionProtocol.Ftp, 12121, "/ftp/demo" },
        { ConnectionProtocol.Smb, 1445, "daten" },
        { ConnectionProtocol.WebDav, 18080, "" },
    };

    [Theory]
    [MemberData(nameof(FileServers))]
    public async Task Real_file_servers_list_upload_rename_copy_zip_extract_download_and_delete(ConnectionProtocol protocol, int port, string root)
    {
        if (Environment.GetEnvironmentVariable("THIXGATE_E2E_FILES") is null)
        {
            return;
        }

        using var data = new TestData();
        var credential = CredentialService.Save(new StoredCredential { Name = "Test", UserName = "demo" }, "demo-only-pw");
        var connection = LibraryService.SaveConnection(new RemoteConnection { Name = $"{protocol}", Protocol = protocol, Host = "127.0.0.1", Port = port, RootPath = root, CredentialId = credential.Id });
        var session = await FileSession.OpenAsync(connection, (_, _, _) => Task.FromResult<LoginAnswer?>(null), CancellationToken.None);
        // where the test user may write: the home of the SFTP container; FTP starts in its home through the start folder (no chroot)
        var top = protocol == ConnectionProtocol.Sftp ? "/config" : "/";
        var folder = "thixgate-" + Guid.NewGuid().ToString("N")[..8];
        await session.CreateFolderAsync(top, folder, default);
        var into = RemoteFiles.CombineVirtualPath(top, folder);
        try
        {
            var local = Path.Combine(Path.GetTempPath(), "thixgate-test-" + Guid.NewGuid().ToString("N") + ".txt");
            await File.WriteAllTextAsync(local, "Grüße aus ThixGate");
            await session.UploadAsync(into, local, null, default);
            var name = Path.GetFileName(local);
            Assert.Contains((await session.ListAsync(into, default)).Entries, entry => entry.Name == name && entry.Size == new FileInfo(local).Length);

            await session.RenameAsync(RemoteFiles.CombineVirtualPath(into, name), "umbenannt.txt", default);
            await session.CreateFolderAsync(into, "kopie", default);
            var result = await session.CopyToAsync([RemoteFiles.CombineVirtualPath(into, "umbenannt.txt")], session, RemoteFiles.CombineVirtualPath(into, "kopie"), move: false, default);
            Assert.Empty(result.Failed);
            var zip = await session.ZipIntoAsync([RemoteFiles.CombineVirtualPath(into, "kopie")], into, "paket.zip", default);
            await session.CreateFolderAsync(into, "entpackt", default);
            Assert.Equal(1, await session.ExtractAsync(RemoteFiles.CombineVirtualPath(into, zip), RemoteFiles.CombineVirtualPath(into, "entpackt"), default));

            var back = Path.Combine(Path.GetTempPath(), "thixgate-back-" + Guid.NewGuid().ToString("N") + ".txt");
            await session.DownloadAsync(RemoteFiles.CombineVirtualPath(into, "entpackt/kopie/umbenannt.txt"), back, null, default);
            Assert.Equal("Grüße aus ThixGate", await File.ReadAllTextAsync(back));
            File.Delete(local);
            File.Delete(back);
        }
        finally
        {
            await session.DeleteAsync([into], default);
        }

        Assert.DoesNotContain((await session.ListAsync(top, default)).Entries, entry => entry.Name == folder);
    }

    public static TheoryData<ConnectionProtocol, int, string, string, string> DatabaseServers => new()
    {
        { ConnectionProtocol.Postgres, 15432, "demo", "demo-only-pw", "shop" },
        { ConnectionProtocol.MySql, 13306, "demo", "demo-only-pw", "shop" },
        { ConnectionProtocol.MsSql, 11433, "sa", "Demo-only-pw1!", "" },
    };

    [Theory]
    [MemberData(nameof(DatabaseServers))]
    public async Task Real_databases_query_page_edit_design_plan_and_export(ConnectionProtocol protocol, int port, string user, string password, string database)
    {
        if (Environment.GetEnvironmentVariable("THIXGATE_E2E_DB") is null)
        {
            return;
        }

        using var data = new TestData();
        var credential = CredentialService.Save(new StoredCredential { Name = "Test", UserName = user }, password);
        var connection = LibraryService.SaveConnection(new RemoteConnection { Name = $"{protocol}", Protocol = protocol, Host = "127.0.0.1", Port = port, Database = database, IgnoreCertificate = true, CredentialId = credential.Id });
        var session = await DatabaseSession.OpenAsync(connection, (_, _, _) => Task.FromResult<LoginAnswer?>(null), CancellationToken.None);
        var db = database.Length > 0 ? database : "master";
        var schema = protocol switch { ConnectionProtocol.Postgres => "public", ConnectionProtocol.MsSql => "dbo", _ => database };
        var table = "kunden_" + Guid.NewGuid().ToString("N")[..6];
        var dialect = DatabaseGateway.DialectOf(session.Engine);
        var name = dialect.Table(schema, table);
        try
        {
            // the designer makes the table, a batch fills it
            var design = new TableDesign(table,
                [new DesignColumn("", "id", "int", false, null, true, false), new DesignColumn("", "name", "varchar(100)", true, null, false, false)], [], []);
            await session.ApplyAsync(db, session.DesignStatements(schema, null, null, design), default);
            var batch = await session.BatchAsync(db, $"INSERT INTO {name} (id, name) VALUES (1, 'Anna'), (2, 'Ben'); SELECT * FROM {name} ORDER BY id; SELECT COUNT(*) AS n FROM {name}", default);
            Assert.Equal(2, batch.Results.Count(result => result.Columns.Count > 0));
            Assert.Contains((await session.ObjectsAsync(db, default)), item => item.Name == table);

            // rows page by page, then edited by primary key
            var page = await session.RowsAsync(db, schema, table, 0, 100, "id", true, default);
            Assert.Equal("2", page.Rows[0][0]);
            var info = await session.TableAsync(db, schema, table, default);
            var statements = session.RowStatements(schema, table, info,
            [
                new RowChange("update", new() { ["id"] = "1" }, new() { ["name"] = "Anna Schmidt" }),
                new RowChange("insert", null, new() { ["id"] = "3", ["name"] = null }),
                new RowChange("delete", new() { ["id"] = "2" }, null),
            ]);
            Assert.Equal(3, await session.ApplyRowsAsync(db, info, statements, default));
            var after = await session.RowsAsync(db, schema, table, 0, 100, "id", false, default);
            Assert.Equal(["1", "3"], after.Rows.Select(row => row[0] ?? "").ToArray());
            Assert.Equal("Anna Schmidt", after.Rows[0][1]);
            Assert.Null(after.Rows[1][1]);

            // a column more through the designer
            var changed = design with
            {
                Columns = [.. design.Columns.Select(column => column with { OriginalName = column.Name }), new DesignColumn("", "ort", "varchar(50)", true, null, false, false)],
            };
            await session.ApplyAsync(db, session.DesignStatements(schema, table, info, changed), default);
            Assert.Contains((await session.TableAsync(db, schema, table, default)).Columns, column => column.Name == "ort");

            var plan = await session.PlanAsync(db, $"SELECT * FROM {name} WHERE id = 1", analyze: false, default);
            Assert.False(string.IsNullOrWhiteSpace(plan.Raw));
            var csv = Path.Combine(Path.GetTempPath(), "thixgate-export-" + Guid.NewGuid().ToString("N") + ".csv");
            Assert.Equal(2, await session.ExportAsync(db, $"SELECT * FROM {name}", csv, json: false, ';', default));
            Assert.Contains("Anna Schmidt", await File.ReadAllTextAsync(csv));
            File.Delete(csv);
        }
        finally
        {
            await session.BatchAsync(db, $"DROP TABLE {name}", default);
        }
    }

    /// <summary>A file store in memory (paths like "/a/b.txt").</summary>
    private sealed class MemoryStore : IFileStore
    {
        private readonly Dictionary<string, byte[]?> _items = new(StringComparer.Ordinal) { ["/"] = null };

        public string Text(string path) => Encoding.UTF8.GetString(_items[path]!);

        public bool Exists(string path) => _items.ContainsKey(path);

        public Task<FileGatewayListResult> ListAsync(string? path, CancellationToken cancellationToken)
        {
            var folder = RemoteFiles.NormalizeVirtualPath(path);
            var entries = _items.Keys.Where(key => key != "/" && RemoteFiles.ParentVirtualPath(key) == folder)
                .Select(key => new FileGatewayEntry(RemoteFiles.FileNameFromPath(key), key, _items[key] is null, _items[key]?.Length, DateTimeOffset.UtcNow))
                .ToList();
            return Task.FromResult(new FileGatewayListResult(folder, RemoteFiles.ParentVirtualPath(folder), entries));
        }

        public Task<FileGatewayDownload> DownloadAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new FileGatewayDownload(new MemoryStream(_items[RemoteFiles.NormalizeVirtualPath(path)]!), RemoteFiles.FileNameFromPath(path), "application/octet-stream"));

        public async Task UploadAsync(string folder, Stream content, string fileName, CancellationToken cancellationToken)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            _items[RemoteFiles.CombineVirtualPath(RemoteFiles.NormalizeVirtualPath(folder), fileName)] = copy.ToArray();
        }

        public Task CreateDirectoryAsync(string folder, string name, CancellationToken cancellationToken)
        {
            _items[RemoteFiles.CombineVirtualPath(RemoteFiles.NormalizeVirtualPath(folder), name)] = null;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            var target = RemoteFiles.NormalizeVirtualPath(path);
            foreach (var key in _items.Keys.Where(key => key == target || key.StartsWith(target + "/", StringComparison.Ordinal)).ToList())
            {
                _items.Remove(key);
            }

            return Task.CompletedTask;
        }
    }
}
