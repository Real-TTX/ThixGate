using System.Net.NetworkInformation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MatDesktop;
using ThixGate.Core.Business;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;
using TrayIcon = ThixGate.Tray.TrayIcon;

namespace ThixGate;

/// <summary>
/// Start-up and end of ThixGate.
///  - Windows: the published ThixGate.exe is also its setup (RunSetupIfRequested).
///  - The look is MatDesktop's theme with ThixGate's accent (DESK-02).
///  - One instance per user and data folder (MatSingleInstance, DESK-05): a second start opens the window of the first one.
///  - "--autostart" (sign-in) and "--tray" (after an update) start hidden in the tray / menu bar.
///  - The cloud sync (ThixCloud) starts with the app; the end stops it and closes the open sessions.
/// </summary>
public partial class App : Application
{
    public const string TrayArgument = "--tray";

    /// <summary>ThixGate's accent (IDENT-01): MatDesktop derives every accent token of light and dark from it.</summary>
    public const string Accent = "#4B3FCF";

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MatSingleInstance? _instance;
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private readonly CancellationTokenSource _matgateLoop = new();
    private bool _isExiting;

    /// <summary>The program file; the autostart entry points to it.</summary>
    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ThixGate");

    public static App Instance => (App)Current!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // ThixGate speaks German, the texts of MatDesktop (title bar, menu, tray, setup) too.
        MatText.Language = "de";
        // the icon of every window (MatWindow.DefaultIcon): the main window, the dialogs, the setup
        MatTheme.Install(this, new MatThemeOptions(Accent) { Icon = Ui.AppIcon });
        // the result grids of the database tab: Fluent's DataGrid, in MatDesktop's colours (its Fluent resources)
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://ThixGate/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"),
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        _desktop = desktop;
        var args = desktop.Args ?? [];

        var selfTest = Array.IndexOf(args, SelfTest.Argument);
        if (selfTest >= 0)
        {
            var report = selfTest + 1 < args.Length ? args[selfTest + 1] : Path.Combine(AppPaths.DataDirectory, "self-test.txt");
            Dispatcher.UIThread.Post(async () => EndProcess(await SelfTest.RunAsync(report)));
            return;
        }

        if (RunSetupIfRequested(args))
        {
            return;
        }

        if (!TakeSingleInstance())
        {
            EndProcess(0);
            return;
        }

        Start(args);
    }

    /// <summary>The main window (null during setup).</summary>
    public MainWindow? MainWindow => _mainWindow;

    /// <summary>
    /// Ends the process through the main loop. Shutting down straight from OnFrameworkInitializationCompleted (update,
    /// quiet uninstall, second instance) throws "Dispatcher shut down" because the loop has not started yet.
    /// </summary>
    private void EndProcess(int exitCode) => Dispatcher.UIThread.Post(() => _desktop?.Shutdown(exitCode));

    /// <summary>Keeps the libraries of the signed-in Matgate accounts current: at the start and every five minutes.</summary>
    private static async Task KeepMatgateLibrariesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Run(() => MatgateService.RefreshAllAsync(cancellationToken), cancellationToken);
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // ThixGate ends.
        }
    }

    /// <summary>Installed by ThixLauncher: its sign-in at MatCloud goes along (in the background; a cloud that does not answer changes nothing).</summary>
    private async Task ConnectFromLauncherAsync()
    {
        if (await Task.Run(() => CloudSyncService.ConnectFromLauncherAsync()) is { } account)
        {
            Ui.Post(() => _trayIcon?.ShowNotification("Mit der ThixCloud verbunden",
                $"ThixGate gleicht deine Verbindungen jetzt mit der ThixCloud ab (Konto {account}, angemeldet von ThixLauncher)."));
        }
    }

    private void Start(string[] args)
    {
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        UiWatchdog.Start();
        Log.Info($"ThixGate {AppInfo.Version} starting on {UpdateService.PlatformKey} ({string.Join(' ', args)})");

        if (!SettingsService.Get().FirstStartDone)
        {
            SettingsService.Remember(settings => settings with { FirstStartDone = true });
        }

        ApplyTheme(SettingsService.Get().Theme);
        SettingsService.Changed += (_, settings) => Ui.Post(() => ApplyTheme(settings.Theme));

        _mainWindow = new MainWindow();
        _trayIcon = new TrayIcon(this);

        CloudSyncService.Start();
        _ = KeepMatgateLibrariesAsync(_matgateLoop.Token);
        _ = ConnectFromLauncherAsync();

        var startHidden = args.Contains(TrayArgument) || args.Contains(AutostartService.StartupArgument);
        if (!startHidden)
        {
            _mainWindow.ShowFromTray();
        }

        // Crashed or hung last time: say so, with the report.
        Dispatcher.UIThread.Post(() => Dialogs.ProblemReportDialog.ShowUnseen(), DispatcherPriority.Background);

        // macOS: opening the app again (Finder, Spotlight, Dock) shows the window.
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, activation) =>
            {
                if (activation.Kind == ActivationKind.Reopen)
                {
                    ShowMainWindow();
                }
            };
        }

        UpdateCoordinator.Checked += (_, result) =>
        {
            _trayIcon?.SetUpdateAvailable(result.IsUpdateAvailable);
            if (result is { IsUpdateAvailable: true, Manifest: { } manifest } && !SettingsService.Get().InstallUpdatesAutomatically)
            {
                _trayIcon?.ShowNotification("Update verfügbar", $"ThixGate {manifest.Version} kann installiert werden (Einstellungen › Updates).");
            }
        };
        UpdateCoordinator.Start();


        // After sleep, a network change or a new Wi-Fi the cloud is asked at once.
        NetworkChange.NetworkAvailabilityChanged += (_, availability) =>
        {
            if (availability.IsAvailable)
            {
                CloudSyncService.Kick();
            }
        };
        NetworkChange.NetworkAddressChanged += (_, _) => CloudSyncService.Kick();

        CloudSyncService.StatusChanged += (_, status) =>
        {
            if (status.State == CloudState.SignedOut)
            {
                Ui.Post(() => _trayIcon?.ShowNotification("ThixCloud", status.Message, isWarning: true));
            }
        };
    }

    /// <summary>Opens the main window (tray, second start, Dock) and optionally navigates.</summary>
    public void ShowMainWindow(Action<MainWindow>? navigate = null)
    {
        if (_mainWindow is null || _isExiting)
        {
            return;
        }

        _mainWindow.ShowFromTray();
        navigate?.Invoke(_mainWindow);
    }

    /// <summary>The setting "Erscheinungsbild": Wie das System, Hell, Dunkel (DESK-02).</summary>
    public static void ApplyTheme(ThemeChoice theme) => MatTheme.SetAppearance(theme switch
    {
        ThemeChoice.Light => MatAppearance.Light,
        ThemeChoice.Dark => MatAppearance.Dark,
        _ => MatAppearance.System,
    });

    /// <summary>"Beenden": ends the application.</summary>
    public void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        UiWatchdog.Stop();
        Log.Info("ThixGate exiting");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        void Step(string name, Action action)
        {
            var started = watch.ElapsedMilliseconds;
            action();
            if (watch.ElapsedMilliseconds - started > 300)
            {
                Log.Warning($"Exit step {name} took {watch.ElapsedMilliseconds - started} ms");
            }
        }

        Step("sessions", Sessions.SessionHub.CloseAll);
        Step("cloud sync", CloudSyncService.Stop);
        Step("matgate", () =>
        {
            _matgateLoop.Cancel();
            MatgateRelay.CloseAll();
        });
        Step("tray icon", () => _trayIcon?.Dispose());
        if (_mainWindow is not null)
        {
            _mainWindow.IsExiting = true;
            Step("main window", _mainWindow.Close);
        }

        _instance?.Dispose();
        Log.Info($"ThixGate ended after {watch.ElapsedMilliseconds} ms");
        _desktop?.Shutdown(0);
    }

    /// <summary>
    /// The command line of SETUP-05 with the install options of SETUP-08 (InstallerOptions; a wrong value: exit code 2).
    /// Windows: the published ThixGate.exe is its own setup (InstallService):
    ///  - "--uninstall" (from "Installierte Apps"): the uninstall assistant, or silently with "--quiet".
    ///  - "--quiet [options]" (ThixLauncher, ThixInstall, an administrator): installs without a window; "--start" starts it in the tray.
    ///  - "--update [options]" (downloaded by the update check): installs itself silently and starts the installed ThixGate in the tray.
    ///  - started anywhere else than the program folder (e.g. from Downloads): the setup assistant.
    /// Both systems: "--configure [options]" applies the options to the installed ThixGate and ends (macOS: ThixLauncher calls
    /// it after it copied the bundle). A development build (not single-file) runs as the application otherwise.
    /// </summary>
    private bool RunSetupIfRequested(string[] args)
    {
        SetupRequest request;
        try
        {
            request = InstallerOptions.Parse(args);
        }
        catch (BusinessException exception)
        {
            Log.Warning($"Setup arguments refused: {exception.Message}");
            Console.Error.WriteLine(exception.Message);
            EndProcess(2);
            return true;
        }

        if (request.Action == SetupAction.Configure)
        {
            var executable = OperatingSystem.IsWindows() && File.Exists(InstallService.InstalledExecutable) ? InstallService.InstalledExecutable : ExecutablePath;
            InstallerOptions.Apply(request.Choices, executable, Log.Info);
            EndProcess(0);
            return true;
        }

        return OperatingSystem.IsWindows() && RunWindowsSetup(request);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool RunWindowsSetup(SetupRequest request)
    {
        if (request.Action == SetupAction.Uninstall)
        {
            if (request.Quiet)
            {
                if (InstallService.Uninstall(deleteUserData: false, Log.Info))
                {
                    InstallService.ScheduleProgramFolderRemoval();
                }

                EndProcess(0);
                return true;
            }

            Setup.ThixGateSetup.Run(isUninstall: true, removeProgramFolder =>
            {
                if (removeProgramFolder)
                {
                    InstallService.ScheduleProgramFolderRemoval();
                }

                EndProcess(0);
            });
            return true;
        }

        if (request.Action is SetupAction.Install or SetupAction.Update)
        {
            var exitCode = 0;
            try
            {
                InstallService.Install(ExecutablePath, request.Choices, Log.Info);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                                  or System.Runtime.InteropServices.COMException or System.Security.SecurityException)
            {
                Log.Error(request.Action == SetupAction.Update ? "Update failed" : "Quiet install failed", exception);
                Console.Error.WriteLine(exception.Message);
                exitCode = 1;
            }

            // an update starts ThixGate again in the tray; a quiet install only with "--start"
            if ((request.Action == SetupAction.Update || request.Start) && File.Exists(InstallService.InstalledExecutable))
            {
                InstallService.StartInstalled(TrayArgument);
            }

            EndProcess(exitCode);
            return true;
        }

#if SINGLE_FILE
        const bool isPublishedBuild = true;
#else
        const bool isPublishedBuild = false;
#endif
        if (isPublishedBuild && !InstallService.IsInstalledExecutable(ExecutablePath))
        {
            Setup.ThixGateSetup.Run(isUninstall: false, _ => EndProcess(0));
            return true;
        }

        return false;
    }

    /// <summary>
    /// One instance per user and data folder (MatSingleInstance, DESK-05: a named mutex and a pipe, on macOS a lock file
    /// and a socket): a second start hands its arguments to the first one, which shows its window, and ends. A development
    /// run with its own THIXGATE_DATA_DIR does not meet the installed one. On Windows the setup may also ask the running
    /// instance to end (ListenForExitSignal).
    /// </summary>
    private bool TakeSingleInstance()
    {
        _instance = new MatSingleInstance(AppInfo.ProductName, AppPaths.HasOverride ? AppPaths.DataDirectory : null);
        _instance.SecondStart += (_, _) => Ui.Post(() => ShowMainWindow());
        if (!_instance.TryClaim(_desktop?.Args ?? []))
        {
            Log.Info("ThixGate runs already: its window was asked to show");
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            ListenForExitSignal();
        }

        return true;
    }

    /// <summary>The setup asks a running ThixGate to end (update, uninstall).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ListenForExitSignal()
    {
        if (AppPaths.HasOverride)
        {
            return; // a development run with its own data folder is none of the setup's business
        }

        var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstallService.ExitSignalName);
        var listener = new Thread(() =>
        {
            exitSignal.WaitOne();
            Ui.Post(() =>
            {
                Log.Info("Setup asked ThixGate to end");
                ExitApplication();
            });
        })
        {
            IsBackground = true,
            Name = "ThixGate exit signal listener",
        };
        listener.Start();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error", e.Exception);
        e.Handled = true;
        if (e.Exception is BusinessException business)
        {
            _trayIcon?.ShowNotification("ThixGate", business.Message, isWarning: true);
            return;
        }

        _trayIcon?.ShowNotification("Unerwarteter Fehler", $"{e.Exception.Message} – Details im Protokoll ({AppPaths.LogDirectory}).", isWarning: true);
    }
}

