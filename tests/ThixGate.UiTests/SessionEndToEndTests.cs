using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ThixGate.Core.Business;
using ThixGate.Core.Models;
using ThixGate.Sessions;

namespace ThixGate.UiTests;

/// <summary>
/// Real sessions through Matgate's engine into ThixGate's session window, against the test desktop of Matgate
/// (Matgate/tests/desktop: xrdp on 3389, FreeRDP's shadow server with NLA on 3390, TigerVNC on 5901). Opt-in: set
/// THIXGATE_E2E_DESKTOP to the host, e.g. "127.0.0.1", and THIXGATE_E2E_PORTS to "13389,13390,15901" when the ports are mapped elsewhere.
/// Test credentials of that container only.
/// </summary>
public sealed class SessionEndToEndTests
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("THIXGATE_E2E_DESKTOP");
    private static readonly int[] Ports = (Environment.GetEnvironmentVariable("THIXGATE_E2E_PORTS") ?? "3389,3390,5901").Split(',').Select(int.Parse).ToArray();

    [AvaloniaFact]
    public void Rdp_session_draws_the_remote_desktop() => Run(ConnectionProtocol.Rdp, Ports[0], "demo", "demo-only-pw");

    [AvaloniaFact]
    public void Rdp_session_with_network_level_authentication_draws_the_remote_desktop() => Run(ConnectionProtocol.Rdp, Ports[1], "demo", "demo-only-pw");

    [AvaloniaFact]
    public void Vnc_session_draws_the_remote_desktop() => Run(ConnectionProtocol.Vnc, Ports[2], "", "demo-pw1");

    private static void Run(ConnectionProtocol protocol, int port, string user, string password)
    {
        if (Host is null)
        {
            return; // opt-in
        }

        using var data = new TestData();
        SettingsService.Update(settings => settings with { ConfirmCloseSession = false });
        var credential = CredentialService.Save(new StoredCredential { Name = "Test", UserName = user.Length > 0 ? user : "vnc" }, password);
        var connection = LibraryService.SaveConnection(new RemoteConnection
        {
            Name = $"Testdesktop {protocol} {port}",
            Protocol = protocol,
            Host = Host,
            Port = port,
            CredentialId = credential.Id,
            Resolution = "1280x800",
        });

        foreach (var leftover in SessionWindow.OpenSessions.ToList())
        {
            leftover.Close();
        }

        var window = SessionWindow.Show(connection);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < until && !(window.IsConnected && window.HasPicture))
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Thread.Sleep(50);
            }

            // the server may send the first picture in passes: let it finish before the screenshot
            for (var settle = DateTime.UtcNow.AddSeconds(2); DateTime.UtcNow < settle;)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Thread.Sleep(50);
            }

            var shots = Environment.GetEnvironmentVariable("THIXGATE_SCREENSHOTS");
            if (shots is not null)
            {
                Directory.CreateDirectory(shots);
                window.CaptureRenderedFrame()?.Save(Path.Combine(shots, $"session-{protocol}-{port}.png"));
            }

            var log = Directory.Exists(ThixGate.Core.Infrastructure.AppPaths.LogDirectory)
                ? string.Join(Environment.NewLine, Directory.GetFiles(ThixGate.Core.Infrastructure.AppPaths.LogDirectory).SelectMany(File.ReadAllLines).TakeLast(25))
                : "";
            Assert.True(window.IsConnected, $"not connected: {window.StateText}{Environment.NewLine}{log}");
            Assert.True(window.HasPicture, $"no picture arrived ({window.LastDrawError}, {window.Misses} misses, {window.Draws} draws){Environment.NewLine}{log}");
            Assert.True(window.PictureSize.Width >= 640, $"picture {window.PictureSize}");
            Assert.NotNull(LibraryService.FindConnection(connection.Id)!.LastUsed);
        }
        finally
        {
            window.Close();
        }

        for (var round = 0; round < 20; round++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }

        Assert.Empty(SessionWindow.OpenSessions);
    }
}
