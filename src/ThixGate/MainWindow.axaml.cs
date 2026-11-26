using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Platform;
using ThixGate.Pages;
using ThixGate.Sessions;

namespace ThixGate;

/// <summary>
/// The main window: Verbindungen, Ordner, Zugangsdaten, Matgate, Einstellungen and About in one window (DESK-03/04). Forms open on top of a
/// section and "Zurück" (or Esc) returns to where they were opened. Closing the window only hides it: ThixGate keeps running in the
/// tray / menu bar (sessions stay open). The menu is the library as a tree (<see cref="LibraryNav"/>: a folder shows its connections) with
/// the other sections below it. Keys: Ctrl/Cmd+1…4 the sections (2: the folder page), +, the settings, +B fold the menu, +N a new
/// connection, +F the search, Esc back.
/// </summary>
public partial class MainWindow : MatWindow
{
    private readonly ConnectionsPage _connections = new();
    private readonly Lazy<FoldersPage> _folders = new(() => new FoldersPage());
    private readonly Lazy<CredentialsPage> _credentials = new(() => new CredentialsPage());
    private readonly Lazy<MatgatePage> _matgate = new(() => new MatgatePage());
    private readonly Lazy<SettingsPage> _settings = new(() => new SettingsPage());
    private readonly Lazy<AboutPage> _about = new(() => new AboutPage());
    private readonly ConnectionForm _connectionForm = new();
    private readonly FolderForm _folderForm = new();
    private readonly CredentialForm _credentialForm = new();
    private readonly MatgateForm _matgateForm = new();
    private readonly MatgateConnectionForm _matgateConnectionForm = new();
    private readonly Stack<(Control Page, MatNavItem? Nav)> _history = new();
    private readonly DispatcherTimer _sizeTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly MatNavItem _navConnections;
    private readonly MatNavItem _navCredentials;
    private readonly MatNavItem _navMatgate;
    private readonly MatNavItem _navSettings;
    private readonly Dictionary<SessionView, Button> _sessionTabs = [];
    private readonly Button _pageTab;
    private SessionView? _shownSession;

    public MainWindow()
    {
        InitializeComponent();
        UseOwnTitleBar();
        var settings = SettingsService.Get();
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;

        TitleBar.Logo = Ui.Logo;
        TitleBar.CloseRequested += (_, e) =>
        {
            e.Handled = true;
            Hide();
        };

        var command = Ui.CommandKey.Name;
        _navConnections = Sidebar.ConnectionsItem;
        _navConnections.Shortcut = $"{command}+1";
        _navConnections.IsCurrent = true;
        _navCredentials = new MatNavItem("key", "Zugangsdaten", $"{command}+3");
        _navMatgate = new MatNavItem("server", "Matgate", $"{command}+4");
        _navSettings = new MatNavItem("settings", "Einstellungen", $"{command}+,");
        Sidebar.Items.AddRange([_navCredentials, _navMatgate]);
        Sidebar.Settings = _navSettings;
        Sidebar.Version = AppInfo.Version;
        Sidebar.Channel = AppInfo.Channel;
        Sidebar.IsFolded = settings.IsSidebarCollapsed;
        Sidebar.Tree.FolderChosen += (_, folderId) => ShowConnections(folderId);
        Sidebar.FoldShortcut = $"{command}+B";
        Sidebar.ItemInvoked += (_, item) => OnNavInvoked(item);
        Sidebar.AboutRequested += (_, _) => ShowAbout();
        Sidebar.UpdateRequested += (_, _) => ShowAbout();
        Sidebar.FoldChanged += (_, folded) =>
        {
            SettingsService.Remember(current => current with { IsSidebarCollapsed = folded });
            ApplySidebar();
        };
        AddToMenuFoot();
        TreeSplitter.DragDelta += (_, _) => TitleBar.BrandWidth = BodyGrid.ColumnDefinitions[0].ActualWidth;
        TreeSplitter.DragCompleted += (_, _) => SettingsService.Remember(current => current with { TreeWidth = BodyGrid.ColumnDefinitions[0].ActualWidth });
        PageHost.Content = _connections;

        _sizeTimer.Tick += (_, _) =>
        {
            _sizeTimer.Stop();
            if (WindowState == WindowState.Normal)
            {
                SettingsService.Remember(current => current with { WindowWidth = Width, WindowHeight = Height });
            }
        };
        Resized += (_, _) =>
        {
            _sizeTimer.Stop();
            _sizeTimer.Start();
        };

        _connectionForm.Closed = message => Back(message, _connections.ShowMessage);
        _folderForm.Closed = message => Back(message, (text, severity) => _folders.Value.ShowMessage(text, severity));
        _credentialForm.Closed = message => Back(message, (text, severity) => _credentials.Value.ShowMessage(text, severity));
        _matgateForm.Closed = message => Back(message, (text, severity) => _matgate.Value.ShowMessage(text, severity));
        _matgateConnectionForm.Closed = message => Back(message, (text, severity) =>
        {
            // Opened from the Matgate form or from the tree of the menu: the message goes where it came back to.
            if (PageHost.Content == _matgateForm)
            {
                _matgateForm.ShowLibraryMessage(text, severity);
            }
            else if (PageHost.Content == _connections)
            {
                _connections.ShowMessage(text, severity);
            }
        });

        SessionStatus.Click += (_, _) =>
        {
            if (SessionHub.OpenSessions.FirstOrDefault() is { } first)
            {
                SessionHub.Show(first);
            }
        };
        _pageTab = PageTab();
        Tabs.Children.Add(_pageTab);
        SessionHub.Main = this;
        SessionHub.Changed += (_, _) => Ui.Post(RefreshSessions);
        Closed += (_, _) =>
        {
            if (SessionHub.Main == this)
            {
                SessionHub.Main = null;
            }
        };
        _sessionTimer.Tick += (_, _) => RefreshSessions();
        _sessionTimer.Start();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        UpdateCoordinator.Checked += (_, _) => Ui.Post(ApplySidebar);
        CloudSyncService.StatusChanged += (_, _) => Ui.Post(RefreshCloudStatus);
        SettingsService.Changed += (_, _) => Ui.Post(() =>
        {
            RefreshCloudStatus();
            ApplyMenuWidth();
        });
        ApplySidebar();
    }

    /// <summary>Set by App when the application ends: then the window really closes.</summary>
    public bool IsExiting { get; set; }

    public bool IsSidebarCollapsed => Sidebar.IsFolded;

    public Button SidebarToggle => Sidebar.FoldButton;

    /// <summary>The version at the foot of the menu (<c>Version 1.0.0 · release</c>, SETUP-07): it opens About.</summary>
    public Button VersionButton => Sidebar.VersionButton;

    public TextBlock CloudText { get; private set; } = null!;

    public ConnectionsPage ConnectionsPage => _connections;

    public FoldersPage FoldersPage => _folders.Value;

    public CredentialsPage CredentialsPage => _credentials.Value;

    public MatgatePage MatgatePage => _matgate.Value;

    public SettingsPage SettingsPage => _settings.Value;

    public ConnectionForm ConnectionForm => _connectionForm;

    public FolderForm FolderForm => _folderForm;

    public CredentialForm CredentialForm => _credentialForm;

    public MatgateForm MatgateForm => _matgateForm;

    public MatgateConnectionForm MatgateConnectionForm => _matgateConnectionForm;

    public object? CurrentPage => PageHost.Content;

    private Button CloudStatus { get; set; } = null!;

    private Ellipse CloudDot { get; set; } = null!;

    public void ToggleSidebar() => Sidebar.IsFolded = !Sidebar.IsFolded;

    /// <summary>The library as a tree, the upper part of the menu.</summary>
    public ConnectionTree LibraryTree => Sidebar.Tree;

    public LibraryNav Menu => Sidebar;

    /// <summary>Tray, second start, Dock: show the window in front.</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        WindowActivation.BringToFront(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
    }

    // ---------------------------------------------------------------------------------------------------------------- sections

    /// <summary>"Alle Verbindungen", or the connections of one folder (the folder is the chosen one in the tree).</summary>
    public void ShowConnections(Guid? folderId = null)
    {
        if (folderId is not { } folder)
        {
            ShowSection(_navConnections, _connections);
            _connections.ShowAll();
            return;
        }

        ShowSection(null, _connections);
        _connections.ShowFolder(folder);
        Sidebar.Tree.Choose(folder);
    }

    /// <summary>The folder page (Ctrl/Cmd+2; the folders live in the tree of the menu).</summary>
    public void ShowFolders() => ShowSection(null, _folders.Value);

    public void ShowCredentials() => ShowSection(_navCredentials, _credentials.Value);

    public void ShowMatgate() => ShowSection(_navMatgate, _matgate.Value);

    public void ShowSettings(string? tab = null)
    {
        ShowSection(_navSettings, _settings.Value);
        if (tab is not null)
        {
            _settings.Value.SelectTab(tab);
        }
    }

    public void ShowAbout() => ShowSection(null, _about.Value);

    // ---------------------------------------------------------------------------------------------------------------- pages on top

    /// <summary>The form of a connection (null: a new one, in <paramref name="folderId"/>), on top of the page shown now.</summary>
    public void ShowConnectionForm(Guid? connectionId, Guid? folderId = null)
    {
        _connectionForm.Load(connectionId, folderId);
        Push(_connectionForm);
        if (connectionId is { } id)
        {
            Sidebar.Tree.Reveal(id);
        }
    }

    public void ShowFolderForm(Guid? folderId, Guid? parentId = null)
    {
        _folderForm.Load(folderId, parentId);
        Push(_folderForm);
    }

    public void ShowCredentialForm(Guid? credentialId)
    {
        _credentialForm.Load(credentialId);
        Push(_credentialForm);
    }

    public void ShowMatgateForm(Guid? accountId)
    {
        _matgateForm.Load(accountId);
        Push(_matgateForm);
    }

    /// <summary>A connection IN a Matgate (null: a new one there), opened from the Matgate form's tab "Verbindungen".</summary>
    public void ShowMatgateConnectionForm(Guid accountId, Guid? matgateConnectionId)
    {
        _matgateConnectionForm.Load(accountId, matgateConnectionId);
        Push(_matgateConnectionForm);
    }

    /// <summary>Back to the page a form was opened from (the start page when there is none).</summary>
    public void GoBack()
    {
        if (_history.Count == 0)
        {
            ShowConnections();
            return;
        }

        var (page, nav) = _history.Pop();
        Sidebar.Select(nav);
        PageHost.Content = page;
        ShowPage();
    }

    private void Back(string? message, Action<string, InfoSeverity> show)
    {
        GoBack();
        if (message is not null)
        {
            show(message, InfoSeverity.Success);
        }
    }

    private void ShowSection(MatNavItem? nav, Control page)
    {
        _history.Clear();
        Sidebar.Select(nav);
        PageHost.Content = page;
        ShowPage();
    }

    private void Push(Control page)
    {
        if (PageHost.Content is Control current && current != page)
        {
            _history.Push((current, Sidebar.Current));
        }

        PageHost.Content = page;
        ShowPage();
    }

    // ---------------------------------------------------------------------------------------------------------------- session tabs

    /// <summary>The session shown now (its tab is the chosen one), or null: the page.</summary>
    public SessionView? ShownSession => _shownSession;

    /// <summary>The tabs above the page: the page first, then one per docked session (tests).</summary>
    public IReadOnlyList<Button> SessionTabs => [.. Tabs.Children.OfType<Button>()];

    /// <summary>A session comes as a tab right of the menu (<see cref="SessionHub"/>) and is shown.</summary>
    public void AddSessionTab(SessionView view)
    {
        if (_sessionTabs.ContainsKey(view))
        {
            ShowSession(view);
            return;
        }

        view.IsVisible = false;
        Body.Children.Add(view);
        var tab = SessionTab(view);
        _sessionTabs[view] = tab;
        Tabs.Children.Add(tab);
        view.StateChanged += OnSessionStateChanged;
        ShowSession(view);
    }

    /// <summary>A session leaves its tab (closed, or into a window of its own).</summary>
    public void RemoveSessionTab(SessionView view)
    {
        if (!_sessionTabs.Remove(view, out var tab))
        {
            return;
        }

        view.StateChanged -= OnSessionStateChanged;
        Tabs.Children.Remove(tab);
        Body.Children.Remove(view);
        if (_shownSession == view)
        {
            ShowPage();
        }
        else
        {
            RefreshTabs();
        }
    }

    /// <summary>Shows a docked session (its tab chosen); the keyboard goes into it.</summary>
    public void ShowSession(SessionView view)
    {
        if (!_sessionTabs.ContainsKey(view))
        {
            return;
        }

        _shownSession = view;
        PageHost.IsVisible = false;
        foreach (var each in _sessionTabs.Keys)
        {
            each.IsVisible = each == view;
        }

        RefreshTabs();
        Dispatcher.UIThread.Post(view.FocusScreen);
    }

    /// <summary>Shows the page again (the first tab); the sessions go on behind it.</summary>
    public void ShowPage()
    {
        _shownSession = null;
        PageHost.IsVisible = true;
        foreach (var each in _sessionTabs.Keys)
        {
            each.IsVisible = false;
        }

        RefreshTabs();
    }

    private void OnSessionStateChanged(object? sender, EventArgs e) => Ui.Post(RefreshTabs);

    /// <summary>The strip shows only while sessions are docked; the chosen tab carries the tint of the accent.</summary>
    private void RefreshTabs()
    {
        TabStrip.IsVisible = _sessionTabs.Count > 0;
        Mark(_pageTab, _shownSession is null);
        foreach (var (view, tab) in _sessionTabs)
        {
            Mark(tab, view == _shownSession);
            if (tab.Tag is Ellipse dot)
            {
                dot.Use(Shape.FillProperty, view.IsConnected ? "Mat.Success" : "Mat.TextFaint");
            }

            ToolTip.SetTip(tab, $"{view.Connection.Name} – {view.StateText}");
        }

        static void Mark(Button tab, bool chosen)
        {
            if (chosen)
            {
                tab.Use(BackgroundProperty, "Mat.AccentSoft");
            }
            else
            {
                tab.ClearValue(BackgroundProperty);
            }

            tab.FontWeight = chosen ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    /// <summary>The tab of the page: what the menu shows.</summary>
    private Button PageTab()
    {
        var tab = new Button { Padding = new Thickness(10, 6), Content = new IconLabel { Icon = "home", Text = "Übersicht" } }.Themed("MatButton.Ghost");
        AutomationProperties.SetName(tab, "Übersicht");
        tab.Click += (_, _) => ShowPage();
        return tab;
    }

    /// <summary>A tab of a session: protocol, name, a dot for connected, the X that closes it (also the middle mouse button).</summary>
    private Button SessionTab(SessionView view)
    {
        var dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button { Padding = new Thickness(3), Content = new MatIcon { Icon = "x", Size = 12 } }.Themed("MatButton.Icon");
        AutomationProperties.SetName(close, $"{view.Connection.Name} schließen");
        ToolTip.SetTip(close, "Sitzung schließen");
        close.Click += async (_, e) =>
        {
            e.Handled = true;
            await SessionHub.CloseAsync(view);
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new MatIcon { Icon = Protocols.Of(view.Connection.Protocol).Icon, Size = 16, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = view.Connection.Name, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
                dot,
                close,
            },
        };
        var tab = new Button { Padding = new Thickness(10, 4, 4, 4), Content = content, Tag = dot }.Themed("MatButton.Ghost");
        AutomationProperties.SetName(tab, view.Connection.Name);
        tab.Click += (_, _) => ShowSession(view);
        tab.PointerReleased += async (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Middle)
            {
                e.Handled = true;
                await SessionHub.CloseAsync(view);
            }
        };
        return tab;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The X (and Alt+F4) only hides: ThixGate keeps running in the tray / menu bar.
        if (!IsExiting)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // A session shown in its tab gets every key (Ctrl+1, Esc … belong to the remote computer then).
        if (_shownSession is { } session && session.IsKeyboardFocusWithin)
        {
            return;
        }

        var withCommand = e.KeyModifiers.HasFlag(Ui.CommandKey.Modifier);
        switch (e.Key)
        {
            case Key.D1 when withCommand:
                e.Handled = true;
                ShowConnections();
                break;
            case Key.D2 when withCommand:
                e.Handled = true;
                ShowFolders();
                break;
            case Key.D3 when withCommand:
                e.Handled = true;
                ShowCredentials();
                break;
            case Key.D4 when withCommand:
                e.Handled = true;
                ShowMatgate();
                break;
            case Key.OemComma when withCommand:
                e.Handled = true;
                ShowSettings();
                break;
            case Key.B when withCommand:
                e.Handled = true;
                ToggleSidebar();
                break;
            case Key.N when withCommand && !AccessPolicy.IsConfigurationLocked:
                e.Handled = true;
                ShowConnectionForm(null);
                break;
            case Key.F when withCommand && PageHost.Content == _connections:
                e.Handled = true;
                _connections.SearchBox.Focus();
                break;
            case Key.Escape when OwnedWindows.Count == 0 && (_history.Count > 0 || PageHost.Content != _connections):
                e.Handled = true;
                GoBack();
                break;
        }
    }

    private void OnNavInvoked(MatNavItem item)
    {
        if (item == _navSettings)
        {
            ShowSettings();
        }
        else if (item == _navCredentials)
        {
            ShowCredentials();
        }
        else if (item == _navMatgate)
        {
            ShowMatgate();
        }
        else
        {
            ShowConnections();
        }
    }

    /// <summary>The open sessions in the middle of the title bar ("2 Sitzungen offen").</summary>
    private void RefreshSessions()
    {
        var open = SessionHub.OpenSessions;
        SessionStatus.IsVisible = open.Count > 0;
        if (open.Count == 0)
        {
            return;
        }

        SessionText.Text = open.Count == 1 ? $"{open[0].Connection.Name} · {open[0].StateText}" : $"{open.Count} Sitzungen offen";
        ToolTip.SetTip(SessionStatus, string.Join("\n", open.Select(view => $"{view.Connection.Name} – {view.StateText}")));
    }

    /// <summary>The cloud state at the foot of the menu (above the version); a waiting update is MatNav's own link.</summary>
    private void AddToMenuFoot()
    {
        CloudDot = new Ellipse { Width = 8, Height = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -1) };
        CloudText = new TextBlock { FontSize = MatTokens.Type.FsSm, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 };
        CloudStatus = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                Children = { new Panel { Children = { new MatIcon { Icon = "cloud", Size = 18 }, CloudDot } }, CloudText },
            },
        }.Themed("MatButton.Ghost");
        CloudStatus.Click += (_, _) => ShowSettings(SettingsPage.CloudTab);
        Sidebar.Foot = new StackPanel { Spacing = 10, Margin = new Thickness(0, 12, 0, 10), Children = { CloudStatus } };
    }

    /// <summary>The width of the menu: the remembered one while it is open (the splitter at its edge changes it), the rail folded; the brand of the title bar follows.</summary>
    private void ApplyMenuWidth()
    {
        var folded = IsSidebarCollapsed;
        var width = folded ? LibraryNav.RailWidth : SettingsService.Get().TreeWidth;
        var column = BodyGrid.ColumnDefinitions[0];
        column.MinWidth = folded ? LibraryNav.RailWidth : SettingsService.TreeMinWidth;
        column.MaxWidth = folded ? LibraryNav.RailWidth : SettingsService.TreeMaxWidth;
        column.Width = new GridLength(width);
        TreeSplitter.IsVisible = !folded;
        TitleBar.BrandWidth = width;
        TitleBar.IsNameVisible = !folded || OperatingSystem.IsMacOS();
    }

    private void ApplySidebar()
    {
        var folded = IsSidebarCollapsed;
        ApplyMenuWidth();
        CloudText.IsVisible = !folded;
        CloudStatus.HorizontalContentAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        Sidebar.UpdateVersion = UpdateService.LastResult is { IsUpdateAvailable: true, Manifest: { } manifest } ? manifest.Version : null;
        RefreshCloudStatus();
    }

    private void RefreshCloudStatus()
    {
        var cloud = SettingsService.Get().Cloud;
        var status = CloudSyncService.Status;
        (CloudText.Text, var dot) = status.State switch
        {
            CloudState.Online => ($"Abgeglichen · {cloud.UserName}", "Mat.Success"),
            CloudState.Syncing => ("Gleiche ab …", "Mat.Accent"),
            CloudState.Connecting => ("Verbinde …", "Mat.Accent"),
            CloudState.Offline => ("ThixCloud nicht erreichbar", "Mat.Warning"),
            CloudState.SignedOut => ("Abgemeldet – neu anmelden", "Mat.Danger"),
            _ => cloud.IsConnected ? ("ThixCloud verbunden", "Mat.Success") : ("Mit ThixCloud verbinden", (string?)null),
        };
        CloudDot.IsVisible = dot is not null;
        if (dot is not null)
        {
            CloudDot.Use(Shape.FillProperty, dot);
        }

        var tip = status.LastSync is { } last ? $"{status.Message} (zuletzt {last.ToLocalTime():HH:mm:ss})" : status.Message;
        ToolTip.SetTip(CloudStatus, IsSidebarCollapsed ? $"{CloudText.Text}\n{tip}" : tip);
        AutomationProperties.SetName(CloudStatus, CloudText.Text);
    }
}
