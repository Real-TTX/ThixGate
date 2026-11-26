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
/// tray / menu bar (sessions stay open). Keys: Ctrl/Cmd+1…4 the sections, +, the settings, +B fold the menu, +N a new connection,
/// +F the search, Esc back.
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
    private readonly MatNavItem _navFolders;
    private readonly MatNavItem _navCredentials;
    private readonly MatNavItem _navMatgate;
    private readonly MatNavItem _navSettings;

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
        _navConnections = new MatNavItem("monitor", "Verbindungen", $"{command}+1") { IsCurrent = true };
        _navFolders = new MatNavItem("folder", "Ordner", $"{command}+2");
        _navCredentials = new MatNavItem("key", "Zugangsdaten", $"{command}+3");
        _navMatgate = new MatNavItem("server", "Matgate", $"{command}+4");
        _navSettings = new MatNavItem("settings", "Einstellungen", $"{command}+,");
        Sidebar.Items.AddRange([_navConnections, _navFolders, _navCredentials, _navMatgate]);
        Sidebar.Settings = _navSettings;
        Sidebar.Version = AppInfo.Version;
        Sidebar.Channel = AppInfo.Channel;
        Sidebar.TitleBar = TitleBar;
        Sidebar.IsFolded = settings.IsSidebarCollapsed;
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
        _matgateConnectionForm.Closed = message => Back(message, _matgateForm.ShowLibraryMessage);

        SessionStatus.Click += (_, _) => SessionWindow.OpenSessions.FirstOrDefault()?.Activate();
        _sessionTimer.Tick += (_, _) => RefreshSessions();
        _sessionTimer.Start();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        UpdateCoordinator.Checked += (_, _) => Ui.Post(ApplySidebar);
        CloudSyncService.StatusChanged += (_, _) => Ui.Post(RefreshCloudStatus);
        SettingsService.Changed += (_, _) => Ui.Post(RefreshCloudStatus);
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

    /// <summary>"Verbindungen" (optionally of one folder).</summary>
    public void ShowConnections(Guid? folderId = null)
    {
        ShowSection(_navConnections, _connections);
        if (folderId is not null)
        {
            _connections.ShowFolder(folderId.Value);
        }
    }

    public void ShowFolders() => ShowSection(_navFolders, _folders.Value);

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
    }

    public void ShowFolderForm(Guid? folderId)
    {
        _folderForm.Load(folderId);
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
    }

    private void Push(Control page)
    {
        if (PageHost.Content is Control current && current != page)
        {
            _history.Push((current, Sidebar.Current));
        }

        PageHost.Content = page;
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
        else if (item == _navFolders)
        {
            ShowFolders();
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
        var open = SessionWindow.OpenSessions;
        SessionStatus.IsVisible = open.Count > 0;
        if (open.Count == 0)
        {
            return;
        }

        SessionText.Text = open.Count == 1 ? $"{open[0].Connection.Name} · {open[0].StateText}" : $"{open.Count} Sitzungen offen";
        ToolTip.SetTip(SessionStatus, string.Join("\n", open.Select(window => $"{window.Connection.Name} – {window.StateText}")));
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

    private void ApplySidebar()
    {
        var folded = IsSidebarCollapsed;
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
