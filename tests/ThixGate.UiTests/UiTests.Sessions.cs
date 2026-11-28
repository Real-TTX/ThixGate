using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using ThixGate.Core.Business;
using ThixGate.Core.Models;
using ThixGate.Sessions;

namespace ThixGate.UiTests;

/// <summary>Remote Desktop from this computer: monitors and drives in the form, the bar at the top edge in full screen, input as the engine reads it.</summary>
public sealed partial class UiTests
{
    [AvaloniaFact]
    public void Full_screen_from_a_tab_fills_a_window_and_docks_back()
    {
        using var data = new TestData();
        SettingsService.Update(settings => settings with { OpenSessionIn = OpenSessionIn.Tab, ConfirmCloseSession = false });
        var connection = LibraryService.SaveConnection(new RemoteConnection { Name = "Terminalserver", Host = "127.0.0.1", Port = 1 });
        var window = Open();
        try
        {
            var view = SessionHub.Open(connection);
            Settle(window);
            Assert.Same(view, window.ShownSession);

            // "Vollbild" in the tab: a window of its own in full screen, laid out without an error (Avalonia threw "wrong LayoutManager"
            // when the main window still held the session's parts), with the connection bar instead of the bars above and below.
            SessionHub.FullScreen(view);
            Settle(window);
            var full = Assert.Single(SessionWindow.OpenSessions);
            Settle(full);
            Assert.Equal(WindowState.FullScreen, full.WindowState);
            Assert.True(view.IsFullScreen);
            Assert.False(view.StatusBar.IsVisible);
            Assert.Same(full, TopLevel.GetTopLevel(view));
            Assert.Single(window.SessionTabs);
            Assert.Equal(full.Bounds.Size, view.Bounds.Size);
            view.RevealBarNow();
            Settle(full);
            Assert.True(view.IsFullBarShown);

            // "Vollbild beenden": back in its tab.
            SessionHub.FullScreen(view);
            Settle(window);
            Assert.Empty(SessionWindow.OpenSessions);
            Assert.Same(view, window.ShownSession);
            Assert.Same(window, TopLevel.GetTopLevel(view));
            Assert.False(view.IsFullScreen);
            Assert.Equal(2, window.SessionTabs.Count);
        }
        finally
        {
            SessionHub.CloseAll();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_display_tab_of_a_remote_desktop_has_monitors_and_drives()
    {
        using var data = new TestData();
        var window = Open();
        window.ShowConnectionForm(null);
        Settle(window);
        var form = window.ConnectionForm;
        T Named<T>(string name) where T : Control => form.GetLogicalDescendants().OfType<T>().First(control => AutomationProperties.GetName(control) == name);

        form.ProtocolChoice.SelectedIndex = (int)ConnectionProtocol.Rdp;
        Settle(window);
        var monitors = Named<ComboBox>("Monitore");
        var drives = Named<ComboBox>("Laufwerke");
        Assert.True(monitors.IsEffectivelyVisible || !form.DisplayTab.IsSelected);
        form.DisplayTab.IsSelected = true;
        Settle(window);
        Assert.True(monitors.IsEffectivelyVisible);
        Assert.True(drives.IsEffectivelyVisible);

        drives.SelectedIndex = 2;
        var folders = Named<TextBox>("Freigegebene Ordner");
        Settle(window);
        Assert.True(folders.IsEffectivelyVisible);
        folders.Text = data.Folder;
        monitors.SelectedIndex = 2;
        Save(window, "connection-form-display");

        form.NameBox.Text = "Terminalserver";
        form.HostBox.Text = "ts01.buero.local";
        form.Layout.Save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        var saved = Assert.Single(LibraryService.Connections(), connection => connection.Name == "Terminalserver");
        Assert.Equal(("all", data.Folder), (saved.Monitors, saved.Drives));

        // VNC has neither.
        window.ShowConnectionForm(null);
        Settle(window);
        window.ConnectionForm.ProtocolChoice.SelectedIndex = (int)ConnectionProtocol.Vnc;
        window.ConnectionForm.DisplayTab.IsSelected = true;
        Settle(window);
        Assert.False(window.ConnectionForm.GetLogicalDescendants().OfType<ComboBox>().First(control => AutomationProperties.GetName(control) == "Monitore").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Full_screen_puts_the_bar_at_the_top_edge()
    {
        using var data = new TestData();
        var window = Open();
        var view = new SessionView(new RemoteConnection { Name = "Terminalserver", Protocol = ConnectionProtocol.Rdp, Host = "ts01.invalid" });
        Assert.False(view.IsFullBarShown);

        // Entering full screen does not show the bar: only the grip at the top edge.
        view.IsFullScreen = true;
        Assert.False(view.IsFullBarShown);
        Assert.True(view.IsHandleShown);

        // The pointer rested at the top edge: the bar comes, with its menu, name and the window's buttons.
        view.RevealBarNow();
        Assert.True(view.IsFullBarShown);
        Assert.False(view.IsHandleShown);
        var names = view.FullBar.GetLogicalDescendants().OfType<Button>().Select(AutomationProperties.GetName).ToList();
        Assert.Equal(["Menü der Sitzung", "Minimieren", "Vollbild beenden", "Trennen"], names);
        Assert.Contains(view.FullBar.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "Terminalserver");

        // The pointer stays on it or just below (on macOS: the menu bar took it there): it stays; further down it goes.
        view.TrackPointer(20);
        view.HideBarIfAway();
        Assert.True(view.IsFullBarShown);
        view.TrackPointer(400);
        view.HideBarIfAway();
        Assert.False(view.IsFullBarShown);
        Assert.True(view.IsHandleShown);

        // A quick move to the top does not bring it; resting there does.
        view.TrackPointer(0);
        Assert.True(view.IsRevealPending);
        view.TrackPointer(300);
        Assert.False(view.IsRevealPending);
        view.TrackPointer(3);
        Assert.True(view.IsRevealPending);
        Assert.False(view.IsFullBarShown);

        view.IsFullScreen = false;
        Assert.False(view.IsFullBarShown);
        Assert.False(view.IsHandleShown);

        // "Leiste immer zeigen": it is there at once.
        SettingsService.Update(settings => settings with { FullScreenBar = "always" });
        view.IsFullScreen = true;
        Assert.True(view.IsFullBarShown);
        Assert.False(view.IsHandleShown);
        window.Close();
    }

    [AvaloniaFact]
    public void The_status_says_the_way_short_and_over_Matgate_both_legs()
    {
        using var data = new TestData();
        SampleData.Create();
        var direct = new SessionView(new RemoteConnection { Name = "PC", Protocol = ConnectionProtocol.Rdp, Host = "pc.invalid" });
        Assert.Equal("Direkt", direct.RouteText());
        Assert.Equal("14 ms", direct.LatencyText(14));

        var onMatgate = new SessionView(new RemoteConnection
        {
            Name = "Home", Protocol = ConnectionProtocol.Rdp, Host = "home.invalid", Via = ConnectVia.Matgate, MatgateAccountId = SampleData.MatgateAccountId, SessionOnMatgate = true,
        });
        Assert.Equal("Auf Matgate · Matgate Büro", onMatgate.RouteText());
        var through = new SessionView(new RemoteConnection { Name = "ERP", Protocol = ConnectionProtocol.Rdp, Host = "erp.invalid", Via = ConnectVia.Matgate, MatgateAccountId = SampleData.MatgateAccountId });
        Assert.Equal("Über Matgate · Matgate Büro", through.RouteText());

        // To Matgate + from there to the host, when Matgate tells it; else the total alone.
        through.SetLegs(12, 31.5);
        Assert.Equal("12 + 32 ms", through.LatencyText(43.5));
        through.SetLegs(12, null);
        Assert.Equal("12 ms", through.LatencyText(12));
    }

    [AvaloniaFact]
    public void Full_screen_over_monitors_fills_them_with_the_bar_in_the_middle_of_the_main_one()
    {
        using var data = new TestData();
        var connection = new RemoteConnection { Name = "Terminalserver", Protocol = ConnectionProtocol.Rdp, Host = "ts01.invalid" };

        // The margins put the bar's middle where it is asked for, on either side of the row's middle.
        Assert.Equal(new Avalonia.Thickness(1640, 0, 0, 0), SessionView.CentreAt(1720, 1800));
        Assert.Equal(new Avalonia.Thickness(0, 0, 2280, 0), SessionView.CentreAt(860, 4000));

        // A 21:9 monitor as the main one and a smaller one beside it.
        Matgate.Display.ScreenMonitor[] monitors = [new(0, 0, 3440, 1440, true), new(3440, 200, 1920, 1080, false)];
        Assert.Equal((0, 0, 5360, 1440), SessionWindow.SpanBounds(monitors));

        var view = new SessionView(connection);
        var window = new SessionWindow(view);
        window.Show();
        window.Span(monitors);
        Settle(window);
        Assert.True(view.IsFullScreen);
        Assert.Equal(5360 / window.RenderScaling, window.Width, 3);
        Assert.Equal(1440 / window.RenderScaling, window.Height, 3);
        Assert.Equal(1720 / window.RenderScaling, view.FullBarCentre!.Value, 3);
        Assert.Equal(1720 / window.RenderScaling, view.FullBarMiddle, 0);

        // One monitor: the system's full screen on it (it fills the monitor whatever its shape); the bar in the middle of the window.
        window.ToggleFullScreen();
        Settle(window);
        Assert.False(view.IsFullScreen);
        Assert.Null(view.FullBarCentre);
        window.Span([new(0, 0, 3440, 1440, true)]);
        Settle(window);
        Assert.Equal(WindowState.FullScreen, window.WindowState);
        Assert.Empty(window.Spanned);
        Assert.True(view.IsFullScreen);
        Assert.Null(view.FullBarCentre);
        window.CloseNow();
    }

    [AvaloniaFact]
    public void A_session_that_ended_offers_to_connect_again_in_the_middle_of_the_picture()
    {
        using var data = new TestData();
        SettingsService.Update(settings => settings with { ConfirmCloseSession = false });
        var mainWindow = Open();

        // Nobody listens on port 1: the session ends at once (signed in with stored test credentials, so nothing asks first).
        var credential = CredentialService.Save(new StoredCredential { Name = "Test", UserName = "demo" }, "demo-only-pw");
        var window = SessionWindow.Show(new RemoteConnection { Name = "Terminalserver", Protocol = ConnectionProtocol.Rdp, Host = "127.0.0.1", Port = 1, CredentialId = credential.Id });
        var view = window.View;
        for (var until = DateTime.UtcNow.AddSeconds(15); DateTime.UtcNow < until && !view.ReconnectButton.IsVisible;)
        {
            Settle(window);
        }

        Assert.True(view.ReconnectButton.IsVisible);
        Assert.Equal("Der Remote-Desktop ist nicht erreichbar.", view.MessageText);
        Assert.True(view.MessageCard.IsVisible);
        Assert.Contains(view.ReconnectButton, view.MessageCard.GetLogicalDescendants());
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, view.MessageCard.HorizontalAlignment);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, view.MessageCard.VerticalAlignment);
        Save(window, "session-ended");
        window.Close();
        mainWindow.Close();
    }

    [AvaloniaFact]
    public void Input_goes_to_the_engine_with_numbers_it_can_read()
    {
        // The engine reads JSON numbers as a browser sends them; a whole number put in from C# would read as 0 (keys and mouse lost).
        var message = SessionView.EngineJson(new JsonObject { ["t"] = "k", ["s"] = 0x1E, ["d"] = 1, ["x"] = 12L });
        Assert.True(message["s"]!.AsValue().TryGetValue<double>(out var scancode) && scancode == 0x1E);
        Assert.True(message["d"]!.AsValue().TryGetValue<double>(out var down) && down == 1);
        Assert.True(message["x"]!.AsValue().TryGetValue<double>(out var x) && x == 12);
        Assert.Equal("k", message["t"]!.GetValue<string>());
    }
}
