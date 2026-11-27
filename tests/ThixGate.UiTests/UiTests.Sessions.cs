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
