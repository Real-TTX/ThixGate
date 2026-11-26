using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;
using ThixGate.Sessions;

namespace ThixGate.Tray;

/// <summary>
/// The icon in the notification area (Windows) or the menu bar (macOS), with MatDesktop's tray (TRAY-01 … TRAY-04): the menu in the
/// order of every Mat tray – "ThixGate 1.0.0", the state in words, "ThixGate öffnen", ThixGate's own items (favourites and the last
/// connections to open with one click, "Neue Verbindung …"), "Einstellungen …", "Log-Ordner öffnen", "Update installieren …" while one
/// waits, "Beenden". Windows: a left click opens the window, the right button the menu; macOS: every click opens the menu.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int ConnectionsInMenu = 8;

    private readonly App _app;
    private readonly MatTrayMenu _menu;
    private readonly MatTray _tray;
    private bool _updateAvailable;
    private bool _refreshPosted;

    public TrayIcon(App app)
    {
        _app = app;
        _menu = new MatTrayMenu(AppInfo.ProductName, AppInfo.Version, () => _app.ExitApplication())
        {
            Open = () => _app.ShowMainWindow(),
            Settings = () => _app.ShowMainWindow(window => window.ShowSettings()),
            LogFolder = () => Ui.OpenExternal(AppPaths.LogDirectory),
        };
        Update();
        _tray = new MatTray(app, _menu, Mark());
        _tray.MenuOpening += (_, _) => Update();
        SettingsService.Changed += OnChanged;
        LibraryService.Changed += OnChanged;
    }

    /// <summary>The model of the menu (MatTray builds the menu from it).</summary>
    public MatTrayMenu Menu => _menu;

    /// <summary>The state of the tray: ready, or the open sessions in words (a state of ThixGate's own, TRAY-02).</summary>
    public static MatState State
    {
        get
        {
            var open = SessionHub.OpenSessions.Count;
            return open > 0
                ? MatState.Custom(open == 1 ? "1 Sitzung offen" : $"{open} Sitzungen offen", "--mat-accent", MatStateMark.Dot)
                : MatState.Running;
        }
    }

    /// <summary>The mark the tray icons are drawn from: the logo on Windows, the template image (black on transparent) on macOS.</summary>
    public static Bitmap Mark(bool? template = null) =>
        new(AssetLoader.Open(new Uri((template ?? OperatingSystem.IsMacOS()) ? "avares://ThixGate/Assets/tray-macos.png" : "avares://ThixGate/Assets/logo-64.png")));

    public void SetUpdateAvailable(bool available)
    {
        _updateAvailable = available;
        Refresh();
    }

    /// <summary>A notification of ThixGate's own (TRAY-04: only for what the user did not cause); a click opens the window.</summary>
    public void ShowNotification(string title, string text, bool isWarning = false, Action<MainWindow>? navigate = null) =>
        MatNotification.Show(title, text, isWarning, () => _app.ShowMainWindow(navigate));

    public void Dispose()
    {
        SettingsService.Changed -= OnChanged;
        LibraryService.Changed -= OnChanged;
        _tray.Dispose();
    }

    /// <summary>Something changed: the menu, the tooltip and (only when the state looks different) the icon follow; coalesced.</summary>
    private void OnChanged(object? sender, object? e)
    {
        if (_refreshPosted)
        {
            return;
        }

        _refreshPosted = true;
        Ui.Post(() =>
        {
            _refreshPosted = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        Update();
        _tray.Refresh();
    }

    /// <summary>The model: the state, the favourites and last connections, whether an update waits.</summary>
    private void Update()
    {
        _menu.State = State;
        _menu.TooltipText = "Remote-Verbindungen";
        _menu.InstallUpdate = _updateAvailable ? () => _app.ShowMainWindow(window => window.ShowSettings(Pages.SettingsPage.UpdatesTab)) : null;
        _menu.ClearOwn();

        var favorites = LibraryService.Connections().Where(connection => connection.IsFavorite)
            .OrderBy(connection => connection.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var recent = LibraryService.Recent(ConnectionsInMenu).Where(connection => !connection.IsFavorite).ToList();
        var entries = favorites.Concat(recent).Take(ConnectionsInMenu).ToList();
        _menu.AddOwn("Verbinden", null, enabled: false);
        if (entries.Count == 0)
        {
            _menu.AddOwn("   Noch keine Favoriten oder zuletzt verwendeten", null, enabled: false);
        }

        foreach (var connection in entries)
        {
            var id = connection.Id;
            var (protocol, _) = Protocols.Of(connection.Protocol);
            _menu.AddOwn($"   {MenuName(connection.Name)} ({protocol})", () => _ = ConnectAsync(id));
        }

        _menu.AddOwn("   Alle Verbindungen …", () => _app.ShowMainWindow(window => window.ShowConnections()));
        _menu.AddOwnSeparator();
        _menu.AddOwn("Neue Verbindung …", () => _app.ShowMainWindow(window => window.ShowConnectionForm(null)), enabled: !AccessPolicy.IsConfigurationLocked);
    }

    private async Task ConnectAsync(Guid connectionId)
    {
        if (LibraryService.FindConnection(connectionId) is not { } connection)
        {
            return;
        }

        try
        {
            await SessionLauncher.OpenAsync(connection, null);
        }
        catch (BusinessException exception)
        {
            ShowNotification(connection.Name, exception.Message, isWarning: true, navigate: window => window.ShowConnectionForm(connectionId));
        }
    }

    private static string MenuName(string name) => name.Length <= 36 ? name : name[..35] + "…";
}
