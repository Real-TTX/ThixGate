using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;
using ThixGate.Dialogs;

namespace ThixGate.Pages;

/// <summary>
/// "Ordner": the folders of the library with their path and how many connections they hold. Toolbar: search, sort. Below: Neuer Ordner,
/// Bearbeiten, Verbindungen zeigen … Löschen (connections move to the folder above).
/// </summary>
public sealed class FoldersPage : UserControl
{
    private readonly ListLayout<RemoteFolder> _layout;
    private readonly ComboBox _sort;
    private readonly Button _new = Ui.Button("plus", "Neuer Ordner", "MatButton.Primary");
    private readonly Button _edit = Ui.Button("edit", "Bearbeiten");
    private readonly Button _show = Ui.Button("list", "Verbindungen zeigen");
    private readonly Button _delete = Ui.Button("trash", "Löschen", "MatButton.Danger");

    public FoldersPage()
    {
        _layout = new ListLayout<RemoteFolder>("Ordner",
            "Ordner ordnen die Bibliothek – zum Beispiel nach Kunde, Standort oder Umgebung. Ordner lassen sich ineinander legen.",
            "Ordner suchen", "Ordner", Row);
        _sort = _layout.Toolbar.AddChoice("Sortierung", ["Nach Pfad", "Nach Anzahl Verbindungen", "Zuletzt geändert"], 220);
        _layout.Toolbar.Changed += (_, _) => Refresh();
        _layout.Actions.Children.AddRange([_new, _edit, _show, _delete]);
        ActionBar.SetSeparated(_delete, true);
        _layout.SelectionChanged += (_, _) => ApplyButtons();
        _layout.ItemActivated += (_, folder) => Ui.MainOf(this)?.ShowFolderForm(folder.Id);
        _new.Click += (_, _) => Ui.MainOf(this)?.ShowFolderForm(null);
        _edit.Click += (_, _) =>
        {
            if (_layout.SelectedItem is { } folder)
            {
                Ui.MainOf(this)?.ShowFolderForm(folder.Id);
            }
        };
        _show.Click += (_, _) =>
        {
            if (_layout.SelectedItem is { } folder)
            {
                Ui.MainOf(this)?.ShowConnections(folder.Id);
            }
        };
        _delete.Click += async (_, _) =>
        {
            if (_layout.SelectedItem is { } folder)
            {
                await FolderForm.DeleteAsync(this, folder, _layout.Message.Show);
            }
        };
        Content = _layout;
        Ui.WhileShown(this, () => LibraryService.Changed += OnChanged, () => LibraryService.Changed -= OnChanged, Refresh);
    }

    public ListBox List => _layout.List;

    public void ShowMessage(string text, InfoSeverity severity) => _layout.Message.Show(text, severity);

    public void Refresh()
    {
        var locked = AccessPolicy.IsConfigurationLocked;
        _layout.ShowPolicy(locked ? ListLayout<RemoteFolder>.LockedNote("Ordner") : null);
        var search = _layout.Toolbar.SearchText;
        var all = LibraryService.Folders();
        IEnumerable<RemoteFolder> folders = all.Where(folder => search.Length == 0 || LibraryService.FolderPath(folder.Id).Contains(search, StringComparison.CurrentCultureIgnoreCase));
        folders = _sort.SelectedIndex switch
        {
            1 => folders.OrderByDescending(folder => LibraryService.CountIn(folder.Id)),
            2 => folders.OrderByDescending(folder => folder.UpdateDate),
            _ => folders.OrderBy(folder => LibraryService.FolderPath(folder.Id), StringComparer.CurrentCultureIgnoreCase),
        };
        var list = folders.ToList();
        var count = list.Count == all.Count ? (all.Count == 1 ? "1 Ordner" : $"{all.Count} Ordner") : $"{list.Count} von {all.Count} Ordnern";
        var empty = all.Count == 0
            ? new EmptyState("folder", "Noch keine Ordner", "Ordner halten die Bibliothek übersichtlich.", locked ? null : NewButton())
            : new EmptyState("search", "Nichts gefunden", "Kein Ordner passt zur Suche.");
        _layout.Show(list, folder => folder.Id, count, empty);
        ApplyButtons();
    }

    private Button NewButton()
    {
        var button = Ui.Button("plus", "Neuer Ordner", "MatButton.Primary");
        button.Click += (_, _) => Ui.MainOf(this)?.ShowFolderForm(null);
        return button;
    }

    private void OnChanged(object? sender, EventArgs e) => Ui.Post(Refresh);

    private void ApplyButtons()
    {
        var folder = _layout.SelectedItem;
        var locked = AccessPolicy.IsConfigurationLocked;
        _new.IsEnabled = !locked;
        _edit.IsEnabled = _show.IsEnabled = folder is not null;
        _delete.IsEnabled = folder is not null && !locked;
    }

    private static Control Row(RemoteFolder folder)
    {
        var count = LibraryService.CountIn(folder.Id);
        var text = new StackPanel
        {
            Margin = new Thickness(16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = folder.Name, FontWeight = FontWeight.SemiBold, FontSize = MatTokens.Type.FsLg, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = LibraryService.FolderPath(folder.Id), TextTrimming = TextTrimming.CharacterEllipsis }.Themed("MatText.Muted"),
            },
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinHeight = 52 };
        row.Children.Add(Ui.IconTile("folder", 44, 22));
        row.Children.Add(Ui.Cell(text, 0, 1));
        row.Children.Add(Ui.Cell(new Border { Child = new TextBlock { Text = count == 1 ? "1 Verbindung" : $"{count} Verbindungen" } }.Themed("MatBadge.Soft"), 0, 2));
        return row;
    }
}

/// <summary>Create / update a folder: name and the folder it lies in. Buttons: Speichern, Zurück … Löschen.</summary>
public sealed class FolderForm : UserControl
{
    private readonly EditLayout _layout = new();
    private readonly TextBox _name = new();
    private readonly ComboBox _parent = new() { MinWidth = 320 };
    private IReadOnlyList<RemoteFolder> _parents = [];
    private RemoteFolder? _existing;

    public FolderForm()
    {
        AutomationProperties.SetName(_name, "Name");
        var general = _layout.AddTab("Allgemein");
        EditLayout.ColumnOf(general).Children.AddRange(
        [
            _layout.Fields.Add(nameof(RemoteFolder.Name), "Name", _name, tab: general),
            _layout.Fields.Add(nameof(RemoteFolder.ParentId), "Liegt in", _parent, "Oben: der Ordner liegt in keinem anderen.", general),
        ]);
        _layout.Save.Click += (_, _) =>
        {
            RemoteFolder? saved = null;
            if (_layout.Run(() => saved = LibraryService.SaveFolder(Collect())))
            {
                Closed?.Invoke($"Der Ordner „{saved!.Name}“ wurde gespeichert.");
            }
        };
        _layout.Back.Click += (_, _) => Closed?.Invoke(null);
        _layout.Delete.Click += async (_, _) =>
        {
            if (_existing is { } folder && await DeleteAsync(this, folder, _layout.Message.Show))
            {
                Closed?.Invoke($"Der Ordner „{folder.Name}“ wurde gelöscht.");
            }
        };
        Content = _layout;
    }

    public Action<string?>? Closed { get; set; }

    public EditLayout Layout => _layout;

    public TextBox NameBox => _name;

    /// <summary>A folder (null: a new one, inside <paramref name="parentId"/> when given).</summary>
    public void Load(Guid? folderId, Guid? parentId = null)
    {
        _layout.Reset();
        _existing = folderId is { } id ? LibraryService.FindFolder(id) : null;
        var all = LibraryService.Folders();
        var excluded = _existing is null ? [] : LibraryService.FolderAndChildren(all, _existing.Id);
        _parents = [.. all.Where(folder => !excluded.Contains(folder.Id)).OrderBy(folder => LibraryService.FolderPath(folder.Id), StringComparer.CurrentCultureIgnoreCase)];
        _parent.ItemsSource = new[] { "Oben (kein Ordner)" }.Concat(_parents.Select(folder => LibraryService.FolderPath(folder.Id))).ToList();
        _name.Text = _existing?.Name ?? "";
        _parent.SelectedIndex = (_existing is null ? parentId : _existing.ParentId) is { } parent ? _parents.ToList().FindIndex(folder => folder.Id == parent) + 1 : 0;
        _layout.SetHead("folder", _existing?.Name ?? "Neuer Ordner",
            _existing is null ? "Ein Ordner der Bibliothek" : $"{LibraryService.CountIn(_existing.Id)} Verbindung(en) · {LibraryService.FolderPath(_existing.Id)}");
        _layout.Delete.IsVisible = _existing is not null;
        _layout.Save.IsEnabled = _layout.Delete.IsEnabled = !AccessPolicy.IsConfigurationLocked;
    }

    public static async Task<bool> DeleteAsync(Control owner, RemoteFolder folder, Action<string, InfoSeverity> show)
    {
        var count = LibraryService.CountIn(folder.Id);
        var message = count == 0
            ? $"Der Ordner „{folder.Name}“ wird gelöscht."
            : $"Der Ordner „{folder.Name}“ wird gelöscht. Seine {count} Verbindung(en) und Unterordner bleiben erhalten und rücken eine Ebene nach oben.";
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(owner), "Ordner löschen?", message, "Löschen", isDestructive: true))
        {
            return false;
        }

        try
        {
            LibraryService.DeleteFolder(folder.Id);
            show($"Der Ordner „{folder.Name}“ wurde gelöscht.", InfoSeverity.Success);
            return true;
        }
        catch (BusinessException exception)
        {
            show(exception.Message, InfoSeverity.Error);
            return false;
        }
    }

    private RemoteFolder Collect() => (_existing ?? new RemoteFolder()) with
    {
        Name = _name.Text ?? "",
        ParentId = _parent.SelectedIndex > 0 && _parent.SelectedIndex <= _parents.Count ? _parents[_parent.SelectedIndex - 1].Id : null,
    };
}
