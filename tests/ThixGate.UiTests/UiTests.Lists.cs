using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;
using ThixGate.Pages;

namespace ThixGate.UiTests;

/// <summary>The lists: the right mouse button on a row offers the actions below the list for that row.</summary>
public sealed partial class UiTests
{
    [AvaloniaFact]
    public void The_right_mouse_button_on_a_row_offers_its_actions()
    {
        using var data = new TestData();
        SampleData.Create();
        var window = Open();
        window.ShowConnections();
        Settle(window);
        var page = window.ConnectionsPage;
        var list = page.List;
        list.SelectedIndex = 0;
        var row = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(2));
        var clicked = Assert.IsType<RemoteConnection>(row.DataContext);

        row.RaiseEvent(new ContextRequestedEventArgs());
        Settle(window);

        // The row under the pointer is chosen; the menu has the page's actions.
        Assert.Same(clicked, list.SelectedItem);
        var actions = page.GetLogicalDescendants().OfType<ActionBar>().First();
        var menu = Assert.IsType<MenuFlyout>(actions.LastMenu);
        var names = menu.Items.OfType<MenuItem>().Select(item => AutomationProperties.GetName(item)).ToList();
        Assert.Equal(["Verbinden", "Neue Verbindung", "Bearbeiten", "Duplizieren", "Importieren", "Exportieren", "Löschen"], names);
        Assert.Contains(menu.Items, item => item is Separator);

        // "Bearbeiten" opens that connection.
        menu.Items.OfType<MenuItem>().First(item => AutomationProperties.GetName(item) == "Bearbeiten").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Settle(window);
        Assert.Same(window.ConnectionForm, window.CurrentPage);
        Assert.Equal(clicked.Name, window.ConnectionForm.NameBox.Text);
        window.Close();
    }
}
