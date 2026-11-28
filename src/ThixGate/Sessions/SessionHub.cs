using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Matgate.Display;
using ThixGate.Core.Business;
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>
/// Where the RDP / VNC sessions are: as tabs right of the menu of the main window (like Royal TS: the default) or in windows of their own
/// ("Eigenes Fenster", "Vollbild" in the settings; "Abdocken" of a tab). One session per connection: opening it again shows the open one. A
/// session moves between a tab and a window without ending ("Abdocken" / "Andocken"); full screen is a window of its own. A Remote Desktop
/// over chosen monitors (<see cref="SessionDisplay.MonitorsFor"/>) opens as a window spanning them.
/// </summary>
public static class SessionHub
{
    private static readonly List<SessionView> Views = [];
    private static readonly Dictionary<SessionView, SessionWindow> InWindows = [];

    /// <summary>Sessions in full screen that came from their tab: leaving full screen docks them again.</summary>
    private static readonly HashSet<SessionView> FromTab = [];

    /// <summary>A session opened, ended, moved or changed its state (title bar, tray).</summary>
    public static event EventHandler? Changed;

    /// <summary>The main window that takes the tabs (set by it; null while there is none).</summary>
    public static MainWindow? Main { get; set; }

    /// <summary>Every open session, tab or window, in the order they were opened.</summary>
    public static IReadOnlyList<SessionView> OpenSessions => Views;

    internal static IEnumerable<SessionWindow> Windows => InWindows.Values;

    /// <summary>Opens a connection's session where the settings say (or shows it when it is open already).</summary>
    public static SessionView Open(RemoteConnection connection)
    {
        if (Find(connection.Id) is { } open)
        {
            Show(open);
            return open;
        }

        var view = Add(connection);
        var where = SettingsService.Get().OpenSessionIn;
        if (SessionDisplay.MonitorsFor(connection, AvailableMonitors()) is { Count: > 0 } monitors)
        {
            ToWindow(view).Span(monitors);
        }
        else if (where == OpenSessionIn.Tab && Main is { } main)
        {
            Dock(view, main);
            main.ShowFromTray(); // from the tray the main window may be hidden
        }
        else
        {
            var window = ToWindow(view);
            if (where == OpenSessionIn.FullScreen)
            {
                window.WindowState = WindowState.FullScreen;
            }
        }

        return view;
    }

    /// <summary>Opens a connection's session in a window of its own, whatever the settings say.</summary>
    public static SessionWindow OpenInWindow(RemoteConnection connection)
    {
        var view = Find(connection.Id) ?? Add(connection);
        if (InWindows.TryGetValue(view, out var existing))
        {
            existing.Activate();
            return existing;
        }

        LeaveTab(view);
        return ToWindow(view);
    }

    /// <summary>Brings a session to the front: its tab, or its window.</summary>
    public static void Show(SessionView view)
    {
        if (InWindows.TryGetValue(view, out var window))
        {
            window.Activate();
        }
        else
        {
            Main?.ShowFromTray();
            Main?.ShowSession(view);
        }
    }

    /// <summary>The tab of a session was closed: end it (after asking, when the setting says so).</summary>
    public static async Task CloseAsync(SessionView view)
    {
        if (InWindows.TryGetValue(view, out var window))
        {
            window.Close();
            return;
        }

        if (await view.ConfirmCloseAsync())
        {
            Main?.RemoveSessionTab(view);
            Ended(view);
        }
    }

    /// <summary>The application ends: every session is closed without asking.</summary>
    public static void CloseAll()
    {
        foreach (var window in InWindows.Values.ToList())
        {
            window.CloseNow();
        }

        foreach (var view in Views.ToList())
        {
            Main?.RemoveSessionTab(view);
            Ended(view);
        }
    }

    /// <summary>A session ended for good (its window or tab closed).</summary>
    internal static void Ended(SessionView view)
    {
        view.Shutdown();
        InWindows.Remove(view);
        FromTab.Remove(view);
        if (Views.Remove(view))
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    private static SessionView? Find(Guid connectionId) => Views.FirstOrDefault(view => view.Connection.Id == connectionId);

    private static SessionView Add(RemoteConnection connection)
    {
        var view = new SessionView(connection);
        view.StateChanged += (_, _) => Changed?.Invoke(null, EventArgs.Empty);
        view.DockRequested += (_, _) => MoveDockOrWindow(view);
        view.FullScreenRequested += (_, _) => FullScreen(view);
        Views.Add(view);
        Changed?.Invoke(null, EventArgs.Empty);
        return view;
    }

    /// <summary>"Abdocken" of a tab: into a window; "Andocken" of a window: back as a tab (when there is a main window).</summary>
    private static void MoveDockOrWindow(SessionView view)
    {
        if (InWindows.Remove(view, out var window))
        {
            if (Main is not { } main)
            {
                InWindows[view] = window;
                return;
            }

            FromTab.Remove(view);
            window.HandOver();
            Dock(view, main);
            main.ShowFromTray();
            return;
        }

        LeaveTab(view);
        ToWindow(view);
    }

    /// <summary>
    /// Full screen: a window of its own in full screen (a tab moves into it); again: the window leaves full screen - and a session that
    /// came from its tab goes back there.
    /// </summary>
    internal static void FullScreen(SessionView view)
    {
        var monitors = SessionDisplay.MonitorsFor(view.Connection, AvailableMonitors());
        if (InWindows.TryGetValue(view, out var window))
        {
            var isFull = window.Spanned.Count > 0 || window.WindowState == WindowState.FullScreen;
            if (isFull && FromTab.Remove(view) && Main is not null)
            {
                MoveDockOrWindow(view);
                return;
            }

            // Back over its monitors when it has some, else full screen on and off.
            if (window.Spanned.Count == 0 && window.WindowState != WindowState.FullScreen && monitors.Count > 0)
            {
                window.Span(monitors);
            }
            else
            {
                window.ToggleFullScreen();
            }

            return;
        }

        if (Main is not null)
        {
            FromTab.Add(view);
        }

        LeaveTab(view);
        var created = ToWindow(view);
        if (monitors.Count > 0)
        {
            created.Span(monitors);
            return;
        }

        created.WindowState = WindowState.FullScreen;
    }

    /// <summary>The monitors of this computer, in pixels (from any window ThixGate has).</summary>
    public static IReadOnlyList<ScreenMonitor> AvailableMonitors()
    {
        TopLevel? top = Main;
        if (top is null && Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            top = desktop.Windows.FirstOrDefault();
        }

        return top is Window { Screens: { } screens }
            ? [.. screens.All.Select(screen => new ScreenMonitor(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height, screen.IsPrimary))]
            : [];
    }

    /// <summary>
    /// The session leaves its tab for a window. The main window's layout runs once right away: it may still hold the session's parts
    /// for its next pass, and running that pass after they moved into the other window makes Avalonia throw ("Attempt to call
    /// InvalidateArrange on wrong LayoutManager") - the window then stays half laid out.
    /// </summary>
    private static void LeaveTab(SessionView view)
    {
        if (Main is not { } main)
        {
            return;
        }

        main.RemoveSessionTab(view);
        main.UpdateLayout();
    }

    private static void Dock(SessionView view, MainWindow main)
    {
        view.IsDocked = true;
        main.AddSessionTab(view);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static SessionWindow ToWindow(SessionView view)
    {
        var window = new SessionWindow(view);
        InWindows[view] = window;
        window.Show();
        Changed?.Invoke(null, EventArgs.Empty);
        return window;
    }
}
