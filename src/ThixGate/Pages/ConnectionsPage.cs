using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;
using ThixGate.Dialogs;
using ThixGate.Sessions;

namespace ThixGate.Pages;

/// <summary>
/// "Verbindungen" (the start page): every connection as a card row - protocol tile, name, protocol badge, address, user, folder, a star
/// for favourites and "Verbinden". Toolbar: search, protocol, folder, favourites, sort + direction. Below: Verbinden, Neue Verbindung,
/// Bearbeiten, Duplizieren, Importieren, Exportieren … Löschen; the pager at the right.
/// </summary>
public sealed class ConnectionsPage : UserControl
{
    private const int PageSize = 50;
    private static readonly ConnectionSort[] Sorts = [ConnectionSort.Name, ConnectionSort.LastUsed, ConnectionSort.Host, ConnectionSort.Protocol];

    private readonly ListLayout<RemoteConnection> _layout;
    private readonly ComboBox _protocol;
    private readonly ComboBox _folder;
    private readonly ComboBox _sort;
    private readonly CheckBox _favorites;
    private readonly ToggleButton _descending = new() { Padding = new Thickness(8, 4) };
    private readonly Button _connect = Ui.Button("play", "Verbinden", "MatButton.Primary");
    private readonly Button _new = Ui.Button("plus", "Neue Verbindung");
    private readonly Button _edit = Ui.Button("edit", "Bearbeiten");
    private readonly Button _duplicate = Ui.Button("copy", "Duplizieren");
    private readonly Button _import = Ui.Button("download", "Importieren");
    private readonly Button _export = Ui.Button("upload", "Exportieren");
    private readonly Button _delete = Ui.Button("trash", "Löschen", "MatButton.Danger");
    private IReadOnlyList<RemoteFolder> _folders = [];
    private int _page;

    public ConnectionsPage()
    {
        _layout = new ListLayout<RemoteConnection>(
            "Verbindungen",
            "Deine Remote-Verbindungen: Remote Desktop, VNC und SSH öffnen als Tab in ThixGate. Ein Doppelklick verbindet.",
            "Name, Adresse, Benutzer, Notiz oder Schlagwort suchen",
            "Verbindungen",
            Row);
        _protocol = _layout.Toolbar.AddChoice("Protokoll", ["Alle Protokolle", "Remote Desktop (RDP)", "VNC", "SSH"], 190);
        _folder = _layout.Toolbar.AddChoice("Ordner", ["Alle Ordner"], 190);
        _sort = _layout.Toolbar.AddChoice("Sortierung", ["Nach Name", "Zuletzt verwendet", "Nach Adresse", "Nach Protokoll"], 180);
        _descending.Content = new MatIcon { Icon = "sort", Size = 16 };
        AutomationProperties.SetName(_descending, "Absteigend sortieren");
        ToolTip.SetTip(_descending, "Reihenfolge umkehren");
        _descending.IsCheckedChanged += (_, _) => Refresh();
        _layout.Toolbar.AddItem(_descending);
        _favorites = _layout.Toolbar.AddSwitch("Nur Favoriten");
        _layout.Toolbar.Changed += (_, _) =>
        {
            _page = 0;
            Refresh();
        };
        _layout.PageChanged += (_, page) =>
        {
            _page = page;
            Refresh();
        };

        _layout.Actions.Children.AddRange([_connect, _new, _edit, _duplicate, _import, _export, _delete]);
        ActionBar.SetSeparated(_delete, true);
        _layout.SelectionChanged += (_, _) => ApplyButtons();
        _layout.ItemActivated += async (_, connection) => await ConnectAsync(connection);
        _connect.Click += async (_, _) =>
        {
            if (_layout.SelectedItem is { } connection)
            {
                await ConnectAsync(connection);
            }
        };
        _new.Click += (_, _) => Ui.MainOf(this)?.ShowConnectionForm(null, FolderFilter);
        _edit.Click += (_, _) => Edit();
        _duplicate.Click += (_, _) => Duplicate();
        _import.Click += async (_, _) => await ImportAsync();
        _export.Click += async (_, _) => await ExportAsync();
        _delete.Click += async (_, _) =>
        {
            if (_layout.SelectedItem is { } connection)
            {
                await ConnectionForm.DeleteAsync(this, connection, _layout.Message.Show);
            }
        };

        Content = _layout;
        Ui.WhileShown(this, () => LibraryService.Changed += OnChanged, () => LibraryService.Changed -= OnChanged, Refresh);
    }

    public ListBox List => _layout.List;

    public TextBox SearchBox => _layout.Toolbar.SearchBox;

    public ComboBox ProtocolChoice => _protocol;

    public InfoBar Message => _layout.Message;

    public Button ConnectButton => _connect;

    public void ShowMessage(string text, InfoSeverity severity) => _layout.Message.Show(text, severity);

    /// <summary>Filters the list to one folder ("Verbindungen zeigen" of the folder page).</summary>
    public void ShowFolder(Guid folderId)
    {
        RefreshFolders();
        _folder.SelectedIndex = Math.Max(0, _folders.ToList().FindIndex(folder => folder.Id == folderId) + 1);
        _page = 0;
        Refresh();
    }

    /// <summary>Every connection again ("Alle Verbindungen" of the menu): no folder in the filter.</summary>
    public void ShowAll()
    {
        if (_folder.SelectedIndex > 0)
        {
            _folder.SelectedIndex = 0;
            _page = 0;
            Refresh();
        }
    }

    /// <summary>The folder chosen in the toolbar (a new connection goes there).</summary>
    private Guid? FolderFilter => _folder.SelectedIndex > 0 && _folder.SelectedIndex <= _folders.Count ? _folders[_folder.SelectedIndex - 1].Id : null;

    public void Refresh()
    {
        var locked = AccessPolicy.IsConfigurationLocked;
        _layout.ShowPolicy(locked ? ListLayout<RemoteConnection>.LockedNote("Verbindungen") : null);
        RefreshFolders();

        var query = new ConnectionQuery
        {
            Search = _layout.Toolbar.SearchText,
            Protocol = _protocol.SelectedIndex > 0 ? (ConnectionProtocol)(_protocol.SelectedIndex - 1) : null,
            FolderId = FolderFilter,
            FavoritesOnly = _favorites.IsChecked == true,
            Sort = Sorts[Math.Max(0, _sort.SelectedIndex)],
            Descending = _descending.IsChecked == true,
            Page = _page + 1,
            PageSize = PageSize,
        };
        var result = LibraryService.Query(query);
        _page = result.Page - 1;
        var all = LibraryService.Connections().Count;
        var count = result.Total == all ? (all == 1 ? "1 Verbindung" : $"{all} Verbindungen") : $"{result.Total} von {all} Verbindungen";
        var empty = all == 0
            ? new EmptyState("monitor", "Noch keine Verbindung", "Lege deine erste Verbindung an – oder importiere eine RDP-Datei bzw. eine Bibliothek von ThixGate.",
                locked ? null : NewButton())
            : new EmptyState("search", "Nichts gefunden", "Keine Verbindung passt zu Suche und Filter.");
        _layout.Show(result.Items, connection => connection.Id, count, empty, _page, result.PageCount, result.Total, PageSize);
        ApplyButtons();
    }

    private Button NewButton()
    {
        var button = Ui.Button("plus", "Neue Verbindung", "MatButton.Primary");
        button.Click += (_, _) => Ui.MainOf(this)?.ShowConnectionForm(null, FolderFilter);
        return button;
    }

    private void RefreshFolders()
    {
        var chosen = FolderFilter;
        _folders = [.. LibraryService.Folders().OrderBy(folder => LibraryService.FolderPath(folder.Id), StringComparer.CurrentCultureIgnoreCase)];
        var names = new List<string> { "Alle Ordner" };
        names.AddRange(_folders.Select(folder => LibraryService.FolderPath(folder.Id)));
        if (_folder.ItemsSource is IReadOnlyList<string> current && current.SequenceEqual(names))
        {
            return;
        }

        _folder.ItemsSource = names;
        var index = chosen is { } id ? _folders.ToList().FindIndex(folder => folder.Id == id) + 1 : 0;
        _folder.SelectedIndex = Math.Max(0, index);
    }

    private void OnChanged(object? sender, EventArgs e) => Ui.Post(Refresh);

    private void ApplyButtons()
    {
        var connection = _layout.SelectedItem;
        var locked = AccessPolicy.IsConfigurationLocked;
        _connect.IsEnabled = connection is not null;
        _new.IsEnabled = _import.IsEnabled = !locked;
        _edit.IsEnabled = connection is not null;
        _duplicate.IsEnabled = _delete.IsEnabled = connection is not null && !locked;
    }

    private void Edit()
    {
        if (_layout.SelectedItem is { } connection)
        {
            Ui.MainOf(this)?.ShowConnectionForm(connection.Id);
        }
    }

    private void Duplicate()
    {
        if (_layout.SelectedItem is not { } connection)
        {
            return;
        }

        try
        {
            var copy = LibraryService.DuplicateConnection(connection.Id);
            Ui.MainOf(this)?.ShowConnectionForm(copy.Id);
        }
        catch (BusinessException exception)
        {
            _layout.Message.Show(exception.Message, InfoSeverity.Error);
        }
    }

    /// <summary>Opens the session of a connection (RDP/VNC: a session window; SSH: the terminal).</summary>
    private async Task ConnectAsync(RemoteConnection connection)
    {
        try
        {
            await SessionLauncher.OpenAsync(connection, Ui.WindowOf(this));
        }
        catch (BusinessException exception)
        {
            _layout.Message.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private async Task ImportAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Verbindungen importieren",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("ThixGate und Remote Desktop") { Patterns = ["*.thixgate.json", "*.rdp"] },
                new FilePickerFileType("Alle Dateien") { Patterns = ["*"] },
            ],
        });
        var results = new List<string>();
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is not { } path)
            {
                continue;
            }

            try
            {
                results.Add(TransferService.Import(path).Summary);
            }
            catch (BusinessException exception)
            {
                _layout.Message.Show($"{Path.GetFileName(path)}: {exception.Message}", InfoSeverity.Error);
                return;
            }
        }

        if (results.Count > 0)
        {
            _layout.Message.Show(string.Join(" ", results), InfoSeverity.Success);
        }
    }

    private async Task ExportAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Bibliothek exportieren",
            SuggestedFileName = "Verbindungen" + TransferService.FileExtension,
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("ThixGate-Bibliothek") { Patterns = ["*.thixgate.json"] }],
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            TransferService.Export(path);
            _layout.Message.Show($"Die Bibliothek ist in {Path.GetFileName(path)} gespeichert – ohne Passwörter.", InfoSeverity.Success);
        }
        catch (IOException exception)
        {
            _layout.Message.Show($"Die Datei konnte nicht geschrieben werden: {exception.Message}", InfoSeverity.Error);
        }
    }

    private Control Row(RemoteConnection connection)
    {
        var (protocolName, icon) = Protocols.Of(connection.Protocol);
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        title.Children.Add(new TextBlock { Text = connection.Name, FontWeight = FontWeight.SemiBold, FontSize = MatTokens.Type.FsLg, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new Border { Child = new TextBlock { Text = protocolName } }.Themed("MatBadge.Soft"));
        if (connection.Via == ConnectVia.Matgate)
        {
            title.Children.Add(new Border { Child = new TextBlock { Text = "über Matgate" } }.Themed("MatBadge.Soft"));
        }

        var details = new List<string> { Protocols.Address(connection) };
        var login = SessionTargets.LoginOf(connection);
        if (login.UserName.Length > 0)
        {
            details.Add(login.Domain.Length > 0 ? $"{login.Domain}\\{login.UserName}" : login.UserName);
        }

        if (LibraryService.FolderPath(connection.FolderId) is { Length: > 0 } folder)
        {
            details.Add(folder);
        }

        if (connection.LastUsed is { } used)
        {
            details.Add($"zuletzt {Formats.When(used)}");
        }

        var text = new StackPanel
        {
            Margin = new Thickness(16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 3,
            Children =
            {
                title,
                new TextBlock { Text = string.Join(" · ", details), TextTrimming = TextTrimming.CharacterEllipsis }.Themed("MatText.Muted"),
            },
        };

        var star = new Button { Padding = new Thickness(6), Content = new MatIcon { Icon = "star", Size = 18, IsFilled = connection.IsFavorite } }.Themed("MatButton.Ghost");
        AutomationProperties.SetName(star, connection.IsFavorite ? "Aus den Favoriten nehmen" : "Zu den Favoriten");
        ToolTip.SetTip(star, connection.IsFavorite ? "Favorit – zum Entfernen klicken" : "Als Favorit markieren");
        star.IsEnabled = !AccessPolicy.IsConfigurationLocked;
        star.Click += (_, e) =>
        {
            e.Handled = true;
            try
            {
                LibraryService.SetFavorite(connection.Id, !connection.IsFavorite);
            }
            catch (BusinessException exception)
            {
                _layout.Message.Show(exception.Message, InfoSeverity.Error);
            }
        };

        var connect = Ui.Button("play", "Verbinden");
        AutomationProperties.SetName(connect, $"{connection.Name} verbinden");
        connect.Click += async (_, e) =>
        {
            e.Handled = true;
            await ConnectAsync(connection);
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), MinHeight = 52 };
        row.Children.Add(Ui.IconTile(icon, 44, 22));
        row.Children.Add(Ui.Cell(text, 0, 1));
        row.Children.Add(Ui.Cell(star, 0, 2));
        row.Children.Add(Ui.Cell(new Border { Margin = new Thickness(8, 0, 0, 0), Child = connect }, 0, 3));
        ToolTip.SetTip(row, connection.Notes.Length > 0 ? connection.Notes : null);
        return row;
    }
}
