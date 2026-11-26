using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Matgate.Services.Database;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>
/// The rows of a table or view, page by page (100; Zurück / Weiter, the total when the server counts it in time), ordered by a click on a
/// column. A table with a primary key can be edited as in Matgate: change cells, Neue Zeile, Zeile löschen; "Vorschau" shows the statements,
/// "Speichern" runs them in one transaction (undone unless each hits exactly one row), "Verwerfen" forgets the changes. NULL is written NULL.
/// </summary>
public sealed class RowsDocument : UserControl
{
    private const int PageSize = 100;

    private readonly DatabaseView _owner;
    private readonly DatabaseSession _session;
    private readonly string _database;
    private readonly string _schema;
    private readonly string _table;
    private readonly bool _isTable;
    private readonly DataGrid _grid = new()
    {
        CanUserSortColumns = true,
        CanUserResizeColumns = true,
        GridLinesVisibility = DataGridGridLinesVisibility.All,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        SelectionMode = DataGridSelectionMode.Extended,
    };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _previous = Ui.Button("chevron-left", "Zurück");
    private readonly Button _next = Ui.Button("chevron-right", "Weiter");
    private readonly Button _reload = Ui.Button("refresh", "Neu lesen");
    private readonly Button _add = Ui.Button("plus", "Neue Zeile");
    private readonly Button _remove = Ui.Button("trash", "Zeile löschen");
    private readonly Button _preview = Ui.Button("code", "Vorschau");
    private readonly Button _save = Ui.Button("save", "Speichern", "MatButton.Primary");
    private readonly Button _discard = Ui.Button("x", "Verwerfen");
    private readonly List<EditRow> _deleted = [];
    private DbTableInfo? _info;
    private IReadOnlyList<string> _columns = [];
    private List<EditRow> _rows = [];
    private int _offset;
    private long? _total;
    private string? _order;
    private bool _descending;

    public RowsDocument(DatabaseView owner, DatabaseSession session, string database, string schema, string table, bool isTable)
    {
        _owner = owner;
        _session = session;
        _database = database;
        _schema = schema;
        _table = table;
        _isTable = isTable;
        _previous.Click += async (_, _) => await LoadAsync(Math.Max(0, _offset - PageSize));
        _next.Click += async (_, _) => await LoadAsync(_offset + PageSize);
        _reload.Click += async (_, _) => await LoadAsync(_offset);
        _add.Click += (_, _) => AddRow();
        _remove.Click += (_, _) => RemoveRows();
        _preview.Click += async (_, _) => await PreviewAsync();
        _save.Click += async (_, _) => await SaveAsync();
        _discard.Click += async (_, _) => await LoadAsync(_offset);
        _grid.Sorting += async (_, e) =>
        {
            // ordered by the server (the whole table, not the page)
            e.Handled = true;
            var column = e.Column.Tag as string;
            _descending = _order == column && !_descending;
            _order = column;
            await LoadAsync(0);
        };
        _grid.CellEditEnded += (_, _) => ShowStatus();

        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(0, 10, 0, 8) };
        bar.Children.AddRange([_previous, _next, _reload, new Border { Width = 8 }, _add, _remove, _preview, _save, _discard]);
        _status.Themed("MatText.Muted");
        var root = new DockPanel();
        root.Children.Add(Ui.Docked(bar, Dock.Top));
        root.Children.Add(Ui.Docked(new Border { Padding = new Thickness(0, 6), Child = _status }, Dock.Bottom));
        root.Children.Add(_grid);
        Content = root;
        AttachedToVisualTree += async (_, _) =>
        {
            if (_info is null)
            {
                await LoadAsync(0);
            }
        };
    }

    public DataGrid Grid => _grid;

    /// <summary>The rows shown (tests).</summary>
    internal IReadOnlyList<EditRow> Rows => _rows;

    /// <summary>Can be edited: a table with a primary key.</summary>
    public bool IsEditable => _isTable && _info is { } info && info.Columns.Any(column => column.PrimaryKey);

    public async Task LoadAsync(int offset)
    {
        try
        {
            _info ??= await _session.TableAsync(_database, _schema, _table, _owner.Token);
            var result = await Task.Run(() => _session.RowsAsync(_database, _schema, _table, offset, PageSize, _order, _descending, _owner.Token));
            _offset = offset;
            _total = result.Total;
            _columns = result.Columns;
            _rows = [.. result.Rows.Select(row => new EditRow(row))];
            _deleted.Clear();
            BuildColumns();
            _grid.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<EditRow>(_rows);
            ShowStatus();
        }
        catch (BusinessException exception)
        {
            _owner.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private void BuildColumns()
    {
        _grid.Columns.Clear();
        var editable = IsEditable;
        _grid.IsReadOnly = !editable;
        for (var index = 0; index < _columns.Count; index++)
        {
            var name = _columns[index];
            var column = _info?.Columns.FirstOrDefault(item => item.Name == name);
            var header = name + (column?.PrimaryKey == true ? " (PK)" : "") + (_order == name ? (_descending ? " ↓" : " ↑") : "");
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Tag = name,
                Binding = new Binding($"[{index}]") { Mode = BindingMode.TwoWay },
                IsReadOnly = !editable || column is null || !DatabaseGateway.IsEditableType(_session.Engine, column.Type),
                MaxWidth = 480,
            });
        }

        _add.IsVisible = _remove.IsVisible = _preview.IsVisible = _save.IsVisible = _discard.IsVisible = editable;
    }

    private void ShowStatus()
    {
        var changes = _rows.Count(row => row.IsChanged || row.IsNew) + _deleted.Count;
        var range = _rows.Count == 0 ? "keine Zeilen" : $"Zeilen {_offset + 1:N0}–{_offset + _rows.Count(row => !row.IsNew):N0}{(_total is { } total ? $" von {total:N0}" : "")}";
        _status.Text = range + (changes > 0 ? $" · {changes} Änderungen nicht gespeichert" : "") + (_isTable && !IsEditable ? " · ohne Primärschlüssel nur zum Lesen" : "");
        _previous.IsEnabled = _offset > 0;
        _next.IsEnabled = _total is { } all ? _offset + PageSize < all : _rows.Count >= PageSize;
    }

    private void AddRow()
    {
        var row = new EditRow(new string?[_columns.Count]) { IsNew = true };
        _rows.Add(row);
        ((System.Collections.ObjectModel.ObservableCollection<EditRow>)_grid.ItemsSource!).Add(row);
        _grid.SelectedItem = row;
        _grid.ScrollIntoView(row, _grid.Columns.FirstOrDefault(column => !column.IsReadOnly));
        ShowStatus();
    }

    private void RemoveRows()
    {
        foreach (var row in _grid.SelectedItems.OfType<EditRow>().ToList())
        {
            _rows.Remove(row);
            ((System.Collections.ObjectModel.ObservableCollection<EditRow>)_grid.ItemsSource!).Remove(row);
            if (!row.IsNew)
            {
                _deleted.Add(row);
            }
        }

        ShowStatus();
    }

    /// <summary>What changed, as Matgate's row changes: key = the primary key's original values, values = the changed (or all new) ones.</summary>
    internal List<RowChange> Changes()
    {
        var keys = _info!.Columns.Where(column => column.PrimaryKey).Select(column => column.Name).ToList();
        Dictionary<string, string?> KeyOf(EditRow row) => keys.ToDictionary(key => key, key => row.Original[_columns.ToList().IndexOf(key)]);
        var changes = new List<RowChange>();
        foreach (var row in _deleted)
        {
            changes.Add(new RowChange("delete", KeyOf(row), null));
        }

        foreach (var row in _rows.Where(row => row.IsChanged && !row.IsNew))
        {
            var values = new Dictionary<string, string?>();
            for (var index = 0; index < _columns.Count; index++)
            {
                if (row.Value(index) != row.Original[index])
                {
                    values[_columns[index]] = row.Value(index);
                }
            }

            changes.Add(new RowChange("update", KeyOf(row), values));
        }

        foreach (var row in _rows.Where(row => row.IsNew))
        {
            var values = new Dictionary<string, string?>();
            for (var index = 0; index < _columns.Count; index++)
            {
                if (row.Value(index) is { } value)
                {
                    values[_columns[index]] = value;
                }
            }

            changes.Add(new RowChange("insert", null, values));
        }

        return changes;
    }

    private async Task PreviewAsync()
    {
        try
        {
            var statements = _session.RowStatements(_schema, _table, _info!, Changes());
            await Dialogs.ConfirmDialog.ShowMessageAsync(Ui.WindowOf(this), "Diese Anweisungen laufen beim Speichern",
                statements.Count == 0 ? "Nichts geändert." : string.Join(Environment.NewLine, statements.Select(statement => statement.Preview)));
        }
        catch (BusinessException exception)
        {
            _owner.Show(exception.Message, InfoSeverity.Error);
        }
    }

    /// <summary>Saves the changes in one transaction (tests await it).</summary>
    public async Task SaveAsync()
    {
        try
        {
            var statements = _session.RowStatements(_schema, _table, _info!, Changes());
            if (statements.Count == 0)
            {
                return;
            }

            var count = await Task.Run(() => _session.ApplyRowsAsync(_database, _info!, statements, _owner.Token));
            _owner.Show($"{count} Zeilen gespeichert.", InfoSeverity.Success);
            await LoadAsync(_offset);
        }
        catch (BusinessException exception)
        {
            _owner.Show(exception.Message, InfoSeverity.Error);
        }
    }

    /// <summary>A row of the grid: its values as text ("NULL" for none), what it was, whether it is new.</summary>
    public sealed class EditRow : INotifyPropertyChanged
    {
        private readonly string?[] _values;

        public EditRow(string?[] original)
        {
            Original = original;
            _values = [.. original];
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string?[] Original { get; }

        public bool IsNew { get; init; }

        public bool IsChanged { get; private set; }

        /// <summary>The cell as the grid edits it: NULL written out.</summary>
        public string this[int index]
        {
            get => _values[index] ?? "NULL";
            set
            {
                var next = value == "NULL" ? null : value;
                if (next == _values[index])
                {
                    return;
                }

                _values[index] = next;
                IsChanged = true;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            }
        }

        public string? Value(int index) => _values[index];
    }
}
