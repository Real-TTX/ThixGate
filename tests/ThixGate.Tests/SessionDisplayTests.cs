using Matgate.Display;
using ThixGate.Core.Business;
using ThixGate.Core.Models;

namespace ThixGate.Tests;

/// <summary>What a Remote Desktop takes from this computer: monitors and folders as drives - per connection, the settings' default, .rdp files.</summary>
public sealed class SessionDisplayTests : IDisposable
{
    private static readonly ScreenMonitor Left = new(-1920, 0, 1920, 1080, false);
    private static readonly ScreenMonitor Main = new(0, 0, 2560, 1440, true);
    private static readonly ScreenMonitor Right = new(2560, 0, 1920, 1080, false);

    private readonly TestData _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Monitors_are_numbered_left_to_right_and_chosen_by_the_connection_or_the_settings()
    {
        IReadOnlyList<ScreenMonitor> available = [Right, Main, Left];
        Assert.Equal([Left, Main, Right], SessionDisplay.Ordered(available));

        var rdp = new RemoteConnection { Protocol = ConnectionProtocol.Rdp, Host = "ts01" };
        Assert.Empty(SessionDisplay.MonitorsFor(rdp, available)); // the settings say "one"
        Assert.Equal([Left, Main, Right], SessionDisplay.MonitorsFor(rdp with { Monitors = "all" }, available));
        Assert.Equal([Left, Right], SessionDisplay.MonitorsFor(rdp with { Monitors = "1,3" }, available));
        Assert.Equal([Main], SessionDisplay.MonitorsFor(rdp with { Monitors = "2,7" }, available)); // monitor 7 is not here
        Assert.Empty(SessionDisplay.MonitorsFor(rdp with { Monitors = "one" }, available));

        SettingsService.Update(settings => settings with { Monitors = "all" });
        Assert.Equal(3, SessionDisplay.MonitorsFor(rdp, available).Count);
        Assert.Empty(SessionDisplay.MonitorsFor(rdp with { Monitors = "one" }, available));

        // Not for VNC, and not for a session on Matgate (its monitors are not this computer's).
        Assert.Empty(SessionDisplay.MonitorsFor(rdp with { Protocol = ConnectionProtocol.Vnc, Monitors = "all" }, available));
        Assert.Empty(SessionDisplay.MonitorsFor(rdp with { Monitors = "all", Via = ConnectVia.Matgate, SessionOnMatgate = true }, available));
    }

    [Fact]
    public void Folders_of_this_computer_become_drives_the_others_are_left_out()
    {
        var documents = Directory.CreateDirectory(Path.Combine(_data.Folder, "Dokumente")).FullName;
        var rdp = new RemoteConnection { Protocol = ConnectionProtocol.Rdp, Host = "ts01", Drives = documents + "\n" + Path.Combine(_data.Folder, "fehlt") };
        var drive = Assert.Single(SessionDisplay.DrivesFor(rdp));
        Assert.Equal(("Dokumente", documents), (drive.Name, drive.Path));
        Assert.NotEmpty(SessionDisplay.DrivesFor(rdp with { Drives = "*" }));
        Assert.Empty(SessionDisplay.DrivesFor(rdp with { Drives = "" }));
        Assert.Empty(SessionDisplay.DrivesFor(rdp with { Via = ConnectVia.Matgate, SessionOnMatgate = true }));

        // The engine gets them, with the monitors when there are two or more.
        var target = SessionTargets.ScreenTargetOf(rdp, SessionTargets.LoginOf(rdp), 1280, 800, monitors: [Main, Right]);
        Assert.Equal("Dokumente", Assert.Single(target.Drives).Name);
        Assert.Equal(2, target.Monitors.Count);
        Assert.Empty(SessionTargets.ScreenTargetOf(rdp, SessionTargets.LoginOf(rdp), 1280, 800, monitors: [Main]).Monitors);
    }

    [Fact]
    public void Monitors_and_drives_are_kept_for_remote_desktop_only_and_tidied()
    {
        var saved = LibraryService.SaveConnection(new RemoteConnection
        {
            Name = "Terminalserver", Protocol = ConnectionProtocol.Rdp, Host = "ts01", Monitors = " 3, 1,1,x ", Drives = "C:\\Daten\r\n\r\nD:\\\r\nc:\\daten",
        });
        Assert.Equal(("1,3", "C:\\Daten\nD:\\"), (saved.Monitors, saved.Drives));
        Assert.Equal("all", LibraryService.SaveConnection(saved with { Monitors = "ALL" }).Monitors);
        Assert.Equal("", LibraryService.SaveConnection(saved with { Monitors = "nonsense" }).Monitors);

        var vnc = LibraryService.SaveConnection(saved with { Id = Guid.NewGuid(), Name = "VNC", Protocol = ConnectionProtocol.Vnc });
        Assert.Equal(("", ""), (vnc.Monitors, vnc.Drives));
    }

    [Fact]
    public void An_rdp_file_brings_its_monitors_and_drives()
    {
        var path = Path.Combine(_data.Folder, "Buchhaltung.rdp");
        File.WriteAllLines(path, ["full address:s:buchhaltung.firma.local", "use multimon:i:1", "drivestoredirect:s:C:\\;E:\\Projekte;DynamicDrives"]);
        TransferService.Import(path);
        var imported = Assert.Single(LibraryService.Connections(), connection => connection.Name == "Buchhaltung");
        Assert.Equal(("all", "C:\\\nE:\\Projekte"), (imported.Monitors, imported.Drives));
    }
}
