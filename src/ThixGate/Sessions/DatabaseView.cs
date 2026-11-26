using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Matgate.Services.Database;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>
/// The database browser of a database connection (SQL Server, MySQL/MariaDB, PostgreSQL) in a tab - Matgate's, with its code
/// (<see cref="DatabaseSession"/>). Left the object explorer (filter; databases with Tabellen, Sichten, Prozeduren, Funktionen; a table with
/// Spalten, Schlüssel, Indizes, Fremdschlüssel; right click: Zeilen zeigen, SELECT-Skript, CREATE-Skript, Struktur, Entwerfen, Name kopieren,
/// Neue Tabelle, Export). Right the documents as tabs: Abfrage (<see cref="QueryDocument"/>), Zeilen (<see cref="RowsDocument"/>, editable with a
/// primary key), Struktur, Entwurf (<see cref="DesignDocument"/>).
/// </summary>
public sealed class DatabaseView : UserControl, ISessionPanel
{
    private readonly RemoteConnection _connection;
    private readonly TextBox _filter = new() { Watermark = "Filtern" };
    private readonly TreeView _tree = new();
    private readonly TabControl _documents = new();
    private readonly InfoBar _message = new();
    private CancellationTokenSource _work = new();
    private DatabaseSession? _session;
    private List<string> _databases = [];
    private readonly Dictionary<string, (List<DbObject> Objects, List<DbRoutine> Routines)> _catalog = new(StringComparer.Ordinal);
    private int _queries;

    public DatabaseView(RemoteConnection connection)
    {
        _connection = connection;
        this.Use(BackgroundProperty, "Mat.Surface");
        AutomationProperties.SetName(_filter, "Objekte filtern");
        AutomationProperties.SetName(_tree, "Datenbankobjekte");
        _filter.TextChanged += (_, _) => BuildTree();
        _tree.ContextRequested += (_, e) =>
        {
            if (_tree.SelectedItem is TreeViewItem { Tag: Node node } && Menu(node) is { } menu)
            {
                menu.Open(_tree);
                e.Handled = true;
            }
        };
        _tree.DoubleTapped += (_, _) =>
        {
            if (_tree.SelectedItem is TreeViewItem { Tag: Node { Kind: NodeKind.Table or NodeKind.View } node })
            {
                OpenRows(node);
            }
        };

        var newQuery = Ui.Button("plus", "Neue Abfrage");
        newQuery.Click += (_, _) => OpenQuery(CurrentDatabase(), "");
        var refresh = Ui.Button("refresh", "");
        ToolTip.SetTip(refresh, "Neu lesen");
        AutomationProperties.SetName(refresh, "Neu lesen");
        refresh.Click += async (_, _) => await LoadCatalogAsync();
        var leftHead = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        leftHead.Children.Add(Ui.Docked(refresh, Dock.Right));
        leftHead.Children.Add(new Border { Margin = new Thickness(0, 0, 8, 0), Child = _filter });
        var left = new DockPanel { Margin = new Thickness(12, 12, 6, 12) };
        left.Children.Add(Ui.Docked(leftHead, Dock.Top));
        left.Children.Add(Ui.Docked(new Border { Margin = new Thickness(0, 8, 0, 0), Child = newQuery }, Dock.Bottom));
        left.Children.Add(Ui.Card(_tree, new Thickness(4)));

        var right = new DockPanel { Margin = new Thickness(6, 12, 12, 12) };
        right.Children.Add(Ui.Docked(_message, Dock.Top));
        right.Children.Add(_documents);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("280,Auto,*") };
        grid.Children.Add(Ui.Cell(left, 0, 0));
        grid.Children.Add(Ui.Cell(new GridSplitter { Width = 4, ResizeDirection = GridResizeDirection.Columns, Background = null }, 0, 1));
        grid.Children.Add(Ui.Cell(right, 0, 2));
        Content = grid;
        IsEnabled = false;
    }

    public Control View => this;

    internal DatabaseSession? Session => _session;

    /// <summary>The documents open now (tests).</summary>
    public TabControl Documents => _documents;

    /// <summary>The databases (tests).</summary>
    public IReadOnlyList<string> Databases => _databases;

    public TreeView Tree => _tree;

    // ================================================================= life cycle

    public async Task ConnectAsync(AskSignIn ask, CancellationToken cancellationToken)
    {
        var session = await DatabaseSession.OpenAsync(_connection, ask, cancellationToken);
        var databases = _connection.Database.Length > 0 ? [_connection.Database] : await session.DatabasesAsync(cancellationToken);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            _session = session;
            _work = new CancellationTokenSource();
            _databases = databases;
            IsEnabled = true;
            if (_databases.Count == 1)
            {
                _expanded.Add($"db:{_databases[0]}"); // the only database: open in the tree
            }

            BuildTree();
            if (_documents.Items.Count == 0)
            {
                OpenQuery(_databases.FirstOrDefault(), "");
            }

            if (_databases.Count == 1)
            {
                await LoadDatabaseAsync(_databases[0]);
            }
        });
    }

    public void Disconnect()
    {
        _work.Cancel();
        _session = null;
        IsEnabled = false;
    }

    public void FocusPanel()
    {
        if (_documents.SelectedItem is TabItem { Content: QueryDocument query })
        {
            query.FocusEditor();
        }
        else
        {
            _tree.Focus();
        }
    }

    /// <summary>Opens a query document (tests use it too).</summary>
    public QueryDocument OpenQuery(string? database, string sql)
    {
        var document = new QueryDocument(this, _session!, database, sql);
        AddDocument($"Abfrage {++_queries}", "code", document);
        return document;
    }

    internal void Show(string message, InfoSeverity severity) => _message.Show(message, severity);

    internal IReadOnlyList<string> DatabaseNames => _databases;

    internal CancellationToken Token => _work.Token;

    /// <summary>Table and column names for completion (the databases read so far).</summary>
    internal IEnumerable<string> KnownNames(string? database) =>
        database is not null && _catalog.TryGetValue(database, out var catalog) ? catalog.Objects.Select(item => item.Name) : [];

    // ================================================================= the tree

    private enum NodeKind
    {
        Database,
        Group,
        Table,
        View,
        Routine,
        Detail,
    }

    private sealed record Node(NodeKind Kind, string Database, string Schema = "", string Name = "", string RoutineKind = "");

    private async Task LoadCatalogAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        try
        {
            _catalog.Clear();
            _databases = _connection.Database.Length > 0 ? [_connection.Database] : await session.DatabasesAsync(Token);
            BuildTree();
        }
        catch (BusinessException exception)
        {
            Show(exception.Message, InfoSeverity.Error);
        }
    }

    private async Task LoadDatabaseAsync(string database)
    {
        if (_session is not { } session || _catalog.ContainsKey(database))
        {
            return;
        }

        try
        {
            var objects = await session.ObjectsAsync(database, Token);
            List<DbRoutine> routines;
            try
            {
                routines = await session.RoutinesAsync(database, Token);
            }
            catch (BusinessException)
            {
                routines = []; // a login without the right to read the routines still sees its tables
            }

            _catalog[database] = (objects, routines);
            BuildTree();
        }
        catch (BusinessException exception)
        {
            Show(exception.Message, InfoSeverity.Error);
        }
    }

    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    private void BuildTree()
    {
        var filter = (_filter.Text ?? "").Trim();
        var items = new List<TreeViewItem>();
        foreach (var database in _databases)
        {
            var databaseItem = Item($"db:{database}", database, "database", new Node(NodeKind.Database, database));
            if (_catalog.TryGetValue(database, out var catalog))
            {
                bool Matches(string name) => filter.Length == 0 || name.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
                var groups = new (string Title, string Icon, IEnumerable<(Node Node, string Text)> Items)[]
                {
                    ("Tabellen", "table", catalog.Objects.Where(item => !IsView(item) && Matches(item.Name)).Select(item => (new Node(NodeKind.Table, database, item.Schema, item.Name), Label(item.Schema, item.Name)))),
                    ("Sichten", "eye", catalog.Objects.Where(item => IsView(item) && Matches(item.Name)).Select(item => (new Node(NodeKind.View, database, item.Schema, item.Name), Label(item.Schema, item.Name)))),
                    ("Prozeduren", "code", catalog.Routines.Where(item => !IsFunction(item) && Matches(item.Name)).Select(item => (new Node(NodeKind.Routine, database, item.Schema, item.Name, item.Kind), Label(item.Schema, item.Name)))),
                    ("Funktionen", "code", catalog.Routines.Where(item => IsFunction(item) && Matches(item.Name)).Select(item => (new Node(NodeKind.Routine, database, item.Schema, item.Name, item.Kind), Label(item.Schema, item.Name)))),
                };
                var children = new List<TreeViewItem>();
                foreach (var (title, icon, groupItems) in groups)
                {
                    var list = groupItems.ToList();
                    var group = Item($"group:{database}:{title}", $"{title} ({list.Count})", "folder", new Node(NodeKind.Group, database));
                    group.ItemsSource = list.Select(entry => TableItem(entry.Node, entry.Text, icon)).ToList();
                    if (filter.Length > 0 && list.Count > 0)
                    {
                        group.IsExpanded = true;
                    }

                    children.Add(group);
                }

                databaseItem.ItemsSource = children;
            }
            else
            {
                databaseItem.ItemsSource = new[] { new TreeViewItem { Header = "…" } };
            }

            items.Add(databaseItem);
        }

        _tree.ItemsSource = items;
    }

    private static bool IsView(DbObject item) => item.Kind.Contains("VIEW", StringComparison.OrdinalIgnoreCase) || item.Kind.Equals("V", StringComparison.OrdinalIgnoreCase);

    private static bool IsFunction(DbRoutine routine) => routine.Kind.Contains("FUNCTION", StringComparison.OrdinalIgnoreCase) || routine.Kind.StartsWith('F');

    private static string Label(string schema, string name) => schema.Length > 0 ? $"{schema}.{name}" : name;

    private TreeViewItem Item(string key, string text, string icon, Node node)
    {
        var item = new TreeViewItem { Header = new IconLabel { Icon = icon, Text = text }, Tag = node, IsExpanded = _expanded.Contains(key) };
        item.PropertyChanged += async (_, e) =>
        {
            if (e.Property != TreeViewItem.IsExpandedProperty)
            {
                return;
            }

            if (item.IsExpanded)
            {
                _expanded.Add(key);
                if (node.Kind == NodeKind.Database)
                {
                    await LoadDatabaseAsync(node.Database);
                }
            }
            else
            {
                _expanded.Remove(key);
            }
        };
        return item;
    }

    private TreeViewItem TableItem(Node node, string text, string icon)
    {
        var item = new TreeViewItem { Header = new IconLabel { Icon = icon, Text = text }, Tag = node };
        if (node.Kind is not (NodeKind.Table or NodeKind.View))
        {
            return item;
        }

        // Spalten, Schlüssel, Indizes, Fremdschlüssel: read when the table is opened in the tree.
        item.ItemsSource = new[] { new TreeViewItem { Header = "…" } };
        var loaded = false;
        item.PropertyChanged += async (_, e) =>
        {
            if (e.Property != TreeViewItem.IsExpandedProperty || !item.IsExpanded || loaded || _session is not { } session)
            {
                return;
            }

            loaded = true;
            try
            {
                var info = await session.TableAsync(node.Database, node.Schema, node.Name, Token);
                TreeViewItem Group(string title, string groupIcon, IEnumerable<string> lines) =>
                    new() { Header = new IconLabel { Icon = groupIcon, Text = title }, ItemsSource = lines.Select(line => new TreeViewItem { Header = line, Tag = new Node(NodeKind.Detail, node.Database) }).ToList() };
                item.ItemsSource = new[]
                {
                    Group($"Spalten ({info.Columns.Count})", "list", info.Columns.Select(column => $"{column.Name} · {column.Type}{(column.Nullable ? "" : " · NOT NULL")}{(column.PrimaryKey ? " · PK" : "")}")),
                    Group("Schlüssel", "key", info.Indexes.Where(index => index.Primary || index.Unique).Select(index => $"{index.Name} ({index.Columns})")),
                    Group($"Indizes ({info.Indexes.Count})", "layers", info.Indexes.Select(index => $"{index.Name} ({index.Columns}){(index.Unique ? " · eindeutig" : "")}")),
                    Group($"Fremdschlüssel ({info.ForeignKeys.Count})", "link", info.ForeignKeys.Select(key => $"{key.Name}: {key.Columns} → {key.ReferencedTable} ({key.ReferencedColumns})")),
                };
            }
            catch (BusinessException exception)
            {
                Show(exception.Message, InfoSeverity.Error);
            }
        };
        return item;
    }

    private string? CurrentDatabase() =>
        _tree.SelectedItem is TreeViewItem { Tag: Node node } ? node.Database : _databases.FirstOrDefault();

    private ContextMenu? Menu(Node node)
    {
        var items = new List<Control>();
        void Add(string header, string icon, Action action)
        {
            var item = new MenuItem { Header = header, Icon = new IconLabel { Icon = icon } };
            item.Click += (_, _) => action();
            items.Add(item);
        }

        switch (node.Kind)
        {
            case NodeKind.Table or NodeKind.View:
                var dialect = DatabaseGateway.DialectOf(_session!.Engine);
                var table = dialect.Table(node.Schema, node.Name);
                Add("Zeilen zeigen", "table", () => OpenRows(node));
                Add("Skript: SELECT", "code", () => OpenQuery(node.Database, $"SELECT * FROM {table}"));
                Add("Skript: CREATE", "code", async () => await ScriptAsync(node, node.Kind == NodeKind.View ? "view" : "table"));
                Add("Struktur zeigen", "list", async () => await OpenStructureAsync(node));
                if (node.Kind == NodeKind.Table)
                {
                    Add("Entwerfen", "edit", async () => await OpenDesignAsync(node.Database, node.Schema, node.Name));
                }

                Add("Name kopieren", "copy", async () => await Ui.CopyTextAsync(this, table));
                items.Add(new Separator());
                Add("Als CSV exportieren …", "download", async () => await ExportAsync(node.Database, $"SELECT * FROM {table}", node.Name, json: false));
                Add("Als JSON exportieren …", "download", async () => await ExportAsync(node.Database, $"SELECT * FROM {table}", node.Name, json: true));
                break;
            case NodeKind.Routine:
                Add("Skript: CREATE", "code", async () => await ScriptAsync(node, node.RoutineKind.Contains("FUNC", StringComparison.OrdinalIgnoreCase) ? "function" : "procedure"));
                Add("Name kopieren", "copy", async () => await Ui.CopyTextAsync(this, DatabaseGateway.DialectOf(_session!.Engine).Table(node.Schema, node.Name)));
                break;
            case NodeKind.Database or NodeKind.Group:
                Add("Neue Abfrage", "code", () => OpenQuery(node.Database, ""));
                Add("Neue Tabelle …", "plus", async () => await OpenDesignAsync(node.Database, DefaultSchema(), null));
                Add("Neu lesen", "refresh", () =>
                {
                    _catalog.Remove(node.Database);
                    _ = LoadDatabaseAsync(node.Database);
                });
                break;
            default:
                return null;
        }

        return new ContextMenu { ItemsSource = items };
    }

    private string DefaultSchema() => _session?.Engine switch
    {
        DatabaseEngine.MsSql => "dbo",
        DatabaseEngine.Postgres => "public",
        _ => "",
    };

    // ================================================================= documents

    private void AddDocument(string title, string icon, Control content)
    {
        var close = new Button { Content = new IconLabel { Icon = "x" }, Padding = new Thickness(4, 0), Background = null, BorderThickness = new Thickness(0) };
        ToolTip.SetTip(close, "Schließen");
        AutomationProperties.SetName(close, $"{title} schließen");
        var tab = new TabItem
        {
            Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { new IconLabel { Icon = icon, Text = title }, close } },
            Content = content,
        };
        close.Click += (_, _) => _documents.Items.Remove(tab);
        tab.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Middle)
            {
                _documents.Items.Remove(tab);
            }
        };
        _documents.Items.Add(tab);
        _documents.SelectedItem = tab;
    }

    private void OpenRows(Node node)
    {
        if (_session is { } session)
        {
            AddDocument(node.Name, "table", new RowsDocument(this, session, node.Database, node.Schema, node.Name, node.Kind == NodeKind.Table));
        }
    }

    private async Task OpenStructureAsync(Node node)
    {
        if (_session is not { } session)
        {
            return;
        }

        try
        {
            var info = await session.TableAsync(node.Database, node.Schema, node.Name, Token);
            var columns = new DbResult(["Spalte", "Typ", "NULL", "Standard", "PK", "Identität"],
                [.. info.Columns.Select(column => new[] { column.Name, column.Type, column.Nullable ? "ja" : "nein", column.Default, column.PrimaryKey ? "ja" : "", column.Identity ? "ja" : "" })], null, false, 0);
            var indexes = new DbResult(["Index", "Spalten", "Eindeutig", "Primär"],
                [.. info.Indexes.Select(index => new[] { index.Name, index.Columns, index.Unique ? "ja" : "", index.Primary ? "ja" : "" })], null, false, 0);
            var keys = new DbResult(["Fremdschlüssel", "Spalten", "Verweist auf", "Spalten dort", "Beim Löschen", "Beim Ändern"],
                [.. info.ForeignKeys.Select(key => new[] { key.Name, key.Columns, key.ReferencedTable, key.ReferencedColumns, key.OnDelete, key.OnUpdate })], null, false, 0);
            var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var (title, result) in new[] { ("Spalten", columns), ("Indizes", indexes), ("Fremdschlüssel", keys) })
            {
                panel.Children.Add(Ui.Text(title, "MatText.SectionTitle"));
                panel.Children.Add(ResultGrids.For(result, maxHeight: 320));
            }

            AddDocument($"{node.Name} (Struktur)", "list", new ScrollViewer { Content = panel });
        }
        catch (BusinessException exception)
        {
            Show(exception.Message, InfoSeverity.Error);
        }
    }

    private async Task ScriptAsync(Node node, string kind)
    {
        if (_session is not { } session)
        {
            return;
        }

        try
        {
            OpenQuery(node.Database, await session.ScriptAsync(node.Database, kind, node.Schema, node.Name, Token));
        }
        catch (BusinessException exception)
        {
            Show(exception.Message, InfoSeverity.Error);
        }
    }

    internal async Task OpenDesignAsync(string database, string schema, string? table)
    {
        if (_session is not { } session)
        {
            return;
        }

        try
        {
            var original = table is null ? null : await session.TableAsync(database, schema, table, Token);
            AddDocument(table is null ? "Neue Tabelle" : $"{table} (Entwurf)", "edit", new DesignDocument(this, session, database, schema, table, original));
        }
        catch (BusinessException exception)
        {
            Show(exception.Message, InfoSeverity.Error);
        }
    }

    /// <summary>A table changed (designer, new table): the tree reads its database again.</summary>
    internal void CatalogChanged(string database)
    {
        _catalog.Remove(database);
        _ = LoadDatabaseAsync(database);
    }

    internal async Task ExportAsync(string? database, string sql, string name, bool json)
    {
        if (_session is not { } session || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var target = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = json ? "Als JSON exportieren" : "Als CSV exportieren",
            SuggestedFileName = name + (json ? ".json" : ".csv"),
            DefaultExtension = json ? "json" : "csv",
        });
        if (target?.TryGetLocalPath() is not { } local)
        {
            return;
        }

        try
        {
            Show("Export läuft …", InfoSeverity.Info);
            var rows = await Task.Run(() => session.ExportAsync(database, sql, local, json, ';', Token));
            Show($"{rows:N0} Zeilen nach {Path.GetFileName(local)} exportiert.", InfoSeverity.Success);
        }
        catch (BusinessException exception)
        {
            Show(exception.Message, InfoSeverity.Error);
        }
    }
}
