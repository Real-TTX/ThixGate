using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Matgate.Services.Database;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>
/// A query of the database tab (as Matgate's): the editor (Strg+Enter or F5 runs the statement - only the selection, if there is one;
/// Strg+L shows the plan), the database to run in, Ausführen / Abbrechen / Plan, Verlauf (the last 100 queries of this connection),
/// below the results - several result sets, each a grid with "Als Tabelle kopieren" and Export - and the messages (rows changed, time).
/// </summary>
public sealed class QueryDocument : UserControl
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, SF Mono, DejaVu Sans Mono, monospace");

    private readonly DatabaseView _owner;
    private readonly DatabaseSession _session;
    private readonly TextBox _editor = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, FontFamily = Mono, MinHeight = 120 };
    private readonly ComboBox _database = new() { MinWidth = 180 };
    private readonly Button _run = Ui.Button("play", "Ausführen", "MatButton.Primary");
    private readonly Button _cancel = Ui.Button("stop", "Abbrechen");
    private readonly Button _plan = Ui.Button("route", "Plan");
    private readonly Button _history = Ui.Button("clock", "Verlauf");
    private readonly StackPanel _results = new() { Spacing = 12 };
    private readonly TextBlock _messages = new() { TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? _running;

    public QueryDocument(DatabaseView owner, DatabaseSession session, string? database, string sql)
    {
        _owner = owner;
        _session = session;
        _editor.Text = sql;
        AutomationProperties.SetName(_editor, "Abfrage");
        AutomationProperties.SetName(_database, "Datenbank");
        _database.ItemsSource = owner.DatabaseNames;
        _database.SelectedItem = database ?? owner.DatabaseNames.FirstOrDefault();
        _cancel.IsEnabled = false;
        _run.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) => _running?.Cancel();
        _plan.Click += async (_, _) => await PlanAsync();
        _history.Flyout = new MenuFlyout();
        ((MenuFlyout)_history.Flyout).Opening += (_, _) => ((MenuFlyout)_history.Flyout).ItemsSource = HistoryItems();
        ToolTip.SetTip(_run, "Strg+Enter oder F5 – nur die Auswahl, wenn etwas markiert ist");
        ToolTip.SetTip(_plan, "Strg+L: wie der Server die Abfrage ausführt");
        _editor.AddHandler(KeyDownEvent, async (_, e) =>
        {
            var command = e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
            if ((e.Key == Key.Enter && command) || e.Key == Key.F5)
            {
                e.Handled = true;
                await RunAsync();
            }
            else if (e.Key == Key.L && command)
            {
                e.Handled = true;
                await PlanAsync();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(0, 10, 0, 8) };
        bar.Children.AddRange([_run, _cancel, _plan, _history, new Border { Width = 8 }, _database]);
        _messages.Themed("MatText.Muted");

        var top = new DockPanel();
        top.Children.Add(Ui.Docked(bar, Dock.Top));
        top.Children.Add(_editor);
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(Ui.Docked(_messages, Dock.Top));
        bottom.Children.Add(new ScrollViewer { Content = _results, Margin = new Thickness(0, 8, 0, 0) });

        var split = new Grid { RowDefinitions = new RowDefinitions("2*,Auto,3*") };
        split.Children.Add(Ui.Cell(top, 0, 0));
        split.Children.Add(Ui.Cell(new GridSplitter { Height = 4, ResizeDirection = GridResizeDirection.Rows, Background = null }, 1, 0));
        split.Children.Add(Ui.Cell(bottom, 2, 0));
        Content = split;
    }

    public TextBox Editor => _editor;

    /// <summary>The result grids shown (tests).</summary>
    public IReadOnlyList<DataGrid> ResultGridsShown => [.. _results.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<DataGrid>())];

    public string MessageText => _messages.Text ?? "";

    public void FocusEditor() => _editor.Focus();

    private string Statement()
    {
        var selected = _editor.SelectedText;
        return string.IsNullOrWhiteSpace(selected) ? _editor.Text ?? "" : selected;
    }

    private string? Database => _database.SelectedItem as string;

    /// <summary>Runs the statement (or the selection); the results below. Tests await it.</summary>
    public async Task RunAsync()
    {
        var sql = Statement().Trim();
        if (sql.Length == 0 || _running is not null)
        {
            return;
        }

        QueryHistory.Add(_session.Connection.Id, sql);
        await Busy(async token =>
        {
            var batch = await Task.Run(() => _session.BatchAsync(Database, sql, token), token);
            _results.Children.Clear();
            var number = 0;
            foreach (var result in batch.Results.Where(result => result.Columns.Count > 0))
            {
                number++;
                _results.Children.Add(ResultPanel(result, batch.Results.Count(item => item.Columns.Count > 0) > 1 ? $"Ergebnis {number}" : "Ergebnis", sql));
            }

            var parts = new List<string>();
            foreach (var result in batch.Results.Where(result => result.Columns.Count > 0))
            {
                parts.Add($"{result.Rows.Count:N0} Zeilen{(result.Truncated ? $" (gekürzt auf {DatabaseSession.MaxRows:N0} – Export liefert alle)" : "")}");
            }

            if (batch.Affected is { } affected)
            {
                parts.Add($"{affected:N0} Zeilen geändert");
            }

            _messages.Text = (parts.Count > 0 ? string.Join(" · ", parts) : "Ausgeführt.") + $" · {batch.ElapsedMs.ToString(CultureInfo.CurrentCulture)} ms";
            if (_session.Engine != DatabaseEngine.MySql && System.Text.RegularExpressions.Regex.IsMatch(sql, @"^\s*(CREATE|ALTER|DROP)\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && Database is { } database)
            {
                _owner.CatalogChanged(database);
            }
        });
    }

    private Control ResultPanel(DbResult result, string title, string sql)
    {
        var copy = Ui.Button("copy", "Als Tabelle kopieren");
        copy.Click += async (_, _) => await Ui.CopyTextAsync(this, ResultGrids.AsTable(result));
        var csv = Ui.Button("download", "CSV …");
        csv.Click += async (_, _) => await _owner.ExportAsync(Database, sql, "Ergebnis", json: false);
        var json = Ui.Button("download", "JSON …");
        json.Click += async (_, _) => await _owner.ExportAsync(Database, sql, "Ergebnis", json: true);
        var head = new DockPanel();
        head.Children.Add(Ui.Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { copy, csv, json } }, Dock.Right));
        head.Children.Add(Ui.Text($"{title} · {result.Rows.Count:N0} Zeilen", "MatText.SectionTitle"));
        return new StackPanel { Spacing = 6, Children = { head, ResultGrids.For(result, maxHeight: 520) } };
    }

    private async Task PlanAsync()
    {
        var sql = Statement().Trim();
        if (sql.Length == 0 || _running is not null)
        {
            return;
        }

        await Busy(async token =>
        {
            var plan = await Task.Run(() => _session.PlanAsync(Database, sql, analyze: false, token), token);
            _results.Children.Clear();
            if (plan.Table is { } table)
            {
                _results.Children.Add(new StackPanel { Spacing = 6, Children = { Ui.Text("Plan", "MatText.SectionTitle"), ResultGrids.For(table, maxHeight: 420) } });
            }

            if (plan.Nodes.Count > 0)
            {
                var tree = new TreeView { ItemsSource = plan.Nodes.Select(PlanItem).ToList() };
                _results.Children.Add(new StackPanel { Spacing = 6, Children = { Ui.Text("Plan", "MatText.SectionTitle"), tree } });
            }

            var raw = new SelectableTextBlock { Text = plan.Raw, FontFamily = Mono, TextWrapping = TextWrapping.Wrap };
            var copy = Ui.Button("copy", "Kopieren");
            copy.Click += async (_, _) => await Ui.CopyTextAsync(this, plan.Raw);
            _results.Children.Add(new Expander { Header = "Plan des Servers als Text", Content = new StackPanel { Spacing = 6, Children = { copy, raw } } });
            _messages.Text = plan.Notes.Count > 0 ? string.Join(" · ", plan.Notes) : "Plan gelesen.";
        });
    }

    private static TreeViewItem PlanItem(PlanNode node)
    {
        var parts = new List<string> { node.Operation };
        if (node.Target.Length > 0)
        {
            parts.Add(node.Target);
        }

        if (node.Rows is { } rows)
        {
            parts.Add($"≈ {rows:N0} Zeilen");
        }

        if (node.Cost is { } cost)
        {
            parts.Add($"Kosten {cost:N1}");
        }

        if (node.ActualMs is { } ms)
        {
            parts.Add($"{ms:N1} ms");
        }

        var header = string.Join(" · ", parts) + (node.Warnings.Count > 0 ? "  ⚠ " + string.Join("; ", node.Warnings) : "");
        var item = new TreeViewItem { Header = header, IsExpanded = true, ItemsSource = node.Children.Select(PlanItem).ToList() };
        ToolTip.SetTip(item, node.Detail.Length > 0 ? node.Detail : null);
        return item;
    }

    private IEnumerable<Control> HistoryItems()
    {
        var history = QueryHistory.Of(_session.Connection.Id).Take(25).ToList();
        if (history.Count == 0)
        {
            return [new MenuItem { Header = "Noch keine Abfragen", IsEnabled = false }];
        }

        return history.Select(sql =>
        {
            var line = sql.Replace('\n', ' ').Replace("\r", "", StringComparison.Ordinal);
            var item = new MenuItem { Header = line.Length > 90 ? line[..90] + " …" : line };
            ToolTip.SetTip(item, sql.Length > 90 ? sql : null);
            item.Click += (_, _) =>
            {
                _editor.Text = sql;
                _editor.Focus();
            };
            return (Control)item;
        });
    }

    private async Task Busy(Func<CancellationToken, Task> action)
    {
        _running = CancellationTokenSource.CreateLinkedTokenSource(_owner.Token);
        _run.IsEnabled = _plan.IsEnabled = false;
        _cancel.IsEnabled = true;
        _messages.Text = "Läuft …";
        _messages.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
        try
        {
            await action(_running.Token);
        }
        catch (OperationCanceledException)
        {
            _messages.Text = "Abgebrochen.";
        }
        catch (BusinessException exception)
        {
            _messages.Text = exception.Message;
            _messages.Use(TextBlock.ForegroundProperty, "Mat.DangerText");
        }
        finally
        {
            _running.Dispose();
            _running = null;
            _run.IsEnabled = _plan.IsEnabled = true;
            _cancel.IsEnabled = false;
        }
    }
}
