using Avalonia;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;

namespace ThixGate.Controls;

/// <summary>
/// ThixGate's menu (in place of MatDesktop's MatNav, in its look): above the library as a tree (<see cref="ConnectionTree"/>: search,
/// "Alle Verbindungen", folders and connections), below a line the other sections (<see cref="Items"/>: Zugangsdaten, Matgate) and
/// <see cref="Settings"/>, at the foot the program's <see cref="Foot"/> (the cloud state), the link of a waiting update and the version (About)
/// with the button that folds the menu. Folded it is a rail of icons like MatNav (<see cref="RailWidth"/>): "Verbindungen", the sections,
/// Einstellungen; the tree waits until it opens again. Its open width is the main window's (a splitter at its edge).
/// </summary>
public sealed class LibraryNav : Border
{
    /// <summary>The width of the folded menu (MatNav's rail).</summary>
    public const double RailWidth = MatNav.RailWidth;

    public static readonly StyledProperty<bool> IsFoldedProperty = AvaloniaProperty.Register<LibraryNav, bool>(nameof(IsFolded));

    private readonly ContentControl _railHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top };
    private readonly StackPanel _items = new() { Spacing = 2 };
    private readonly Border _line = new() { Height = 1, Margin = new Thickness(12, 8, 12, 8) };
    private readonly ContentControl _settingsHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ContentControl _footHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _versionText = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _version = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _updateText = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _update = new() { HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button _fold = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _foot = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
    private MatNavItem? _settings;
    private string _versionNumber = "0.0.0";
    private string _channel = MatVersion.Release;
    private string? _updateVersion;
    private string? _foldShortcut;

    static LibraryNav()
    {
        IsFoldedProperty.Changed.AddClassHandler<LibraryNav>((nav, e) =>
        {
            nav.Refresh();
            if (!Equals(e.OldValue, e.NewValue))
            {
                nav.FoldChanged?.Invoke(nav, nav.IsFolded);
            }
        });
    }

    public LibraryNav()
    {
        BorderThickness = new Thickness(0, 0, 1, 0);
        this.Use(BackgroundProperty, "Mat.NavBg");
        this.Use(BorderBrushProperty, "Mat.Border");
        _line.Use(Border.BackgroundProperty, "Mat.Border");
        _version.Themed("MatButton.Link");
        _update.Themed("MatButton.Link");
        _fold.Themed("MatButton.Icon");
        _update.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new MatIcon { Icon = "download", Size = 16 }, _updateText } };
        _version.Click += (_, _) => AboutRequested?.Invoke(this, EventArgs.Empty);
        _update.Click += (_, _) => UpdateRequested?.Invoke(this, EventArgs.Empty);
        _fold.Click += (_, _) => IsFolded = !IsFolded;
        ConnectionsItem.Click += (_, _) => Invoke(ConnectionsItem);
        Grid.SetColumn(_fold, 1);
        _foot.Children.Add(_version);
        _foot.Children.Add(_fold);

        var bottom = new StackPanel { Children = { _line, _items, _settingsHost, _footHost, _update, _foot } };
        var top = new Panel { Children = { Tree, _railHost } };
        var dock = new DockPanel { Margin = new Thickness(12, 16, 12, 12) };
        dock.Children.Add(Ui.Docked(bottom, Dock.Bottom));
        dock.Children.Add(top);
        Child = dock;

        Items.CollectionChanged += (_, _) => RebuildItems();
        Refresh();
    }

    /// <summary>A section was chosen (it is the current one now).</summary>
    public event EventHandler<MatNavItem>? ItemInvoked;

    /// <summary>The version at the foot was chosen: About.</summary>
    public event EventHandler? AboutRequested;

    /// <summary>The link of the waiting update was chosen.</summary>
    public event EventHandler? UpdateRequested;

    /// <summary>The menu was folded or opened (remember it).</summary>
    public event EventHandler<bool>? FoldChanged;

    /// <summary>The library as a tree, the upper part of the open menu.</summary>
    public ConnectionTree Tree { get; } = new();

    /// <summary>"Alle Verbindungen": in the open menu between search and tree, folded the first icon of the rail.</summary>
    public MatNavItem ConnectionsItem { get; } = new("monitor", "Alle Verbindungen");

    /// <summary>The sections below the tree, in their order.</summary>
    public AvaloniaList<MatNavItem> Items { get; } = [];

    /// <summary>Einstellungen, below the sections.</summary>
    public MatNavItem? Settings
    {
        get => _settings;
        set
        {
            if (_settings is not null)
            {
                _settings.Click -= OnItemClick;
            }

            _settings = value;
            if (_settings is not null)
            {
                _settings.Click += OnItemClick;
            }

            _settingsHost.Content = _settings;
            Refresh();
        }
    }

    /// <summary>The program's own part of the foot (the cloud state).</summary>
    public Control? Foot
    {
        get => _footHost.Content as Control;
        set => _footHost.Content = value;
    }

    public string AppName { get; set; } = "";

    public string Version
    {
        get => _versionNumber;
        set
        {
            _versionNumber = value;
            Refresh();
        }
    }

    public string Channel
    {
        get => _channel;
        set
        {
            _channel = value;
            Refresh();
        }
    }

    /// <summary>The version of a waiting update, or null.</summary>
    public string? UpdateVersion
    {
        get => _updateVersion;
        set
        {
            _updateVersion = value;
            Refresh();
        }
    }

    /// <summary>The key that folds the menu, for the tool tip.</summary>
    public string? FoldShortcut
    {
        get => _foldShortcut;
        set
        {
            _foldShortcut = value;
            Refresh();
        }
    }

    public bool IsFolded
    {
        get => GetValue(IsFoldedProperty);
        set => SetValue(IsFoldedProperty, value);
    }

    public Button VersionButton => _version;

    public Button FoldButton => _fold;

    public Button UpdateButton => _update;

    /// <summary>The current item, or null (a folder of the tree, About).</summary>
    public MatNavItem? Current => AllItems().FirstOrDefault(item => item.IsCurrent);

    /// <summary>Makes <paramref name="item"/> the current item; any other than "Alle Verbindungen" leaves no folder chosen in the tree.</summary>
    public void Select(MatNavItem? item)
    {
        foreach (var each in AllItems())
        {
            each.IsCurrent = each == item;
        }

        if (item is not null)
        {
            Tree.ClearSelection();
        }
    }

    private IEnumerable<MatNavItem> AllItems() => new[] { ConnectionsItem }.Concat(Items).Append(_settings).OfType<MatNavItem>();

    private void RebuildItems()
    {
        foreach (var item in _items.Children.OfType<MatNavItem>())
        {
            item.Click -= OnItemClick;
        }

        _items.Children.Clear();
        foreach (var item in Items)
        {
            item.Click += OnItemClick;
            _items.Children.Add(item);
        }

        Refresh();
    }

    private void OnItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MatNavItem item)
        {
            Invoke(item);
        }
    }

    private void Invoke(MatNavItem item)
    {
        Select(item);
        ItemInvoked?.Invoke(this, item);
    }

    /// <summary>Open: the tree with "Alle Verbindungen" in it; folded: the rail. The foot like MatNav's.</summary>
    private void Refresh()
    {
        var folded = IsFolded;
        foreach (var item in AllItems())
        {
            item.IsFolded = folded;
        }

        // "Alle Verbindungen" moves between the tree (open) and the rail (folded).
        Tree.Above.Content = null;
        _railHost.Content = null;
        if (folded)
        {
            _railHost.Content = ConnectionsItem;
        }
        else
        {
            Tree.Above.Content = ConnectionsItem;
        }

        Tree.IsVisible = !folded;
        _railHost.IsVisible = folded;
        _line.IsVisible = Items.Count > 0 || _settings is not null;

        var version = MatVersion.Format(_versionNumber, _channel);
        var about = MatText.Format("nav.about", AppName);
        _versionText.Text = version;
        _version.Content = folded ? new MatIcon { Icon = "info", Size = 18 } : _versionText;
        ToolTip.SetTip(_version, $"{about} – {version}");
        AutomationProperties.SetName(_version, folded ? about : version);
        _version.HorizontalAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        _version.Margin = folded ? new Thickness(0) : new Thickness(8, 0, 0, 0);
        Grid.SetColumnSpan(_version, folded ? 2 : 1);

        var update = string.IsNullOrWhiteSpace(_updateVersion) ? null : _updateVersion.Trim();
        _update.IsVisible = update is not null;
        _updateText.Text = update is null ? "" : MatText.Format("nav.update", update);
        _updateText.IsVisible = !folded;
        var available = update is null ? null : MatText.Format("nav.updateAvailable", update);
        ToolTip.SetTip(_update, available);
        AutomationProperties.SetName(_update, available ?? "");
        _update.HorizontalAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        _update.Margin = folded ? new Thickness(0, 0, 0, 6) : new Thickness(8, 0, 0, 6);

        _fold.Content = new MatIcon { Icon = "sidebar", Size = 18 };
        var action = MatText.Get(folded ? "nav.unfold" : "nav.fold");
        ToolTip.SetTip(_fold, string.IsNullOrWhiteSpace(_foldShortcut) ? action : $"{action} ({_foldShortcut.Trim()})");
        AutomationProperties.SetName(_fold, action);
        Grid.SetRow(_fold, folded ? 1 : 0);
        Grid.SetColumn(_fold, folded ? 0 : 1);
        Grid.SetColumnSpan(_fold, folded ? 2 : 1);
        _fold.HorizontalAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        _fold.Margin = folded ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }
}
