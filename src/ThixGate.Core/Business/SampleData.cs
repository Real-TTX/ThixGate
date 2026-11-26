using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>
/// The sample library of the snapshots and UI tests: folders (Büro, Kunden › Müller GmbH, Labor), saved credentials, RDP, VNC and SSH
/// connections, a file share, a website and a database, with favourites and "last used", a Matgate account. Only ever written into a data folder of its own (snapshot, tests).
/// </summary>
public static class SampleData
{
    public static readonly Guid OfficeFolderId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000001");
    public static readonly Guid CustomersFolderId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000002");
    public static readonly Guid MuellerFolderId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000003");
    public static readonly Guid LabFolderId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000004");
    public static readonly Guid AdminCredentialId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000011");
    public static readonly Guid RootCredentialId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000012");
    public static readonly Guid TerminalServerId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000021");
    public static readonly Guid FileServerId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000022");
    public static readonly Guid MuellerErpId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000023");
    public static readonly Guid MuellerWebId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000024");
    public static readonly Guid RaspberryId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000025");
    public static readonly Guid BuildAgentId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000026");
    public static readonly Guid OfficeShareId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000027");
    public static readonly Guid IntranetId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000028");
    public static readonly Guid MuellerDatabaseId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000029");
    public static readonly Guid MatgateAccountId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000031");
    public static readonly Guid MatgateDeviceId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000032");
    public static readonly Guid MatgateErpId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000041");
    public static readonly Guid MatgateSqlId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000042");
    public static readonly Guid MatgateGitId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000043");
    public static readonly Guid MatgateWatchId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000044");

    /// <summary>Writes the sample library (the data folder must be a fresh one).</summary>
    public static void Create()
    {
        var now = AppClock.UtcNow;
        MatgateService.Save(new MatgateAccount { Id = MatgateAccountId, Name = "Matgate Büro", ServerUrl = "https://matgate.example.com", UserName = "anna" });
        SettingsService.UpdateMatgateAccounts(list => [.. list.Select(account => account with
        {
            LastChecked = now.AddHours(-2), LastVersion = "0.56.0", DeviceId = MatgateDeviceId, DisplayName = "Anna Schmidt", LibraryFetched = now.AddMinutes(-4),
        })]);
        MatgateStore.Write(MatgateAccountId, new MatgateLibrary
        {
            Revision = "sample",
            User = new MatgateLibraryUser { UserName = "anna", DisplayName = "Anna Schmidt", CanCreatePrivate = true },
            Folders = [new() { Path = "Kunden", Name = "Kunden" }, new() { Path = "Kunden/Müller GmbH", Name = "Müller GmbH", Parent = "Kunden" }, new() { Path = "Rechenzentrum", Name = "Rechenzentrum" }],
            Connections =
            [
                new() { Id = MatgateErpId, Name = "ERP Müller", Protocol = "rdp", Host = "erp.mueller-gmbh.de", Port = 3390, UserName = "support", Domain = "MUELLER", Folder = "Kunden/Müller GmbH", Level = "Connect", KeyboardLayout = "de-de-qwertz", HasStoredLogin = true },
                new() { Id = MatgateSqlId, Name = "SQL-Server", Protocol = "rdp", Host = "sql01.rz.local", Port = 3389, Folder = "Rechenzentrum", Level = "Manage", KeyboardLayout = "de-de-qwertz", IsPrivate = true, CanDelete = true },
                new() { Id = MatgateWatchId, Name = "Leitstand", Protocol = "vnc", Host = "leitstand.rz.local", Port = 5900, Folder = "Rechenzentrum", Level = "ReadOnly", HasStoredLogin = true },
                new() { Id = MatgateGitId, Name = "Git", Protocol = "ssh", Host = "git.rz.local", Port = 22, UserName = "git", Folder = "Rechenzentrum", Level = "Connect" },
            ],
        });

        LibraryService.SaveFolder(new RemoteFolder { Id = OfficeFolderId, Name = "Büro" });
        LibraryService.SaveFolder(new RemoteFolder { Id = CustomersFolderId, Name = "Kunden" });
        LibraryService.SaveFolder(new RemoteFolder { Id = MuellerFolderId, Name = "Müller GmbH", ParentId = CustomersFolderId });
        LibraryService.SaveFolder(new RemoteFolder { Id = LabFolderId, Name = "Labor" });

        CredentialService.Save(new StoredCredential { Id = AdminCredentialId, Name = "Domänen-Admin Büro", UserName = "admin", Domain = "BUERO" }, "sample-password");
        CredentialService.Save(new StoredCredential { Id = RootCredentialId, Name = "root Labor", UserName = "root" }, newPassword: null);

        LibraryService.SaveConnection(new RemoteConnection
        {
            Id = TerminalServerId, Name = "Terminalserver", Protocol = ConnectionProtocol.Rdp, Host = "ts01.buero.local", FolderId = OfficeFolderId,
            CredentialId = AdminCredentialId, IsFavorite = true, Notes = "Für alle im Büro – Office und DATEV.", Tags = "Büro, Windows",
        });
        LibraryService.SaveConnection(new RemoteConnection
        {
            Id = FileServerId, Name = "Dateiserver", Protocol = ConnectionProtocol.Rdp, Host = "fs01.buero.local", FolderId = OfficeFolderId,
            CredentialId = AdminCredentialId, Quality = SessionQuality.Medium,
        });
        LibraryService.SaveConnection(new RemoteConnection
        {
            Id = MuellerErpId, Name = "ERP Müller", Protocol = ConnectionProtocol.Rdp, Host = "erp.mueller-gmbh.de", Port = 3390, FolderId = MuellerFolderId,
            UserName = "support", Domain = "MUELLER", Via = ConnectVia.Matgate, MatgateAccountId = MatgateAccountId, MatgateConnectionId = MatgateErpId, SessionOnMatgate = true,
            Tags = "Kunde",
        });
        LibraryService.SaveConnection(new RemoteConnection
        {
            Id = MuellerWebId, Name = "Webserver Müller", Protocol = ConnectionProtocol.Ssh, Host = "web.mueller-gmbh.de", FolderId = MuellerFolderId,
            UserName = "deploy", IsFavorite = true, Tags = "Kunde, Linux",
        });
        LibraryService.SaveConnection(new RemoteConnection
        {
            Id = RaspberryId, Name = "Raspberry Pi Werkstatt", Protocol = ConnectionProtocol.Vnc, Host = "192.168.20.41", FolderId = LabFolderId,
            Notes = "Anzeige der Maschinenlaufzeiten.",
        });
        LibraryService.SaveConnection(new RemoteConnection
        {
            Id = BuildAgentId, Name = "Build-Agent", Protocol = ConnectionProtocol.Ssh, Host = "build01.lab.local", Port = 2222, FolderId = LabFolderId,
            CredentialId = RootCredentialId,
        });

        // "last used" as if the week had been a normal one
        LibraryStore.Update(library =>
        {
            for (var index = 0; index < library.Connections.Count; index++)
            {
                var connection = library.Connections[index];
                DateTimeOffset? used = connection.Id == TerminalServerId ? now.AddMinutes(-20)
                    : connection.Id == MuellerWebId ? now.AddHours(-3)
                    : connection.Id == RaspberryId ? now.AddDays(-2)
                    : connection.Id == FileServerId ? now.AddDays(-5)
                    : null;
                library.Connections[index] = connection with { LastUsed = used };
            }
        });
        LibraryService.Raise(syncToo: false);
    }

    /// <summary>A file share, a website and a database besides (the snapshots; the tests count the connections of <see cref="Create"/>).</summary>
    public static void CreateFilesWebDatabase()
    {
        LibraryService.SaveConnection(new RemoteConnection
        {
            Id = OfficeShareId, Name = "Ablage Büro", Protocol = ConnectionProtocol.Smb, Host = "fs01.buero.local", RootPath = "daten", FolderId = OfficeFolderId,
            CredentialId = AdminCredentialId, Tags = "Büro, Dateien",
        });
        LibraryService.SaveConnection(new RemoteConnection
        {
            Id = IntranetId, Name = "Intranet", Protocol = ConnectionProtocol.Web, Url = "https://intranet.buero.local", FolderId = OfficeFolderId,
        });
        LibraryService.SaveConnection(new RemoteConnection
        {
            Id = MuellerDatabaseId, Name = "Datenbank Müller", Protocol = ConnectionProtocol.MsSql, Host = "sql.mueller-gmbh.de", Database = "erp", FolderId = MuellerFolderId,
            UserName = "report", IgnoreCertificate = true, Tags = "Kunde",
        });
    }
}
