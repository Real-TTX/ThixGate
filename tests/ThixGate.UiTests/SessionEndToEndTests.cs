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

    [AvaloniaFact]
    public void Rdp_session_in_a_tab_of_the_main_window_draws_the_remote_desktop()
    {
        if (Host is null)
        {
            return; // opt-in
        }

        using var data = new TestData();
        SettingsService.Update(settings => settings with { ConfirmCloseSession = false, OpenSessionIn = OpenSessionIn.Tab });
        var credential = CredentialService.Save(new StoredCredential { Name = "Test", UserName = "demo" }, "demo-only-pw");
        var connection = LibraryService.SaveConnection(new RemoteConnection { Name = "Testdesktop im Tab", Host = Host, Port = Ports[0], CredentialId = credential.Id });
        var main = new MainWindow { Width = 1360, Height = 860 };
        main.ShowFromTray();
        try
        {
            var view = SessionHub.Open(connection);
            Assert.Same(view, main.ShownSession);
            var until = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < until && !(view.IsConnected && view.HasPicture))
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Thread.Sleep(50);
            }

            for (var settle = DateTime.UtcNow.AddSeconds(2); DateTime.UtcNow < settle;)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Thread.Sleep(50);
            }

            if (Environment.GetEnvironmentVariable("THIXGATE_SCREENSHOTS") is { } shots)
            {
                Directory.CreateDirectory(shots);
                main.CaptureRenderedFrame()?.Save(Path.Combine(shots, "session-tab.png"));
            }

            Assert.True(view.IsConnected, $"not connected: {view.StateText}");
            Assert.True(view.HasPicture, $"no picture ({view.LastDrawError})");
            Assert.Empty(SessionWindow.OpenSessions);
        }
        finally
        {
            SessionHub.CloseAll();
            main.IsExiting = true;
            main.Close();
        }
    }

    /// <summary>
    /// SSH in ThixGate's own terminal, as a tab: THIXGATE_E2E_SSH=host:port of a test SSH server with the user demo / demo-only-pw, e.g.
    /// docker run -d -p 12222:2222 -e USER_NAME=demo -e USER_PASSWORD=demo-only-pw -e PASSWORD_ACCESS=true lscr.io/linuxserver/openssh-server.
    /// </summary>
    [AvaloniaFact]
    public void Ssh_session_in_a_tab_of_the_main_window_runs_a_shell()
    {
        if (Environment.GetEnvironmentVariable("THIXGATE_E2E_SSH") is not { } address)
        {
            return; // opt-in
        }

        using var data = new TestData();
        SettingsService.Update(settings => settings with { ConfirmCloseSession = false, OpenSessionIn = OpenSessionIn.Tab });
        var credential = CredentialService.Save(new StoredCredential { Name = "Test", UserName = "demo" }, "demo-only-pw");
        var connection = LibraryService.SaveConnection(new RemoteConnection
        {
            Name = "Linux-Server", Protocol = ConnectionProtocol.Ssh, Host = address.Split(':')[0], Port = int.Parse(address.Split(':')[1]), CredentialId = credential.Id,
        });

        // The server's key is trusted beforehand (the dialog that asks is not clicked here).
        Task.Run(async () =>
        {
            await using var first = new SshSession(connection, new TrustingHost());
            await first.ConnectAsync(80, 24, CancellationToken.None);
        }).GetAwaiter().GetResult();

        var main = new MainWindow { Width = 1360, Height = 860 };
        main.ShowFromTray();
        try
        {
            var view = SessionHub.Open(connection);
            var terminal = view.Terminal!;
            void Pump(double seconds, Func<bool> done)
            {
                for (var until = DateTime.UtcNow.AddSeconds(seconds); DateTime.UtcNow < until && !done();)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(50);
                }
            }

            Pump(20, () => view.IsConnected && terminal.Buffer.ScreenText().Contains('$'));
            Assert.True(view.IsConnected, $"not connected: {view.StateText}");
            terminal.Paste("printf '\\033[1;32mThixGate\\033[0m SSH im Tab: %s\\n' \"$(uname -s)\"; ls -la --color=always / | head -12");
            terminal.Focus();
            main.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None); // a pasted line runs on Enter (bracketed paste)
            Pump(10, () => terminal.Buffer.ScreenText().Contains("SSH im Tab: Linux", StringComparison.Ordinal));
            Pump(1, () => false);
            if (Environment.GetEnvironmentVariable("THIXGATE_SCREENSHOTS") is { } shots)
            {
                Directory.CreateDirectory(shots);
                main.CaptureRenderedFrame()?.Save(Path.Combine(shots, "session-ssh.png"));
            }

            Assert.Contains("SSH im Tab: Linux", terminal.Buffer.ScreenText());
            Assert.Same(view, main.ShownSession);
        }
        finally
        {
            SessionHub.CloseAll();
            main.IsExiting = true;
            main.Close();
        }
    }

    private sealed class TrustingHost : ISshHost
    {
        public Task<SshAnswer?> AskAsync(string userName, string prompt, bool failed, bool canRemember) => Task.FromResult<SshAnswer?>(null);

        public Task<bool> TrustHostKeyAsync(string host, int port, string algorithm, string fingerprint, KnownHost? previous) => Task.FromResult(true);
    }

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
