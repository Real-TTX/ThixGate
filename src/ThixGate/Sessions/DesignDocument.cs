using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Matgate.Services.Database;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>
/// Matgate's table designer: a new table or one that is there - its name, columns (name, type, NULL, default, primary key, identity),
/// indexes and foreign keys. "Vorschau" shows the statements Matgate's TableDesigner makes for this server (what goes first, renames are
/// renames), "Anwenden" runs them (in a transaction, except on MySQL) and the tree reads the database again.
/// </summary>
public sealed class DesignDocument : UserControl
{
    private readonly DatabaseView _owner;
    private readonly DatabaseSession _session;
    private readonly string _database;
    private readonly string _schema;
    private readonly string? _originalName;
    private readonly DbTableInfo? _original;
    private readonly TextBox _name = new() { MinWidth = 280 };
    private readonly ObservableCollection<ColumnRow> _columns = [];
    private readonly ObservableCollection<IndexRow> _indexes = [];
    private readonly ObservableCollection<KeyRow> _keys = [];

    public DesignDocument(DatabaseView owner, DatabaseSession session, string database, string schema, string? table, DbTableInfo? original)
    {
        _owner = owner;
        _session = session;
        _database = database;
        _schema = schema;
        _originalName = table;
        _original = original;
        _name.Text = table ?? "neue_tabelle";
        AutomationProperties.SetName(_name, "Name der Tabelle");
        foreach (var column in original?.Columns ?? [])
        {
            _columns.Add(new ColumnRow { OriginalName = column.Name, Name = column.Name, Type = column.Type, Nullable = column.Nullable, Default = column.Default ?? "", PrimaryKey = column.PrimaryKey, Identity = column.Identity });
        }

        foreach (var index in (original?.Indexes ?? []).Where(index => !index.Primary))
        {
            _indexes.Add(new IndexRow { OriginalName = index.Name, Name = index.Name, Columns = index.Columns, Unique = index.Unique });
        }

        foreach (var key in original?.ForeignKeys ?? [])
        {
            _keys.Add(new KeyRow { OriginalName = key.Name, Name = key.Name, Columns = key.Columns, ReferencedTable = key.ReferencedTable, ReferencedColumns = key.ReferencedColumns, OnDelete = key.OnDelete, OnUpdate = key.OnUpdate });
        }

        if (_columns.Count == 0)
        {
            _columns.Add(new ColumnRow { Name = "id", Type = session.Engine == DatabaseEngine.Postgres ? "integer" : "int", PrimaryKey = true, Identity = true });
        }

        var preview = Ui.Button("code", "Vorschau");
        preview.Click += async (_, _) => await PreviewAsync();
        var apply = Ui.Button("check", "Anwenden", "MatButton.Primary");
        apply.Click += async (_, _) => await ApplyAsync();
        var head = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(0, 10, 0, 8) };
        head.Children.AddRange([new MatFormField { Label = "Tabelle", Content = _name }, preview, apply]);

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(head);
        panel.Children.Add(Section("Spalten", Grid(_columns,
            Text<ColumnRow>("Name", nameof(ColumnRow.Name)), Text<ColumnRow>("Typ", nameof(ColumnRow.Type)), Check<ColumnRow>("NULL", nameof(ColumnRow.Nullable)),
            Text<ColumnRow>("Standard", nameof(ColumnRow.Default)), Check<ColumnRow>("PK", nameof(ColumnRow.PrimaryKey)), Check<ColumnRow>("Identität", nameof(ColumnRow.Identity))),
            () => _columns.Add(new ColumnRow { Name = $"spalte{_columns.Count + 1}", Type = "varchar(100)", Nullable = true })));
        panel.Children.Add(Section("Indizes", Grid(_indexes,
            Text<IndexRow>("Name", nameof(IndexRow.Name)), Text<IndexRow>("Spalten (mit Komma)", nameof(IndexRow.Columns)), Check<IndexRow>("Eindeutig", nameof(IndexRow.Unique))),
            () => _indexes.Add(new IndexRow { Name = $"ix_{_name.Text}_{_indexes.Count + 1}" })));
        panel.Children.Add(Section("Fremdschlüssel", Grid(_keys,
            Text<KeyRow>("Name", nameof(KeyRow.Name)), Text<KeyRow>("Spalten", nameof(KeyRow.Columns)), Text<KeyRow>("Verweist auf (Tabelle)", nameof(KeyRow.ReferencedTable)),
            Text<KeyRow>("Spalten dort", nameof(KeyRow.ReferencedColumns)), Text<KeyRow>("Beim Löschen", nameof(KeyRow.OnDelete)), Text<KeyRow>("Beim Ändern", nameof(KeyRow.OnUpdate))),
            () => _keys.Add(new KeyRow { Name = $"fk_{_name.Text}_{_keys.Count + 1}" })));
        Content = new ScrollViewer { Content = panel };
    }

    /// <summary>The design as Matgate's TableDesigner takes it.</summary>
    public TableDesign Design() => new(
        (_name.Text ?? "").Trim(),
        [.. _columns.Select(row => new DesignColumn(row.OriginalName, row.Name.Trim(), row.Type.Trim(), row.Nullable, row.Default.Trim().Length > 0 ? row.Default.Trim() : null, row.PrimaryKey, row.Identity))],
        [.. _indexes.Select(row => new DesignIndex(row.OriginalName, row.Name.Trim(), row.Columns.Trim(), row.Unique))],
        [.. _keys.Select(row => new DesignForeignKey(row.OriginalName, row.Name.Trim(), row.Columns.Trim(), row.ReferencedTable.Trim(), row.ReferencedColumns.Trim(),
            row.OnDelete.Trim().Length > 0 ? row.OnDelete.Trim().ToUpperInvariant() : "NO ACTION", row.OnUpdate.Trim().Length > 0 ? row.OnUpdate.Trim().ToUpperInvariant() : "NO ACTION"))]);

    private List<string> Statements() => _session.DesignStatements(_schema, _originalName, _original, Design());

    private async Task PreviewAsync()
    {
        try
        {
            var statements = Statements();
            await Dialogs.ConfirmDialog.ShowMessageAsync(Ui.WindowOf(this), "Diese Anweisungen laufen beim Anwenden",
                statements.Count == 0 ? "Nichts geändert." : string.Join(Environment.NewLine + Environment.NewLine, statements));
        }
        catch (Exception exception) when (exception is BusinessException or DatabaseException)
        {
            _owner.Show(exception.Message, InfoSeverity.Error);
        }
    }

    /// <summary>Runs the statements (tests await it).</summary>
    public async Task ApplyAsync()
    {
        try
        {
            var statements = Statements();
            if (statements.Count == 0)
            {
                _owner.Show("Nichts geändert.", InfoSeverity.Info);
                return;
            }

            await Task.Run(() => _session.ApplyAsync(_database, statements, _owner.Token));
            _owner.Show($"Die Tabelle „{Design().Name}“ ist {(_originalName is null ? "angelegt" : "geändert")}.", InfoSeverity.Success);
            _owner.CatalogChanged(_database);
        }
        catch (Exception exception) when (exception is BusinessException or DatabaseException)
        {
            _owner.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private static Control Section(string title, DataGrid grid, Action add)
    {
        var plus = Ui.Button("plus", "Hinzufügen");
        plus.Click += (_, _) => add();
        var minus = Ui.Button("trash", "Entfernen");
        minus.Click += (_, _) =>
        {
            if (grid.ItemsSource is System.Collections.IList list)
            {
                foreach (var item in grid.SelectedItems.Cast<object>().ToList())
                {
                    list.Remove(item);
                }
            }
        };
        var head = new DockPanel();
        head.Children.Add(Ui.Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { plus, minus } }, Dock.Right));
        head.Children.Add(Ui.Text(title, "MatText.SectionTitle"));
        return new StackPanel { Spacing = 6, Children = { head, grid } };
    }

    private static DataGrid Grid<T>(ObservableCollection<T> items, params DataGridColumn[] columns)
    {
        var grid = new DataGrid
        {
            ItemsSource = items,
            CanUserSortColumns = false,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            MinHeight = 90,
            MaxHeight = 360,
        };
        foreach (var column in columns)
        {
            grid.Columns.Add(column);
        }

        return grid;
    }

    private static DataGridTextColumn Text<T>(string header, string property) => new() { Header = header, Binding = new Binding(property) { Mode = BindingMode.TwoWay }, MinWidth = 90 };

    private static DataGridCheckBoxColumn Check<T>(string header, string property) => new() { Header = header, Binding = new Binding(property) { Mode = BindingMode.TwoWay } };

    public sealed class ColumnRow
    {
        public string OriginalName { get; init; } = "";

        public string Name { get; set; } = "";

        public string Type { get; set; } = "";

        public bool Nullable { get; set; }

        public string Default { get; set; } = "";

        public bool PrimaryKey { get; set; }

        public bool Identity { get; set; }
    }

    public sealed class IndexRow
    {
        public string OriginalName { get; init; } = "";

        public string Name { get; set; } = "";

        public string Columns { get; set; } = "";

        public bool Unique { get; set; }
    }

    public sealed class KeyRow
    {
        public string OriginalName { get; init; } = "";

        public string Name { get; set; } = "";

        public string Columns { get; set; } = "";

        public string ReferencedTable { get; set; } = "";

        public string ReferencedColumns { get; set; } = "";

        public string OnDelete { get; set; } = "NO ACTION";

        public string OnUpdate { get; set; } = "NO ACTION";
    }
}
