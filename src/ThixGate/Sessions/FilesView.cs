using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Matgate.Files;
using Matgate.Models;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;
using ThixGate.Dialogs;

namespace ThixGate.Sessions;

/// <summary>
/// The file manager of a file connection (SFTP, FTP, SMB, WebDAV) in a tab - what Matgate's file manager does, with its code
/// (<see cref="FileSession"/>): navigate (path field, Hoch, Aktualisieren), Neu (Ordner, Datei), Hochladen (Dateien, a whole folder, an
/// archive unpacked on the way; or drop files and folders from Explorer / Finder), Herunterladen (files, folders, as ZIP), Öffnen with the
/// program of this computer, Umbenennen, Löschen, ZIP erstellen, Entpacken, Kopieren / Ausschneiden / Einfügen also between two file tabs,
/// Kopieren nach … another open file tab. Several rows can be chosen; keys as in Explorer (Entf, F2, Strg+C/X/V, Rücktaste, F5).
/// </summary>
public sealed class FilesView : UserControl, ISessionPanel
{
    private static readonly List<FilesView> Open = [];
    private static (FilesView Source, IReadOnlyList<string> Paths, bool Cut)? _clipboard;

    private readonly RemoteConnection _connection;
    private readonly TextBox _path = new() { MinWidth = 240 };
    private readonly DataTable<FileGatewayEntry> _table = new("Dateien");
    private readonly InfoBar _message = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _up = Ui.Button("arrow-up", "Hoch");
    private readonly Button _refresh = Ui.Button("refresh", "Aktualisieren");
    private readonly Button _new = Ui.Button("plus", "Neu");
    private readonly Button _upload = Ui.Button("upload", "Hochladen");
    private readonly Button _download = Ui.Button("download", "Herunterladen");
    private readonly Button _more = Ui.Button("more-horizontal", "Aktionen");
    private CancellationTokenSource _work = new();
    private FileSession? _session;
    private string _folder = "/";
    private bool _busy;

    public FilesView(RemoteConnection connection)
    {
        _connection = connection;
        this.Use(BackgroundProperty, "Mat.Surface");
        AutomationProperties.SetName(_path, "Pfad");
        _path.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await NavigateAsync(_path.Text ?? "/");
            }
        };
        _up.Click += async (_, _) => await NavigateAsync(RemoteFiles.ParentVirtualPath(_folder));
        _refresh.Click += async (_, _) => await NavigateAsync(_folder);
        _new.Flyout = Menu(("Ordner …", "folder", () => NewAsync(folder: true)), ("Datei …", "file", () => NewAsync(folder: false)));
        _upload.Flyout = Menu(("Dateien …", "file", UploadFilesAsync), ("Ordner …", "folder", UploadFolderAsync), ("Archiv hochladen und entpacken …", "archive", UploadArchiveAsync));
        _download.Click += async (_, _) => await DownloadAsync();
        _more.Flyout = new MenuFlyout();
        ((MenuFlyout)_more.Flyout).Opening += (_, _) => ((MenuFlyout)_more.Flyout).ItemsSource = ActionItems();

        _table.List.SelectionMode = SelectionMode.Multiple;
        _table.EmptyText = "Dieser Ordner ist leer.";
        _table.AddColumn("Name", "3*", entry => NameCell(entry))
            .AddColumn("Größe", "110", entry => DataTable<FileGatewayEntry>.TextCell(entry.IsDirectory ? "" : Size(entry.Size), "Mat.TextMuted"), alignRight: true)
            .AddColumn("Geändert", "160", entry => DataTable<FileGatewayEntry>.TextCell(entry.ModifiedAt is { } at ? at.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture) : "", "Mat.TextMuted"));
        _table.ItemActivated += async (_, entry) => await OpenAsync(entry);
        _table.SelectionChanged += (_, _) => ShowStatus();
        _table.List.ContextRequested += (_, e) =>
        {
            var menu = new ContextMenu { ItemsSource = ActionItems() };
            menu.Open(_table.List);
            e.Handled = true;
        };
        AddHandler(KeyDownEvent, async (_, e) => await KeyAsync(e), Avalonia.Interactivity.RoutingStrategies.Tunnel);

        DragDrop.SetAllowDrop(_table, true);
        _table.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) && _session is not null ? DragDropEffects.Copy : DragDropEffects.None);
        _table.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (e.DataTransfer.TryGetFiles() is { } items)
            {
                await UploadLocalAsync([.. items.Select(item => item.TryGetLocalPath()).OfType<string>()]);
            }
        });

        var tools = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        tools.Children.AddRange([_up, _refresh, new Border { Width = 4 }, _new, _upload, _download, _more]);
        var head = new DockPanel { Margin = new Thickness(16, 12, 16, 8) };
        head.Children.Add(Ui.Docked(tools, Dock.Right));
        head.Children.Add(new Border { Margin = new Thickness(0, 0, 12, 0), Child = _path });
        _status.Themed("MatText.Muted");
        var foot = new Border { Padding = new Thickness(16, 6), Child = _status };
        var root = new DockPanel();
        root.Children.Add(Ui.Docked(head, Dock.Top));
        root.Children.Add(Ui.Docked(new Border { Margin = new Thickness(16, 0, 16, 8), Child = _message }, Dock.Top));
        root.Children.Add(Ui.Docked(foot, Dock.Bottom));
        root.Children.Add(new Border { Margin = new Thickness(16, 0), Child = _table });
        Content = root;
        IsEnabled = false;
    }

    public Control View => this;

    /// <summary>The folder shown (tests).</summary>
    public string Folder => _folder;

    /// <summary>The rows shown (tests).</summary>
    public IReadOnlyList<FileGatewayEntry> Entries => _table.List.ItemsSource as IReadOnlyList<FileGatewayEntry> ?? [];

    /// <summary>The session (tests).</summary>
    internal FileSession? Session => _session;

    public DataTable<FileGatewayEntry> Table => _table;

    // ================================================================= life cycle

    public async Task ConnectAsync(AskSignIn ask, CancellationToken cancellationToken)
    {
        var session = await FileSession.OpenAsync(_connection, ask, cancellationToken);
        var listing = await session.ListAsync("/", cancellationToken);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            _session = session;
            _work = new CancellationTokenSource();
            lock (Open)
            {
                Open.Add(this);
            }

            IsEnabled = true;
            Show(listing);
        });
    }

    public void Disconnect()
    {
        _work.Cancel();
        _session = null;
        IsEnabled = false;
        lock (Open)
        {
            Open.Remove(this);
        }

        if (_clipboard?.Source == this)
        {
            _clipboard = null;
        }
    }

    public void FocusPanel() => _table.List.Focus();

    // ================================================================= showing

    /// <summary>Shows a folder ("/" the top).</summary>
    public async Task NavigateAsync(string folder)
    {
        await RunAsync(null, async (session, token) => Show(await session.ListAsync(RemoteFiles.NormalizeVirtualPath(folder), token)), refresh: false);
    }

    private void Show(FileGatewayListResult listing)
    {
        _folder = listing.Path;
        _path.Text = listing.Path;
        _up.IsEnabled = listing.Path != "/";
        _table.SetItems(listing.Entries, entry => entry.Path, selectFirst: false);
        ShowStatus();
    }

    private void ShowStatus()
    {
        if (_busy)
        {
            return;
        }

        var entries = Entries;
        var chosen = Selected();
        var folders = entries.Count(entry => entry.IsDirectory);
        _status.Text = chosen.Count > 0
            ? $"{chosen.Count} ausgewählt · {Size(chosen.Where(entry => !entry.IsDirectory).Sum(entry => entry.Size ?? 0))}"
            : $"{folders} Ordner, {entries.Count - folders} Dateien";
    }

    private Control NameCell(FileGatewayEntry entry)
    {
        var icon = entry.IsDirectory ? "folder" : FileOperations.IsArchive(entry.Name) ? "archive" : "file";
        var label = new IconLabel { Icon = icon, Text = entry.Name };
        ToolTip.SetTip(label, entry.Path);
        return label;
    }

    private IReadOnlyList<FileGatewayEntry> Selected() => [.. _table.List.SelectedItems?.OfType<FileGatewayEntry>() ?? []];

    // ================================================================= actions

    private IEnumerable<Control> ActionItems()
    {
        var chosen = Selected();
        var one = chosen.Count == 1 ? chosen[0] : null;
        var items = new List<Control>();
        void Add(string header, string icon, Func<Task> action, bool enabled = true, string? gesture = null)
        {
            var item = new MenuItem { Header = header, Icon = new IconLabel { Icon = icon }, IsEnabled = enabled && _session is not null };
            if (gesture is not null)
            {
                item.InputGesture = KeyGesture.Parse(gesture);
            }

            item.Click += async (_, _) => await action();
            items.Add(item);
        }

        var command = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";
        Add("Öffnen", "external", () => one is null ? Task.CompletedTask : OpenAsync(one), one is not null);
        Add("Herunterladen …", "download", DownloadAsync, chosen.Count > 0);
        Add("Als ZIP herunterladen …", "archive", DownloadZipAsync, chosen.Count > 0);
        items.Add(new Separator());
        Add("Kopieren", "copy", () => ClipAsync(cut: false), chosen.Count > 0, $"{command}+C");
        Add("Ausschneiden", "paste", () => ClipAsync(cut: true), chosen.Count > 0, $"{command}+X");
        Add("Einfügen", "clipboard", PasteAsync, _clipboard is not null, $"{command}+V");
        var others = OtherTabs();
        if (others.Count > 0 && chosen.Count > 0)
        {
            var copyTo = new MenuItem { Header = "Kopieren nach", Icon = new IconLabel { Icon = "copy" } };
            copyTo.ItemsSource = others.Select(other =>
            {
                var target = new MenuItem { Header = $"{other._connection.Name} › {other._folder}" };
                target.Click += async (_, _) => await CopyToAsync(other, move: false);
                return target;
            }).ToList();
            items.Add(copyTo);
        }

        items.Add(new Separator());
        Add("Umbenennen …", "edit", () => one is null ? Task.CompletedTask : RenameAsync(one), one is not null, "F2");
        Add("ZIP erstellen …", "archive", ZipIntoAsync, chosen.Count > 0);
        Add("Hier entpacken", "archive", () => one is null ? Task.CompletedTask : ExtractAsync(one), one is { IsDirectory: false } && FileOperations.IsArchive(one.Name));
        items.Add(new Separator());
        Add("Löschen …", "trash", DeleteAsync, chosen.Count > 0, "Delete");
        return items;
    }

    private static MenuFlyout Menu(params (string Header, string Icon, Func<Task> Action)[] entries)
    {
        var flyout = new MenuFlyout();
        flyout.ItemsSource = entries.Select(entry =>
        {
            var item = new MenuItem { Header = entry.Header, Icon = new IconLabel { Icon = entry.Icon } };
            item.Click += async (_, _) => await entry.Action();
            return item;
        }).ToList();
        return flyout;
    }

    private async Task KeyAsync(KeyEventArgs e)
    {
        if (_session is null || e.Source is TextBox)
        {
            return;
        }

        var command = e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
        Func<Task>? action = e.Key switch
        {
            Key.Delete => DeleteAsync,
            Key.F2 when Selected() is [var one] => () => RenameAsync(one),
            Key.F5 => () => NavigateAsync(_folder),
            Key.Back => () => NavigateAsync(RemoteFiles.ParentVirtualPath(_folder)),
            Key.C when command => () => ClipAsync(cut: false),
            Key.X when command => () => ClipAsync(cut: true),
            Key.V when command => PasteAsync,
            Key.A when command => () =>
            {
                _table.List.SelectAll();
                return Task.CompletedTask;
            },
            _ => null,
        };
        if (action is not null)
        {
            e.Handled = true;
            await action();
        }
    }

    private async Task OpenAsync(FileGatewayEntry entry)
    {
        if (entry.IsDirectory)
        {
            await NavigateAsync(entry.Path);
            return;
        }

        // A file opens with the program of this computer (Matgate's preview in the browser): a copy in the temporary folder.
        await RunAsync($"„{entry.Name}“ wird geöffnet …", async (session, token) => Ui.OpenExternal(await session.DownloadTemporaryAsync(entry.Path, token)), refresh: false);
    }

    private async Task NewAsync(bool folder)
    {
        var name = await PromptDialog.AskAsync(Ui.WindowOf(this), folder ? "Neuer Ordner" : "Neue Datei", "Name", folder ? "Neuer Ordner" : "Neue Datei.txt", "Anlegen");
        if (name is null)
        {
            return;
        }

        await RunAsync(null, (session, token) => folder ? session.CreateFolderAsync(_folder, name, token) : session.CreateFileAsync(_folder, name, token));
    }

    private async Task UploadFilesAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Dateien hochladen", AllowMultiple = true });
        await UploadLocalAsync([.. files.Select(file => file.TryGetLocalPath()).OfType<string>()]);
    }

    private async Task UploadFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Ordner hochladen", AllowMultiple = true });
        await UploadLocalAsync([.. folders.Select(folder => folder.TryGetLocalPath()).OfType<string>()]);
    }

    private async Task UploadArchiveAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Archiv hochladen und entpacken",
            FileTypeFilter = [new FilePickerFileType("Archive") { Patterns = ["*.zip", "*.rar", "*.7z", "*.tar", "*.gz", "*.tgz", "*.bz2", "*.xz"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } archive)
        {
            await RunAsync($"„{Path.GetFileName(archive)}“ wird entpackt …", async (session, token) =>
            {
                var count = await session.UploadAndExtractAsync(_folder, archive, token);
                Ui.Post(() => _message.Show($"{count} Dateien entpackt.", InfoSeverity.Success));
            });
        }
    }

    /// <summary>Uploads local files and folders into the folder shown (the file picker, a drop from Explorer / Finder).</summary>
    public async Task UploadLocalAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        await RunAsync("Hochladen …", async (session, token) =>
        {
            var index = 0;
            foreach (var path in paths)
            {
                index++;
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                if (Directory.Exists(path))
                {
                    Progress($"Ordner „{name}“ ({index}/{paths.Count}) …");
                    await session.UploadFolderAsync(_folder, path, token);
                }
                else
                {
                    var length = new FileInfo(path).Length;
                    await session.UploadAsync(_folder, path, new Progress<long>(done => Progress($"„{name}“ ({index}/{paths.Count}): {Size(done)} von {Size(length)}")), token);
                }
            }

            Ui.Post(() => _message.Show(paths.Count == 1 ? $"„{Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0]))}“ wurde hochgeladen." : $"{paths.Count} Einträge wurden hochgeladen.", InfoSeverity.Success));
        });
    }

    private async Task DownloadAsync()
    {
        var chosen = Selected();
        if (chosen.Count == 0 || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        if (chosen is [{ IsDirectory: false } file])
        {
            var target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Herunterladen", SuggestedFileName = file.Name });
            if (target?.TryGetLocalPath() is { } local)
            {
                await RunAsync($"„{file.Name}“ wird heruntergeladen …", async (session, token) =>
                {
                    await session.DownloadAsync(file.Path, local, new Progress<long>(done => Progress($"„{file.Name}“: {Size(done)} von {Size(file.Size ?? 0)}")), token);
                    Ui.Post(() => _message.Show($"„{file.Name}“ liegt in {Path.GetDirectoryName(local)}.", InfoSeverity.Success));
                }, refresh: false);
            }

            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Herunterladen in" });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } into)
        {
            return;
        }

        await RunAsync("Herunterladen …", async (session, token) =>
        {
            foreach (var entry in chosen)
            {
                Progress($"„{entry.Name}“ …");
                var local = Path.Combine(into, RemoteFiles.SafeFileName(entry.Name));
                if (entry.IsDirectory)
                {
                    await session.DownloadFolderAsync(entry.Path, local, token);
                }
                else
                {
                    await session.DownloadAsync(entry.Path, local, null, token);
                }
            }

            Ui.Post(() => _message.Show($"{chosen.Count} Einträge liegen in {into}.", InfoSeverity.Success));
        }, refresh: false);
    }

    private async Task DownloadZipAsync()
    {
        var chosen = Selected();
        if (chosen.Count == 0 || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var suggested = (chosen.Count == 1 ? Path.GetFileNameWithoutExtension(chosen[0].Name) : RemoteFiles.FileNameFromPath(_folder) is { Length: > 0 } name ? name : "Dateien") + ".zip";
        var target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Als ZIP herunterladen", SuggestedFileName = suggested, DefaultExtension = "zip" });
        if (target?.TryGetLocalPath() is { } local)
        {
            await RunAsync("ZIP wird erstellt …", async (session, token) =>
            {
                await session.DownloadZipAsync([.. chosen.Select(entry => entry.Path)], local, token);
                Ui.Post(() => _message.Show($"{Path.GetFileName(local)} liegt in {Path.GetDirectoryName(local)}.", InfoSeverity.Success));
            }, refresh: false);
        }
    }

    private async Task RenameAsync(FileGatewayEntry entry)
    {
        var name = await PromptDialog.AskAsync(Ui.WindowOf(this), "Umbenennen", "Neuer Name", entry.Name, "Umbenennen");
        if (name is not null && name != entry.Name)
        {
            await RunAsync($"„{entry.Name}“ wird umbenannt …", (session, token) => session.RenameAsync(entry.Path, name, token));
        }
    }

    private async Task DeleteAsync()
    {
        var chosen = Selected();
        if (chosen.Count == 0)
        {
            return;
        }

        var what = chosen.Count == 1 ? $"„{chosen[0].Name}“" + (chosen[0].IsDirectory ? " mit allem darin" : "") : $"{chosen.Count} Einträge";
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(this), "Löschen?", $"{what} wird auf dem Server gelöscht. Das lässt sich nicht rückgängig machen.", "Löschen", isDestructive: true))
        {
            return;
        }

        await RunAsync("Löschen …", (session, token) => session.DeleteAsync([.. chosen.Select(entry => entry.Path)], token));
    }

    private async Task ZipIntoAsync()
    {
        var chosen = Selected();
        var suggested = chosen.Count == 1 ? Path.GetFileNameWithoutExtension(chosen[0].Name) + ".zip" : "Archiv.zip";
        var name = await PromptDialog.AskAsync(Ui.WindowOf(this), "ZIP erstellen", "Name des Archivs", suggested, "Erstellen");
        if (name is not null)
        {
            await RunAsync("ZIP wird erstellt …", async (session, token) =>
            {
                var created = await session.ZipIntoAsync([.. chosen.Select(entry => entry.Path)], _folder, name, token);
                Ui.Post(() => _message.Show($"„{created}“ wurde erstellt.", InfoSeverity.Success));
            });
        }
    }

    private async Task ExtractAsync(FileGatewayEntry archive) =>
        await RunAsync($"„{archive.Name}“ wird entpackt …", async (session, token) =>
        {
            var count = await session.ExtractAsync(archive.Path, _folder, token);
            Ui.Post(() => _message.Show($"{count} Dateien entpackt.", InfoSeverity.Success));
        });

    // ---- clipboard between file tabs ----

    private Task ClipAsync(bool cut)
    {
        var chosen = Selected();
        if (chosen.Count > 0)
        {
            _clipboard = (this, [.. chosen.Select(entry => entry.Path)], cut);
            _message.Show($"{chosen.Count} Einträge {(cut ? "ausgeschnitten" : "kopiert")} – in einem Ordner (auch eines anderen Datei-Tabs) einfügen.", InfoSeverity.Info);
        }

        return Task.CompletedTask;
    }

    private async Task PasteAsync()
    {
        if (_clipboard is not { } clip || clip.Source._session is null)
        {
            return;
        }

        await clip.Source.CopyToAsync(this, clip.Cut, clip.Paths);
        if (clip.Cut)
        {
            _clipboard = null;
        }
    }

    private Task CopyToAsync(FilesView target, bool move) => CopyToAsync(target, move, [.. Selected().Select(entry => entry.Path)]);

    private async Task CopyToAsync(FilesView target, bool move, IReadOnlyList<string> paths)
    {
        if (_session is not { } source || target._session is not { } into)
        {
            return;
        }

        await target.RunAsync(move ? "Verschieben …" : "Kopieren …", async (_, token) =>
        {
            var result = await source.CopyToAsync(paths, into, target._folder, move, token);
            Ui.Post(() => target._message.Show(result.Failed.Count == 0
                ? $"{result.Copied} Dateien {(move ? "verschoben" : "kopiert")}."
                : $"{result.Copied} Dateien {(move ? "verschoben" : "kopiert")}, nicht geklappt: {string.Join("; ", result.Failed)}",
                result.Failed.Count == 0 ? InfoSeverity.Success : InfoSeverity.Warning));
        });
        if (move && target != this)
        {
            await NavigateAsync(_folder);
        }
    }

    private List<FilesView> OtherTabs()
    {
        lock (Open)
        {
            return [.. Open.Where(view => view != this && view._session is not null)];
        }
    }

    // ---- running ----

    /// <summary>Runs an operation with a status line; errors go into the message bar; the folder is read again afterwards.</summary>
    private async Task RunAsync(string? status, Func<FileSession, CancellationToken, Task> action, bool refresh = true)
    {
        if (_session is not { } session)
        {
            return;
        }

        if (status is not null)
        {
            _busy = true;
            _status.Text = status;
            _message.Hide();
        }

        try
        {
            await Task.Run(() => action(session, _work.Token));
            if (refresh)
            {
                Show(await session.ListAsync(_folder, _work.Token));
            }
        }
        catch (BusinessException exception)
        {
            _message.Show(exception.Message, InfoSeverity.Error);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _busy = false;
            ShowStatus();
        }
    }

    private void Progress(string text) => Ui.Post(() =>
    {
        if (_busy)
        {
            _status.Text = text;
        }
    });

    public static string Size(long? bytes)
    {
        if (bytes is not { } value)
        {
            return "";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} B" : $"{size.ToString(size < 10 ? "0.0" : "0", CultureInfo.CurrentCulture)} {units[unit]}";
    }
}
