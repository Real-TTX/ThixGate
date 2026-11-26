using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using MatDesktop;
using ThixGate.Core.Business;
using ThixGate.Core.Models;
using ThixGate.Pages;
using ThixGate.Sessions;

namespace ThixGate.Controls;

/// <summary>
/// The library as a tree, the upper part of ThixGate's menu (<see cref="LibraryNav"/>): folders (open and closed like in Explorer, the number of
/// connections beside a folder) with their connections, the ones without a folder below them. A click on a folder shows its connections
/// (<see cref="FolderChosen"/>) and opens it, the chevron opens and closes it; a double click (or Enter) connects; the right mouse button has what
/// the item can do. Search above, "Neue Verbindung" and "Neuer Ordner" in its head (in the chosen folder), <see cref="Above"/> between the
/// search and the tree. The open folders are remembered (<see cref="SettingsService.Remember"/>). Several trees may exist (no static state).
/// </summary>
public sealed class ConnectionTree : UserControl
{
    private const double Indent = 16;

    private readonly TextBox _search = new() { Watermark = "Bibliothek durchsuchen" };
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Single };
    private readonly Button _newConnection = IconButton("plus", "Neue Verbindung");
    private readonly Button _newFolder = IconButton("folder", "Neuer Ordner");
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8), IsVisible = false };
    private readonly HashSet<Guid> _expanded;
    private IReadOnlyList<LibraryTreeRow> _rows = [];
    private string _matgateKey = "";

    public ConnectionTree()
    {
        _expanded = [.. SettingsService.Get().ExpandedFolders];
        _search.Themed("MatSearchBox");
        _empty.Themed("MatText.Muted");
        _list.Themed("MatList");
        _list.ItemContainerTheme = Ui.Theme("MatChoiceRow");
        _list.ContainerPrepared += (_, e) =>
        {
            // A tree row is lower than a choice and sits closer to the next one.
            if (e.Container is ListBoxItem item && item.Content is Control row && row.Tag is LibraryTreeRow data)
            {
                item.MinHeight = 30;
                item.Margin = new Thickness(0, 0, 0, 1);
                item.Padding = new Thickness(6, 0);
                item.ContextMenu = MenuOf(data);
                AutomationProperties.SetName(item, data switch
                {
                    { Matgate: not null } => $"Matgate {data.Name}, {data.Count} Verbindung(en)",
                    { IsFolder: true } => $"Ordner {data.Name}, {data.Count} Verbindung(en)",
                    _ => data.Name,
                });
            }
        };
        AutomationProperties.SetName(_list, "Bibliothek");
        _list.Tapped += (_, e) =>
        {
            if (RowAt(e.Source) is not { IsFolder: true } row || _list.ContainerFromIndex(IndexOf(row.Id)) is not ListBoxItem { Content: Control content })
            {
                return;
            }

            // The chevron opens and closes; the rest of the row of an own folder shows the folder (and opens it). A Matgate and its folders
            // open and close with a click anywhere.
            var onChevron = e.GetPosition(content).X < (row.Depth * Indent) + 18;
            if (onChevron || !row.IsExpanded || row.Folder is null)
            {
                Toggle(row);
            }

            if (!onChevron && row.Folder is { } folder)
            {
                FolderChosen?.Invoke(this, folder.Id);
            }
        };
        _list.DoubleTapped += async (_, e) =>
        {
            switch (RowAt(e.Source))
            {
                case { Connection: { } connection }:
                    await ConnectAsync(connection);
                    break;
                case { MatgateItem: { } item, MatgateAccountId: { } account }:
                    await ConnectAsync(account, item);
                    break;
            }
        };
        // Tunnel: the list itself would take Left / Right before a bubbling handler sees them.
        _list.AddHandler(KeyDownEvent, async (_, e) => await OnKeyAsync(e), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _search.TextChanged += (_, _) => Refresh();
        _newConnection.Click += (_, _) => Ui.MainOf(this)?.ShowConnectionForm(null, SelectedFolder);
        _newFolder.Click += (_, _) => Ui.MainOf(this)?.ShowFolderForm(null, SelectedFolder);

        var title = Ui.Text("Bibliothek", "MatText.SectionTitle");
        title.VerticalAlignment = VerticalAlignment.Center;
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(8, 0, 0, 10) };
        head.Children.Add(title);
        head.Children.Add(Ui.Cell(_newConnection, 0, 1));
        head.Children.Add(Ui.Cell(_newFolder, 0, 2));

        var top = new StackPanel { Children = { head, _search, Above, _empty }, Margin = new Thickness(0, 0, 0, 6) };
        var dock = new DockPanel();
        dock.Children.Add(Ui.Docked(top, Dock.Top));
        dock.Children.Add(_list);
        Content = dock;

        Ui.WhileShown(
            this,
            () =>
            {
                LibraryService.Changed += OnChanged;
                SettingsService.Changed += OnSettingsChanged;
            },
            () =>
            {
                LibraryService.Changed -= OnChanged;
                SettingsService.Changed -= OnSettingsChanged;
            },
            Refresh);
    }

    /// <summary>A folder was clicked: show its connections.</summary>
    public event EventHandler<Guid>? FolderChosen;

    /// <summary>What stands between the search and the tree ("Alle Verbindungen" of the menu).</summary>
    public ContentControl Above { get; } = new() { Margin = new Thickness(0, 10, 0, 4), HorizontalContentAlignment = HorizontalAlignment.Stretch };

    public TextBox SearchBox => _search;

    public ListBox List => _list;

    public Button NewConnectionButton => _newConnection;

    public Button NewFolderButton => _newFolder;

    /// <summary>The rows that show now, in their order.</summary>
    public IReadOnlyList<LibraryTreeRow> Rows => _rows;

    /// <summary>The chosen item (a folder or a connection), or null.</summary>
    public LibraryTreeRow? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _rows.Count ? _rows[_list.SelectedIndex] : null;

    /// <summary>The chosen folder, or the folder of the chosen connection: where something new goes.</summary>
    private Guid? SelectedFolder => Selected switch
    {
        { Folder: { } folder } => folder.Id,
        { Connection: { } connection } => connection.FolderId,
        _ => null,
    };

    /// <summary>Opens or closes a folder.</summary>
    public void Toggle(LibraryTreeRow row)
    {
        if (!row.IsFolder || !row.HasChildren || _search.Text?.Trim().Length > 0)
        {
            return;
        }

        if (!_expanded.Remove(row.Id))
        {
            _expanded.Add(row.Id);
        }

        RememberExpanded();
        Refresh(row.Id);
    }

    /// <summary>Opens the folders above a connection and chooses it (a form of it was opened).</summary>
    public void Reveal(Guid connectionId)
    {
        if (LibraryService.FindConnection(connectionId) is not { } connection)
        {
            return;
        }

        if (LibraryService.FolderChain(connection.FolderId).Count(_expanded.Add) > 0)
        {
            RememberExpanded();
        }

        Refresh(connectionId);
    }

    public void Refresh() => Refresh(Selected?.Id);

    /// <summary>Nothing chosen in the tree ("Alle Verbindungen" or another page is the current one).</summary>
    public void ClearSelection() => _list.SelectedIndex = -1;

    /// <summary>Chooses a folder (opened from elsewhere, e.g. "Verbindungen zeigen" of the folder page).</summary>
    public void Choose(Guid folderId)
    {
        if (LibraryService.FolderChain(LibraryService.FindFolder(folderId)?.ParentId).Count(_expanded.Add) > 0)
        {
            RememberExpanded();
        }

        Refresh(folderId);
    }

    private int IndexOf(Guid id) => _rows.ToList().FindIndex(row => row.Id == id);

    private void Refresh(Guid? select)
    {
        var search = _search.Text ?? "";
        _rows = LibraryService.Tree(search, _expanded);
        _list.ItemsSource = _rows.Select(Row).ToList();
        var index = select is { } id ? _rows.ToList().FindIndex(row => row.Id == id) : -1;
        _list.SelectedIndex = index;
        if (index >= 0)
        {
            _list.ScrollIntoView(index);
        }

        var locked = AccessPolicy.IsConfigurationLocked;
        _newConnection.IsVisible = !locked;
        _newFolder.IsVisible = !locked;
        _empty.IsVisible = _rows.Count == 0;
        _empty.Text = search.Trim().Length > 0 ? "Nichts gefunden." : "Noch keine Verbindungen. Lege mit + die erste an.";
    }

    private void OnChanged(object? sender, EventArgs e) => Ui.Post(Refresh);

    /// <summary>A Matgate signed in or out, renamed or its library read again: the tree follows (not for every remembered window size).</summary>
    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        var key = string.Join("|", settings.MatgateAccounts.Select(account => $"{account.Id}:{account.Name}:{account.IsSignedIn}:{account.LibraryFetched:O}"));
        if (key != _matgateKey)
        {
            _matgateKey = key;
            Ui.Post(Refresh);
        }
    }

    private void RememberExpanded()
    {
        SettingsService.Remember(current => current with { ExpandedFolders = [.. _expanded] });
    }

    private LibraryTreeRow? RowAt(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem { Content: Control { Tag: LibraryTreeRow row } })
            {
                return row;
            }

            if (visual == _list)
            {
                return null;
            }
        }

        return null;
    }

    private async Task OnKeyAsync(KeyEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Right when row is { IsFolder: true, HasChildren: true, IsExpanded: false }:
            case Key.Left when row is { IsFolder: true, IsExpanded: true }:
                e.Handled = true;
                Toggle(row);
                break;
            case Key.Left when row.Depth > 0:
                // To the folder above.
                e.Handled = true;
                var index = _list.SelectedIndex;
                while (--index >= 0 && _rows[index].Depth >= row.Depth)
                {
                }

                _list.SelectedIndex = Math.Max(0, index);
                (_list.ContainerFromIndex(_list.SelectedIndex) as ListBoxItem)?.Focus(NavigationMethod.Directional);
                break;
            case Key.Enter or Key.Space when row.IsFolder:
                e.Handled = true;
                Toggle(row);
                break;
            case Key.Enter when row.Connection is { } connection:
                e.Handled = true;
                await ConnectAsync(connection);
                break;
            case Key.Enter when row is { MatgateItem: { } item, MatgateAccountId: { } account }:
                e.Handled = true;
                await ConnectAsync(account, item);
                break;
            case Key.F2 when !AccessPolicy.IsConfigurationLocked && (!row.IsFromMatgate || row.Matgate is not null || row.MatgateItem is { CanEdit: true }):
                e.Handled = true;
                Edit(row);
                break;
        }
    }

    private void Edit(LibraryTreeRow row)
    {
        var main = Ui.MainOf(this);
        switch (row)
        {
            case { Folder: { } folder }:
                main?.ShowFolderForm(folder.Id);
                break;
            case { Connection: { } connection }:
                main?.ShowConnectionForm(connection.Id);
                break;
            case { Matgate: { } account }:
                main?.ShowMatgateForm(account.Id);
                break;
            case { MatgateItem: { } item, MatgateAccountId: { } accountId }:
                main?.ShowMatgateConnectionForm(accountId, item.Id);
                break;
        }
    }

    private async Task ConnectAsync(RemoteConnection connection)
    {
        try
        {
            await SessionLauncher.OpenAsync(connection, Ui.WindowOf(this));
        }
        catch (BusinessException exception)
        {
            Ui.MainOf(this)?.ShowConnections();
            Ui.MainOf(this)?.ConnectionsPage.ShowMessage(exception.Message, InfoSeverity.Error);
        }
    }

    /// <summary>A connection of a Matgate, straight from its library (the own one taken over from it, if there is one).</summary>
    private async Task ConnectAsync(Guid accountId, MatgateConnectionItem item)
    {
        try
        {
            await SessionLauncher.OpenAsync(MatgateService.TakenOver(accountId, item.Id) ?? MatgateService.AsConnection(accountId, item), Ui.WindowOf(this));
        }
        catch (BusinessException exception)
        {
            Show(exception.Message, InfoSeverity.Error);
        }
    }

    private async Task RefreshMatgateAsync(Guid accountId)
    {
        try
        {
            await MatgateService.RefreshLibraryAsync(accountId);
        }
        catch (BusinessException exception)
        {
            Show(exception.Message, InfoSeverity.Error);
        }
    }

    /// <summary>What the right mouse button offers for a folder or a connection, of the own library or of a Matgate.</summary>
    private ContextMenu MenuOf(LibraryTreeRow row)
    {
        var locked = AccessPolicy.IsConfigurationLocked;
        var main = () => Ui.MainOf(this);
        var items = new List<Control>();
        if (row.Folder is { } folder)
        {
            items.Add(Item("monitor", "Verbindungen zeigen", () => FolderChosen?.Invoke(this, folder.Id)));
            if (!locked)
            {
                items.Add(Item("plus", "Neue Verbindung hier", () => main()?.ShowConnectionForm(null, folder.Id)));
                items.Add(Item("folder", "Neuer Unterordner", () => main()?.ShowFolderForm(null, folder.Id)));
                items.Add(new Separator());
                items.Add(Item("edit", "Ordner bearbeiten", () => main()?.ShowFolderForm(folder.Id)));
                items.Add(Item("trash", "Ordner löschen …", async () => await FolderForm.DeleteAsync(this, folder, Show)));
            }
        }
        else if (row.Connection is { } connection)
        {
            items.Add(Item("play", "Verbinden", async () => await ConnectAsync(connection)));
            if (!locked)
            {
                items.Add(Item("edit", "Bearbeiten", () => main()?.ShowConnectionForm(connection.Id)));
                items.Add(Item("copy", "Duplizieren", () => Run(() => main()?.ShowConnectionForm(LibraryService.DuplicateConnection(connection.Id).Id))));
                items.Add(Item("star", connection.IsFavorite ? "Aus den Favoriten nehmen" : "Zu den Favoriten",
                    () => Run(() => LibraryService.SetFavorite(connection.Id, !connection.IsFavorite))));
                items.Add(new Separator());
                items.Add(Item("trash", "Löschen …", async () => await ConnectionForm.DeleteAsync(this, connection, Show)));
            }
        }

        else if (row.Matgate is { } account)
        {
            items.Add(Item("refresh", "Aktualisieren", async () => await RefreshMatgateAsync(account.Id)));
            if (MatgateService.Library(account.Id)?.User is { CanCreate: true })
            {
                items.Add(Item("plus", "Neu in Matgate …", () => main()?.ShowMatgateConnectionForm(account.Id, null)));
            }

            items.Add(new Separator());
            items.Add(Item("server", "Matgate öffnen", () => main()?.ShowMatgateForm(account.Id)));
        }
        else if (row is { MatgateItem: { } item, MatgateAccountId: { } accountId })
        {
            items.Add(Item(item.IsWatchOnly ? "eye" : "play", item.IsWatchOnly ? "Ansehen" : "Verbinden", async () => await ConnectAsync(accountId, item)));
            if (item.CanEdit)
            {
                items.Add(Item("edit", "In Matgate bearbeiten …", () => main()?.ShowMatgateConnectionForm(accountId, item.Id)));
            }

            if (!locked && !item.IsWatchOnly && MatgateService.TakenOver(accountId, item.Id) is null)
            {
                items.Add(Item("download", "In die eigene Bibliothek übernehmen", () => Run(() => MatgateService.TakeOver(accountId, item.Id))));
            }
        }
        else if (row.MatgateAccountId is { } folderAccount)
        {
            items.Add(Item("refresh", "Aktualisieren", async () => await RefreshMatgateAsync(folderAccount)));
        }

        return new ContextMenu { ItemsSource = items };
    }

    private void Show(string text, InfoSeverity severity)
    {
        Ui.MainOf(this)?.ShowConnections();
        Ui.MainOf(this)?.ConnectionsPage.ShowMessage(text, severity);
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (BusinessException exception)
        {
            Show(exception.Message, InfoSeverity.Error);
        }
    }

    private static MenuItem Item(string icon, string text, Action click)
    {
        var item = new MenuItem { Header = text, Icon = new MatIcon { Icon = icon, Size = 16 } };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>A row: the indent of its depth, the chevron of a folder, its icon, the name and (folder) the number or (connection) the star.</summary>
    private Control Row(LibraryTreeRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"), Tag = row };
        grid.Children.Add(new Border { Width = row.Depth * Indent });
        if (row.IsFolder && row.HasChildren)
        {
            grid.Children.Add(Ui.Cell(new MatIcon { Icon = row.IsExpanded ? "chevron-down" : "chevron-right", Size = 14, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center }, 0, 1));
        }
        else
        {
            grid.Children.Add(Ui.Cell(new Border { Width = 18 }, 0, 1));
        }

        var icon = row switch
        {
            { Matgate: not null } => "server",
            { IsFolder: true } => row.IsExpanded ? "folder-open" : "folder",
            { Connection: { } own } => Protocols.Of(own.Protocol).Icon,
            { MatgateItem.ProtocolKind: { } protocol } => Protocols.Of(protocol).Icon,
            _ => "monitor",
        };
        grid.Children.Add(Ui.Cell(new MatIcon { Icon = icon, Size = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }, 0, 2));
        grid.Children.Add(Ui.Cell(new TextBlock { Text = row.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 0, 3));

        if (row.IsFolder)
        {
            var count = Ui.Text(row.Count.ToString(System.Globalization.CultureInfo.CurrentCulture), "MatText.Faint");
            count.VerticalAlignment = VerticalAlignment.Center;
            count.Margin = new Thickness(8, 0, 2, 0);
            grid.Children.Add(Ui.Cell(count, 0, 4));
        }
        else if (row.Connection is { IsFavorite: true })
        {
            grid.Children.Add(Ui.Cell(new MatIcon { Icon = "star", Size = 12, IsFilled = true, Margin = new Thickness(8, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center }, 0, 4));
        }

        else if (row.MatgateItem is { IsWatchOnly: true })
        {
            grid.Children.Add(Ui.Cell(new MatIcon { Icon = "eye", Size = 14, Margin = new Thickness(8, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center }, 0, 4));
        }

        if (row.Connection is { } connection)
        {
            ToolTip.SetTip(grid, string.Join("\n", new[] { Protocols.Address(connection), connection.Notes }.Where(text => text.Length > 0)));
        }
        else if (row.MatgateItem is { } item)
        {
            var address = item.Port > 0 ? $"{item.Host}:{item.Port}" : item.Host;
            var how = item switch
            {
                { IsWatchOnly: true } => "Nur ansehen – als Sitzung auf Matgate",
                { HasStoredLogin: true } => "Matgate meldet sich an – als Sitzung auf Matgate",
                _ => "Über Matgate",
            };
            ToolTip.SetTip(grid, string.Join("\n", new[] { address, how, item.Notes }.Where(text => text.Length > 0)));
        }
        else if (row.Matgate is { } account)
        {
            ToolTip.SetTip(grid, $"{account.ServerUrl}\nangemeldet als {(account.DisplayName.Length > 0 ? account.DisplayName : account.UserName)}");
        }

        return grid;
    }

    private static Button IconButton(string icon, string name)
    {
        var button = new Button { Padding = new Thickness(6), Content = new MatIcon { Icon = icon, Size = 16 } }.Themed("MatButton.Icon");
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        return button;
    }
}
