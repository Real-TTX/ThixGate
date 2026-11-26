using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using MatCloud.Contract;
using ThixGate.Core.Business;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate;

/// <summary>
/// "ThixGate.exe --self-test &lt;report file&gt;": the published program checks itself – everything trimming or
/// single-file packaging could break silently: JSON of settings, library, sync and update manifest, the vault, the remote
/// engine (Mat.Remote) and its targets, the protected token store, Windows shortcuts (COM) and every window
/// of the app with its bindings (from the sample data). It runs in a fresh temporary data folder (Program sets it), never touches the
/// user's data, writes a report and ends with exit code 0 (all passed) or 1. CI runs it on every Windows build.
/// </summary>
internal static class SelfTest
{
    public const string Argument = "--self-test";

    public static async Task<int> RunAsync(string reportPath)
    {
        var report = new StringBuilder($"ThixGate {AppInfo.Version} self test, data folder {AppPaths.DataDirectory}{Environment.NewLine}");
        var failures = 0;

        async Task Check(string name, Func<Task<string>> check)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var note = await check();
                report.AppendLine($"ok    {name} ({watch.ElapsedMilliseconds} ms){(note.Length > 0 ? " – " + note : "")}");
            }
            catch (Exception exception)
            {
                failures++;
                report.AppendLine($"FAIL  {name}: {exception}");
            }
        }

        await Check("settings (JSON)", () =>
        {
            SettingsService.Update(settings => settings with { ConfirmCloseSession = false, Theme = ThemeChoice.Dark, OpenSessionIn = OpenSessionIn.FullScreen });
            SettingsStore.Reset();
            var read = SettingsService.Get();
            Ensure(read is { ConfirmCloseSession: false, Theme: ThemeChoice.Dark, OpenSessionIn: OpenSessionIn.FullScreen }, "settings did not survive a save");
            return Task.FromResult("");
        });

        await Check("update manifest (JSON)", () =>
        {
            const string json = """{"version":"0.1.9-20261115","channel":"release","buildNumber":9,"setup":"ThixGateSetup.exe","sha256":"AB","packages":{"win-x64":{"file":"ThixGateSetup.exe","sha256":"AB"}}}""";
            var manifest = new HttpResponseMessage { Content = new StringContent(json, Encoding.UTF8, "application/json") }.Content
                .ReadFromJsonAsync<UpdateManifest>().GetAwaiter().GetResult();
            Ensure(manifest is { BuildNumber: 9 } && UpdateService.PackageFor(manifest, "win-x64")?.File == "ThixGateSetup.exe", "manifest not read");
            return Task.FromResult("");
        });

        await Check("MatCloud contract (JSON)", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var item = new SyncItemDto { Collection = SyncCollections.Connections, Key = Guid.NewGuid().ToString("D"), Data = "{\"name\":\"x\"}", UpdatedAt = now, Revision = 3 };
            var json = JsonSerializer.Serialize(new SyncChangeSet([item], 3, false), CloudContract.Json);
            var back = JsonSerializer.Deserialize<SyncChangeSet>(json, CloudContract.Json);
            Ensure(back is { Latest: 3 } && back.Items[0].Key == item.Key, "change set not read back");
            Ensure(CloudApps.Find(CloudApps.Gate)?.Name == "ThixGate", "app id missing in the contract");
            return Task.FromResult("");
        });

        await Check("library (JSON)", () =>
        {
            var folder = LibraryService.SaveFolder(new RemoteFolder { Name = "Selbsttest" });
            var credential = CredentialService.Save(new StoredCredential { Name = "Selbsttest", UserName = "probe" }, "geheim");
            var connection = LibraryService.SaveConnection(new RemoteConnection
            {
                Name = "Selbsttest", Protocol = ConnectionProtocol.Rdp, Host = "probe.invalid", FolderId = folder.Id, CredentialId = credential.Id,
            });
            LibraryStore.Reset();
            Ensure(LibraryService.FindConnection(connection.Id) is { FolderId: not null } && CredentialService.ReadPassword(credential.Id) == "geheim",
                "library or vault not read back");
            var target = SessionTargets.ScreenTargetOf(LibraryService.FindConnection(connection.Id)!, SessionTargets.LoginOf(connection), 1280, 720);
            Ensure(target is { Protocol: "rdp", Port: 3389, UserName: "probe", Password: "geheim" }, "engine target not built");
            LibraryService.DeleteConnection(connection.Id);
            CredentialService.Delete(credential.Id);
            LibraryService.DeleteFolder(folder.Id);
            return Task.FromResult($"{LibraryStore.Pending(100).Count} changes for the cloud");
        });

        await Check("remote engine (Mat.Remote)", () =>
        {
            var host = new Sessions.NullScreenHost();
            var backend = Matgate.Display.ScreenBackends.Create(new Matgate.Display.ScreenTarget { Protocol = "vnc", Host = "probe.invalid", Port = 5900 }, host);
            Ensure(backend.Name == "vnc", "VNC backend not created");
            backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
            backend = Matgate.Display.ScreenBackends.Create(new Matgate.Display.ScreenTarget { Protocol = "rdp", Host = "probe.invalid", Port = 3389 }, host);
            Ensure(backend.Name == "rdp", "RDP backend not created");
            backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return Task.FromResult("");
        });

        await Check("SSH (SSH.NET) and terminal", async () =>
        {
            var buffer = new Core.Terminal.TerminalBuffer(40, 5);
            buffer.Feed("\u001b[1;32mgrün\u001b[0m\r\nzwei");
            Ensure(buffer.Text(0) == "grün" && buffer.Row(0)[0].Foreground == 2 && buffer.Text(1) == "zwei", "the terminal did not read its input");
            var connection = new RemoteConnection { Name = "Selbsttest", Protocol = ConnectionProtocol.Ssh, Host = "127.0.0.1", Port = 1, UserName = "probe" };
            await using var session = new SshSession(connection, new RefusingSshHost());
            try
            {
                await session.ConnectAsync(80, 24, CancellationToken.None);
                throw new InvalidOperationException("connected to port 1");
            }
            catch (BusinessException exception)
            {
                return exception.Message;
            }
        });

        await Check("install options (installer.json, arguments)", () =>
        {
            Ensure(InstallerOptions.Config is { Autostart: not null, DesktopShortcut: not null, AutomaticUpdates: not null }, "installer.json not read");
            var request = InstallerOptions.Parse([InstallerOptions.QuietArgument, "--autostart=no", "--desktop-shortcut=yes"]);
            Ensure(request is { Action: SetupAction.Install, Choices: { Autostart: false, DesktopShortcut: true } }, "arguments not read");
            return Task.FromResult("");
        });

        await Check("token store", () =>
        {
            var stored = TokenStore.Save("self-test", "geheim");
            Ensure(TokenStore.Read("self-test", stored) == "geheim", "token not read back");
            TokenStore.Delete("self-test");
            return Task.FromResult("");
        });

        if (OperatingSystem.IsWindows())
        {
            await Check("shortcut (COM)", () =>
            {
                var link = Path.Combine(AppPaths.DataDirectory, "ThixGate.lnk");
                Core.Platform.Windows.ShellLink.Write(link, App.ExecutablePath, AppPaths.DataDirectory, "Selbsttest", App.ExecutablePath);
                Ensure(File.Exists(link) && new FileInfo(link).Length > 0, "no .lnk written");
                return Task.FromResult("");
            });
        }

        await Check("windows and bindings", async () =>
        {
            SampleData.Create();
            var window = new MainWindow();
            window.ShowFromTray();
            await Settle();
            Ensure(window.ConnectionsPage.List.ItemCount >= 6, $"the connection page shows {window.ConnectionsPage.List.ItemCount} rows");
            window.ShowConnectionForm(null);
            await Settle();
            window.ShowConnectionForm(SampleData.TerminalServerId);
            await Settle();
            window.ShowFolders();
            await Settle();
            Ensure(window.FoldersPage.List.ItemCount > 0, "no folder listed");
            window.ShowFolderForm(SampleData.MuellerFolderId);
            await Settle();
            window.ShowCredentials();
            await Settle();
            Ensure(window.CredentialsPage.List.ItemCount > 0, "no credentials listed");
            window.ShowCredentialForm(SampleData.AdminCredentialId);
            await Settle();
            window.ShowMatgate();
            await Settle();
            window.ShowMatgateForm(SampleData.MatgateAccountId);
            await Settle();
            window.ShowSettings();
            await Settle();
            window.ShowAbout();
            await Settle();
            if (OperatingSystem.IsWindows())
            {
                var setup = Setup.ThixGateSetup.Create(isUninstall: false, InstallService.IsInstalled, InstallService.InstalledVersion, InstallService.ProgramFolder, _ => Task.CompletedTask);
                setup.Show();
                await Settle();
                setup.Close();
            }

            ProblemReports.WriteHang(TimeSpan.FromSeconds(9), withDump: false);
            Dialogs.ProblemReportDialog.ShowUnseen()?.Close();
            window.IsExiting = true;
            window.Close();
            return "";
        });

        await Check("database grid and files tab", async () =>
        {
            // the result grid binds by index (trimming must keep it) and the files table draws
            var grid = Sessions.ResultGrids.For(new Matgate.Services.Database.DbResult(["id", "name"], [["1", "Anna"], ["2", null]], null, false, 0));
            var window = new Avalonia.Controls.Window { Width = 500, Height = 300, Content = grid };
            window.Show();
            await Settle();
            Ensure(grid.Columns.Count == 2 && grid.ItemsSource is System.Collections.ICollection { Count: 2 }, "the result grid has no rows");
            window.Content = new Sessions.FilesView(new RemoteConnection { Name = "Selbsttest", Protocol = ConnectionProtocol.Sftp, Host = "probe.invalid" });
            await Settle();
            window.Close();
            return "";
        });

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            await Check("website browser (WebView2)", async () =>
            {
                var browser = new Sessions.WebView2Browser();
                var window = new Avalonia.Controls.Window { Width = 640, Height = 400, Content = browser };
                window.Show();
                await Settle();
                try
                {
                    await browser.StartAsync(new Sessions.BrowserOptions(new Uri("data:text/html,<title>ThixGate</title><p>Selbsttest</p>"),
                        Path.Combine(AppPaths.DataDirectory, "web", "self-test"), null, false, () => null, []));
                    var loaded = await browser.FirstPage.WaitAsync(TimeSpan.FromSeconds(30));
                    Ensure(loaded, "the page did not load");
                    return browser.EngineVersion;
                }
                finally
                {
                    window.Close();
                }
            });
        }
#endif

        report.AppendLine(failures == 0 ? "PASSED" : $"FAILED ({failures})");
        await File.WriteAllTextAsync(reportPath, report.ToString(), Encoding.UTF8);

        // The temporary data folder goes again (unless the report was written into it).
        LibraryStore.Reset();
        if (!Path.GetFullPath(reportPath).StartsWith(Path.GetFullPath(AppPaths.DataDirectory), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.Delete(AppPaths.DataDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>The self test signs in nowhere: every question is answered "no".</summary>
    private sealed class RefusingSshHost : ISshHost
    {
        public Task<SshAnswer?> AskAsync(string userName, string prompt, bool failed, bool canRemember) => Task.FromResult<SshAnswer?>(null);

        public Task<bool> TrustHostKeyAsync(string host, int port, string algorithm, string fingerprint, KnownHost? previous) => Task.FromResult(false);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task Settle()
    {
        for (var round = 0; round < 10; round++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(30);
        }
    }
}
