using Avalonia.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>
/// Where the RDP / VNC sessions are: as tabs right of the menu of the main window (like Royal TS: the default) or in windows of their own
/// ("Eigenes Fenster", "Vollbild" in the settings; "Abdocken" of a tab). One session per connection: opening it again shows the open one. A
/// session moves between a tab and a window without ending ("Abdocken" / "Andocken"); full screen is a window of its own.
/// </summary>
public static class SessionHub
{
    private static readonly List<SessionView> Views = [];
    private static readonly Dictionary<SessionView, SessionWindow> InWindows = [];

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
        if (where == OpenSessionIn.Tab && Main is { } main)
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

        Main?.RemoveSessionTab(view);
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

            window.HandOver();
            Dock(view, main);
            main.ShowFromTray();
            return;
        }

        Main?.RemoveSessionTab(view);
        ToWindow(view);
    }

    /// <summary>Full screen: a window of its own in full screen (a tab moves into it); again: the window leaves full screen.</summary>
    private static void FullScreen(SessionView view)
    {
        if (InWindows.TryGetValue(view, out var window))
        {
            window.ToggleFullScreen();
            return;
        }

        Main?.RemoveSessionTab(view);
        ToWindow(view).WindowState = WindowState.FullScreen;
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
