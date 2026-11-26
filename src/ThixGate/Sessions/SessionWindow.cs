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
        view.MinimizeRequested += Minimize;
        Content = view;
    }

    private void Minimize(object? sender, EventArgs e) => WindowState = WindowState.Minimized;

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

    /// <summary>Over these monitors (a desktop over several monitors, or one chosen monitor) instead of a normal window; empty: none.</summary>
    public IReadOnlyList<Matgate.Display.ScreenMonitor> Spanned { get; private set; } = [];

    public void ToggleFullScreen()
    {
        if (Spanned.Count > 0)
        {
            Unspan();
            return;
        }

        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    }

    /// <summary>
    /// Covers these monitors without a frame (one monitor: full screen on it). Their rectangle is in pixels; the window's size is in its
    /// own units, so it is divided by the scaling of the monitor it starts on.
    /// </summary>
    public void Span(IReadOnlyList<Matgate.Display.ScreenMonitor> monitors)
    {
        if (monitors.Count == 0)
        {
            return;
        }

        Spanned = monitors;
        var left = monitors.Min(monitor => monitor.Left);
        var top = monitors.Min(monitor => monitor.Top);
        var right = monitors.Max(monitor => monitor.Left + monitor.Width);
        var bottom = monitors.Max(monitor => monitor.Top + monitor.Height);
        WindowState = WindowState.Normal;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        MinWidth = 0;
        MinHeight = 0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(left, top);
        var scaling = Screens.ScreenFromPoint(new PixelPoint(left + 1, top + 1))?.Scaling ?? 1;
        Width = (right - left) / scaling;
        Height = (bottom - top) / scaling;
        View.IsFullScreen = true;
        Activate();
    }

    private void Unspan()
    {
        Spanned = [];
        SystemDecorations = SystemDecorations.Full;
        CanResize = true;
        MinWidth = 640;
        MinHeight = 420;
        Width = 1280;
        Height = 800;
        View.IsFullScreen = false;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            Position = new PixelPoint(area.X + (int)((area.Width - 1280 * screen.Scaling) / 2), area.Y + (int)((area.Height - 800 * screen.Scaling) / 2));
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && Spanned.Count == 0)
        {
            View.IsFullScreen = WindowState == WindowState.FullScreen;
        }
    }

    /// <summary>The session goes elsewhere (docked): the window closes without ending it.</summary>
    internal void HandOver()
    {
        _handedOver = true;
        View.MinimizeRequested -= Minimize;
        View.IsFullScreen = false;
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
