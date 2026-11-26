using Avalonia;
using Avalonia.Controls;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>
/// A window of its own around one <see cref="SessionView"/>: when the settings say "Eigenes Fenster" or "Vollbild", when a tab was undocked, or
/// for full screen. Closing it ends the session (after asking, when the setting says so); "Andocken" gives the session back to the main window.
/// </summary>
public sealed class SessionWindow : Window
{
    private bool _closeConfirmed;
    private bool _handedOver;

    internal SessionWindow(SessionView view)
    {
        View = view;
        var (protocol, _) = Protocols.Of(view.Connection.Protocol);
        Title = $"{view.Connection.Name} – {protocol} – ThixGate";
        Icon = Ui.AppIcon;
        Width = 1280;
        Height = 800;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.Use(BackgroundProperty, "Mat.Surface");
        view.IsDocked = false;
        Content = view;
    }

    /// <summary>The session in this window.</summary>
    public SessionView View { get; }

    /// <summary>The open sessions that are in windows of their own (tests).</summary>
    public static IReadOnlyList<SessionWindow> OpenSessions => [.. SessionHub.Windows];

    /// <summary>Opens a connection's session in a window of its own (tests, the end-to-end runs), whatever the settings say.</summary>
    public static SessionWindow Show(RemoteConnection connection) => SessionHub.OpenInWindow(connection);

    public bool IsConnected => View.IsConnected;

    public bool HasPicture => View.HasPicture;

    public PixelSize PictureSize => View.PictureSize;

    public string StateText => View.StateText;

    public string? LastDrawError => View.LastDrawError;

    public int Draws => View.Draws;

    public int Misses => View.Misses;

    public void ToggleFullScreen() => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    /// <summary>The session goes elsewhere (docked): the window closes without ending it.</summary>
    internal void HandOver()
    {
        _handedOver = true;
        Content = null;
        Close();
    }

    /// <summary>The application ends: close without asking.</summary>
    internal void CloseNow()
    {
        _closeConfirmed = true;
        Close();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_handedOver)
        {
            return;
        }

        if (!_closeConfirmed)
        {
            e.Cancel = true;
            if (await View.ConfirmCloseAsync())
            {
                _closeConfirmed = true;
                Close();
            }

            return;
        }

        SessionHub.Ended(View);
    }
}
