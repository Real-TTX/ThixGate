using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using ThixGate.Core.Models;

namespace ThixGate.Controls;

/// <summary>
/// THE list page of a CRUD entity (owner's UI rules): title and intro, the toolbar above the list (search, filter, sort), messages, the
/// list of card rows, below it the action bar (left-aligned, positive to negative, delete with a gap) and the pager with the count.
/// Every list page of ThixGate is one of these; several on one window work (no static state).
/// </summary>
public sealed class ListLayout<T> : UserControl where T : class
{
    private readonly ListBox _list = new();
    private readonly InfoBar _message = new();
    private readonly InfoBar _policy = new();
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Pager _pager = new(hideWhenSingle: true);
    private readonly ContentControl _empty = new() { Margin = new Thickness(0, 40, 0, 0) };

    public ListLayout(string title, string intro, string searchWatermark, string listName, Func<T, Control> row)
    {
        this.Use(BackgroundProperty, "Mat.Surface");
        Toolbar = new ListToolbar(searchWatermark);
        _count.Themed("MatText.Muted");

        _list.Themed("MatList");
        _list.ItemContainerTheme = Ui.Theme("MatCardRow");
        _list.ItemTemplate = new FuncDataTemplate<T>((item, _) => item is null ? new Panel() : row(item), supportsRecycling: false);
        AutomationProperties.SetName(_list, listName);

        var introText = new TextBlock
        {
            Text = intro,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 900,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 18),
        }.Themed("MatText.Muted");

        // Below the list: the pager with the count at the right, then the actions in a row of their own (left-aligned).
        _count.HorizontalAlignment = HorizontalAlignment.Right;
        var bottom = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 0),
            Spacing = 12,
            Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Right, Children = { _pager, _count } }, Actions },
        };

        var page = new DockPanel { Margin = new Thickness(36, 28, 36, 24) };
        page.Children.Add(Ui.Docked(new TextBlock { Text = title }.Themed("MatText.PageTitle"), Dock.Top));
        page.Children.Add(Ui.Docked(introText, Dock.Top));
        page.Children.Add(Ui.Docked(Toolbar, Dock.Top));
        page.Children.Add(Ui.Docked(new StackPanel { Margin = new Thickness(0, 0, 0, 8), Children = { _policy, _message } }, Dock.Top));
        page.Children.Add(Ui.Docked(bottom, Dock.Bottom));
        page.Children.Add(new Panel { Children = { _list, _empty } });
        Content = page;

        _list.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        _list.DoubleTapped += (_, _) =>
        {
            if (_list.SelectedItem is T item)
            {
                ItemActivated?.Invoke(this, item);
            }
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && _list.SelectedItem is T item)
            {
                e.Handled = true;
                ItemActivated?.Invoke(this, item);
            }
        };
        _pager.PageChanged += (_, page) => PageChanged?.Invoke(this, page);
    }

    public event EventHandler? SelectionChanged;

    /// <summary>Double click or Enter on a row.</summary>
    public event EventHandler<T>? ItemActivated;

    /// <summary>The pager asks for another page (0-based).</summary>
    public event EventHandler<int>? PageChanged;

    public ListToolbar Toolbar { get; }

    /// <summary>The buttons below the list: positive to negative, <see cref="ActionBar.SetSeparated"/> for delete.</summary>
    public ActionBar Actions { get; } = new();

    public ListBox List => _list;

    public Pager Pager => _pager;

    public InfoBar Message => _message;

    public string CountText => _count.Text ?? "";

    public T? SelectedItem => _list.SelectedItem as T;

    /// <summary>Shows one page of items, keeps the selection where it can; the empty state when there are none.</summary>
    public void Show(IReadOnlyList<T> items, Func<T, Guid> idOf, string countText, Control emptyState, int pageIndex = 0, int pageCount = 1, int total = 0, int pageSize = 0)
    {
        var selected = SelectedItem is { } current ? idOf(current) : (Guid?)null;
        _list.ItemsSource = items;
        _list.SelectedItem = items.FirstOrDefault(item => idOf(item) == selected) ?? items.FirstOrDefault();
        _list.IsVisible = items.Count > 0;
        _empty.Content = emptyState;
        _empty.IsVisible = items.Count == 0;
        _count.Text = countText;
        _pager.Show(pageIndex, pageCount, total, pageSize);
    }

    /// <summary>The note of a policy above the list (null hides it).</summary>
    public void ShowPolicy(string? text)
    {
        if (text is null)
        {
            _policy.Hide();
        }
        else
        {
            _policy.Show(text, InfoSeverity.Info);
        }
    }

    public static string LockedNote(string what) =>
        $"Von deiner Organisation festgelegt: {what} lassen sich hier nicht anlegen oder ändern (Richtlinie LockConfiguration).";
}

/// <summary>
/// THE create/update page of an entity: head (icon tile, title, subtitle), messages, the areas as tabs, and the action bar
/// Speichern, (own actions), Zurück, gap, Löschen. <see cref="Run"/> shows a <see cref="BusinessException"/> at its field (choosing its
/// tab) or above the tabs.
/// </summary>
public sealed class EditLayout : UserControl
{
    private readonly ContentControl _tile = new() { Margin = new Thickness(0, 2, 16, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _title = new();
    private readonly TextBlock _subtitle = new() { TextWrapping = TextWrapping.Wrap };

    public EditLayout()
    {
        this.Use(BackgroundProperty, "Mat.Surface");
        _title.Themed("MatText.PageTitle");
        _subtitle.Themed("MatText.Muted");
        Save.IsDefault = true;
        ActionBar.SetSeparated(Delete, true);
        Message.Margin = new Thickness(0, 14, 0, 0);
        Tabs.Margin = new Thickness(0, 14, 0, 0);

        var head = new DockPanel
        {
            Children =
            {
                Ui.Docked(_tile, Dock.Left),
                new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { _title, _subtitle } },
            },
        };
        var page = new DockPanel { Margin = new Thickness(36, 28, 36, 24) };
        page.Children.Add(Ui.Docked(head, Dock.Top));
        page.Children.Add(Ui.Docked(new ActionBar { Margin = new Thickness(0, 16, 0, 0), Children = { Save, Extra, Back, Delete } }, Dock.Bottom));
        page.Children.Add(Ui.Docked(Message, Dock.Top));
        page.Children.Add(Tabs);
        Content = page;
    }

    public Button Save { get; } = Ui.Button("save", "Speichern", "MatButton.Primary");

    /// <summary>The page's own actions between Speichern and Zurück (e.g. "Verbinden", "Prüfen").</summary>
    public StackPanel Extra { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 8 };

    public Button Back { get; } = Ui.Button("back", "Zurück");

    public Button Delete { get; } = Ui.Button("trash", "Löschen", "MatButton.Danger");

    public InfoBar Message { get; } = new();

    public TabControl Tabs { get; } = new();

    public FormFields Fields { get; } = new();

    public string Title => _title.Text ?? "";

    /// <summary>Head of the page.</summary>
    public void SetHead(string icon, string title, string subtitle)
    {
        var tile = Ui.IconTile(icon, 52, 26);
        tile.CornerRadius = new CornerRadius(14);
        _tile.Content = tile;
        _title.Text = title;
        _subtitle.Text = subtitle;
    }

    /// <summary>A tab with a scrolling column of fields.</summary>
    public TabItem AddTab(string header, params Control[] children)
    {
        var column = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var child in children)
        {
            column.Children.Add(child);
        }

        var tab = new TabItem { Header = header, Content = new ScrollViewer { Content = column, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };
        AutomationProperties.SetName(tab, header);
        Tabs.Items.Add(tab);
        return tab;
    }

    /// <summary>The column of fields of a tab (to add fields later).</summary>
    public static StackPanel ColumnOf(TabItem tab) => (StackPanel)((ScrollViewer)tab.Content!).Content!;

    /// <summary>Runs a save/delete of the business layer: true when it went through; otherwise the error is shown.</summary>
    public bool Run(Action action)
    {
        Message.Hide();
        Fields.Clear();
        try
        {
            action();
            return true;
        }
        catch (BusinessException exception)
        {
            if (!Fields.ShowError(exception.Field, exception.Message))
            {
                Message.Show(exception.Message, InfoSeverity.Error);
            }

            return false;
        }
    }

    /// <summary>The first tab again (a page loaded for another item).</summary>
    public void Reset()
    {
        Message.Hide();
        Fields.Clear();
        if (Tabs.Items.Count > 0)
        {
            Tabs.SelectedIndex = 0;
        }
    }
}
