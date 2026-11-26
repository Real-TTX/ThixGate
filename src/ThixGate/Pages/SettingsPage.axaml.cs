using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MatCloud.Contract;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;
using ThixGate.Dialogs;

namespace ThixGate.Pages;

/// <summary>
/// The settings of this computer in tabs. Every change goes through the business layer (SettingsService, AutostartService,
/// CloudSyncService, UpdateService), which checks values and policies; an error is shown at its field or above the
/// tabs. A configuration locked by the organisation (<see cref="AccessPolicy.IsConfigurationLocked"/>) is shown read-only.
/// </summary>
public partial class SettingsPage : UserControl
{
    public const string GeneralTab = "general";
    public const string SessionsTab = "sessions";
    public const string CloudTab = "cloud";
    public const string UpdatesTab = "updates";
    public const string AdvancedTab = "advanced";

    private bool _isLoading;
    private UpdateManifest? _availableUpdate;
    private CancellationTokenSource? _browserSignIn;
    private string? _browserAddress;

    public SettingsPage()
    {
        InitializeComponent();
        AutostartSwitch.IsCheckedChanged += (_, _) => Apply(() =>
        {
            if (AutostartSwitch.IsChecked == true)
            {
                AutostartService.Enable(App.ExecutablePath);
            }
            else
            {
                AutostartService.Disable();
            }
        });
        ThemeChoice.SelectionChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { Theme = (ThemeChoice)Math.Max(0, ThemeChoice.SelectedIndex) }));
        OpenInChoice.SelectionChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { OpenSessionIn = (OpenSessionIn)Math.Max(0, OpenInChoice.SelectedIndex) }));
        TreeRow.Description = $"Ordner und Verbindungen als Baum in einer eigenen Spalte, auf jeder Seite in Sicht. Ein- und ausblenden auch mit {Ui.CommandKey.Name}+L.";
        TreeSwitch.IsCheckedChanged += (_, _) =>
        {
            if (TreeSwitch.IsChecked is { } shown && shown == SettingsService.Get().IsTreeHidden)
            {
                SettingsService.Remember(settings => settings with { IsTreeHidden = !shown });
            }
        };
        ConfirmCloseSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { ConfirmCloseSession = ConfirmCloseSwitch.IsChecked == true }));
        SshTerminalBox.LostFocus += (_, _) => Apply(() => SettingsService.Update(settings => settings with { SshTerminal = (SshTerminalBox.Text ?? "").Trim() }));
        CheckUpdatesSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { CheckForUpdates = CheckUpdatesSwitch.IsChecked == true }));
        AutoInstallSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { InstallUpdatesAutomatically = AutoInstallSwitch.IsChecked == true }));

        UpdateCoordinator.Checked += (_, result) => ShowUpdateResult(result);
        Ui.WhileShown(this, Subscribe, Unsubscribe, Load);
        Load();
    }

    /// <summary>A sign-in in the browser is waiting for its confirmation (the main window stays open meanwhile).</summary>
    public bool IsSigningInInBrowser => _browserSignIn is not null;

    public void SelectTab(string tab) => Tabs.SelectedItem = tab switch
    {
        SessionsTab => SessionsTabItem,
        CloudTab => CloudTabItem,
        UpdatesTab => UpdatesTabItem,
        AdvancedTab => AdvancedTabItem,
        _ => GeneralTabItem,
    };

    private void Subscribe()
    {
        SettingsService.Changed += OnSettingsChanged;
        CloudSyncService.StatusChanged += OnCloudStatus;
    }

    private void Unsubscribe()
    {
        SettingsService.Changed -= OnSettingsChanged;
        CloudSyncService.StatusChanged -= OnCloudStatus;
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => Ui.Post(Load);

    private void OnCloudStatus(object? sender, SyncStatus status) => Ui.Post(LoadCloud);

    /// <summary>Shows the stored settings (again after every change, also from the tray or the sidebar).</summary>
    private void Load()
    {
        _isLoading = true;
        try
        {
            var settings = SettingsService.Get();
            var locked = AccessPolicy.IsConfigurationLocked;
            if (locked)
            {
                PolicyNote.Show("Von deiner Organisation festgelegt: die Einstellungen sind gesperrt (Richtlinie LockConfiguration). Du kannst sie ansehen, aber nicht ändern.", InfoSeverity.Info);
            }
            else if (AccessPolicy.IsCloudDisabled || AccessPolicy.AreUpdatesDisabled || AccessPolicy.IsMatgateDisabled || AccessPolicy.ArePasswordsDisabled)
            {
                PolicyNote.Show("Von deiner Organisation festgelegt: was eine Richtlinie bestimmt, ist hier ausgeschaltet.", InfoSeverity.Info);
            }
            else
            {
                PolicyNote.Hide();
            }

            AutostartSwitch.IsChecked = AutostartService.IsEnabled();
            AutostartSwitch.IsEnabled = AutostartService.IsSupported && !locked;
            ThemeChoice.SelectedIndex = (int)settings.Theme;
            OpenInChoice.SelectedIndex = (int)settings.OpenSessionIn;
            ConfirmCloseSwitch.IsChecked = settings.ConfirmCloseSession;
            TreeSwitch.IsChecked = !settings.IsTreeHidden;
            SshTerminalBox.Text = settings.SshTerminal;
            CheckUpdatesSwitch.IsChecked = settings.CheckForUpdates;
            AutoInstallSwitch.IsChecked = settings.InstallUpdatesAutomatically;
            foreach (var control in new Control[]
                     {
                         ThemeChoice, OpenInChoice, ConfirmCloseSwitch, SshTerminalBox, ResetButton,
                     })
            {
                control.IsEnabled = !locked;
            }

            InstalledVersionText.Text = MatVersion.Value(AppInfo.Version, AppInfo.Channel);
            ChannelText.Text = AppInfo.Channel switch
            {
                "release" => "Release (Branch main)",
                "nightly" => "Nightly (Branch dev)",
                _ => "Lokaler Build – keine Updates",
            };
            LastCheckText.Text = UpdateService.LastCheck is { } last ? Formats.When(last) : "–";
            CheckUpdateButton.IsEnabled = UpdateService.CanUpdate;
            CheckUpdatesSwitch.IsEnabled = AutoInstallSwitch.IsEnabled = UpdateService.CanUpdate && !locked;

            DataFolderRow.Description = AppPaths.DataDirectory;
            LoadCloud();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void LoadCloud()
    {
        var cloud = SettingsService.Get().Cloud;
        var status = CloudSyncService.Status;
        CloudLogin.IsVisible = !cloud.IsConnected;
        CloudConnected.IsVisible = cloud.IsConnected;
        CloudLogin.IsEnabled = !AccessPolicy.IsCloudDisabled;
        if (AccessPolicy.IsCloudDisabled)
        {
            CloudMessage.Show("Von deiner Organisation festgelegt: der Abgleich mit der ThixCloud ist ausgeschaltet (Richtlinie DisableCloud).", InfoSeverity.Warning);
        }
        else if (status.State == CloudState.SignedOut)
        {
            CloudMessage.Show(status.Message, InfoSeverity.Warning);
        }

        if (!cloud.IsConnected)
        {
            if (string.IsNullOrEmpty(ServerUrlBox.Text))
            {
                ServerUrlBox.Text = cloud.ServerUrl;
            }

            if (string.IsNullOrEmpty(UserNameBox.Text))
            {
                UserNameBox.Text = cloud.UserName;
            }

            if (string.IsNullOrEmpty(DeviceNameBox.Text))
            {
                DeviceNameBox.Text = cloud.DeviceName.Length > 0 ? cloud.DeviceName : AppInfo.MachineName;
            }

            return;
        }

        CloudServerText.Text = cloud.ServerUrl;
        CloudAccountText.Text = cloud.DisplayName.Length > 0 && cloud.DisplayName != cloud.UserName ? $"{cloud.DisplayName} ({cloud.UserName})" : cloud.UserName;
        CloudDeviceText.Text = cloud.DeviceName;
        CloudStateText.Text = status.Message;
        CloudLastSyncText.Text = status.LastSync is { } last ? Formats.When(last) : "–";
    }

    private void Apply(Action change)
    {
        if (_isLoading)
        {
            return;
        }

        try
        {
            FormErrors.ShowError(this, null, "");
            PageMessage.Hide();
            change();
        }
        catch (BusinessException exception)
        {
            ShowError(exception.Message, exception.Field);
            Load();
        }
    }

    private void ShowError(string message, string? field)
    {
        if (!FormErrors.ShowError(this, field, message))
        {
            PageMessage.Show(message, InfoSeverity.Error);
        }
    }

    private async void OnResetClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(this), "Einstellungen zurücksetzen?",
                "Erscheinungsbild, Sitzungen und Updates gehen auf den Anfang. Verbindungen, Zugangsdaten, ThixCloud und Matgate bleiben.",
                "Zurücksetzen", isDestructive: true))
        {
            return;
        }

        Apply(() =>
        {
            SettingsService.Update(_ => new AppSettings());
            PageMessage.Show("Die Einstellungen sind zurückgesetzt.", InfoSeverity.Success);
        });
    }

    private void OnOpenDataFolderClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal(AppPaths.DataDirectory);

    private void OnOpenLogFolderClick(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Ui.OpenExternal(AppPaths.LogDirectory);
    }

    // ---------------------------------------------------------------------------------------------------------------- Cloud

    /// <summary>The first way: MatCloud shows a code, the browser opens, the person confirms it there; ThixGate waits (Abbrechen ends it).</summary>
    private async void OnBrowserConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_browserSignIn is not null)
        {
            return;
        }

        CloudMessage.Hide();
        FormErrors.ShowError(this, null, "");
        _browserSignIn = new CancellationTokenSource();
        ServerUrlBox.IsEnabled = DeviceNameBox.IsEnabled = BrowserConnectButton.IsEnabled = ShowPasswordSignInButton.IsEnabled = false;
        try
        {
            if (await CloudSyncService.ConnectInBrowserAsync(ServerUrlBox.Text ?? "", DeviceNameBox.Text ?? "", start => Ui.Post(() => ShowBrowserCode(start)),
                    Ui.OpenExternal, _browserSignIn.Token))
            {
                CloudMessage.Show("Verbunden. Deine Ordner, Verbindungen und Zugangsdaten werden jetzt mit der ThixCloud abgeglichen (ohne Passwörter).", InfoSeverity.Success);
            }
        }
        catch (BusinessException exception)
        {
            if (!FormErrors.ShowError(this, exception.Field, exception.Message))
            {
                CloudMessage.Show(exception.Message, InfoSeverity.Error);
            }
        }
        finally
        {
            _browserSignIn.Dispose();
            _browserSignIn = null;
            ServerUrlBox.IsEnabled = DeviceNameBox.IsEnabled = BrowserConnectButton.IsEnabled = ShowPasswordSignInButton.IsEnabled = true;
            ShowSignInWay(password: false);
            LoadCloud();
        }
    }

    /// <summary>The code of a started sign-in in the browser, big, with the hint to confirm it there.</summary>
    internal void ShowBrowserCode(DeviceLinkStart start)
    {
        _browserAddress = start.VerificationUrl;
        BrowserCodeText.Text = start.Code;
        BrowserSignIn.IsVisible = PasswordSignIn.IsVisible = false;
        BrowserCode.IsVisible = true;
        FocusAfterLayout(CancelLinkButton);
    }

    /// <summary>The way to sign in: in the browser (first) or with user name and password (second).</summary>
    internal void ShowSignInWay(bool password)
    {
        BrowserCode.IsVisible = false;
        BrowserSignIn.IsVisible = !password;
        PasswordSignIn.IsVisible = password;
    }

    private void OnShowPasswordSignInClick(object? sender, RoutedEventArgs e)
    {
        ShowSignInWay(password: true);
        FocusAfterLayout(string.IsNullOrEmpty(UserNameBox.Text) ? UserNameBox : PasswordBox);
    }

    private void OnShowBrowserSignInClick(object? sender, RoutedEventArgs e)
    {
        ShowSignInWay(password: false);
        FocusAfterLayout(BrowserConnectButton);
    }

    /// <summary>A control that has just become visible takes the focus once it is laid out (before that it cannot).</summary>
    private static void FocusAfterLayout(Control control) =>
        Dispatcher.UIThread.Post(() => control.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);

    private void OnOpenLinkClick(object? sender, RoutedEventArgs e)
    {
        if (_browserAddress is not null)
        {
            Ui.OpenExternal(_browserAddress);
        }
    }

    private void OnCancelLinkClick(object? sender, RoutedEventArgs e) => _browserSignIn?.Cancel();

    /// <summary>The second way: user name and password of a local account of MatCloud.</summary>
    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        CloudMessage.Hide();
        try
        {
            FormErrors.ShowError(this, null, "");
            await CloudSyncService.ConnectAsync(ServerUrlBox.Text ?? "", UserNameBox.Text ?? "", PasswordBox.Text ?? "", DeviceNameBox.Text ?? "");
            PasswordBox.Text = "";
            CloudMessage.Show("Verbunden. Deine Ordner, Verbindungen und Zugangsdaten werden jetzt mit der ThixCloud abgeglichen (ohne Passwörter).", InfoSeverity.Success);
        }
        catch (BusinessException exception)
        {
            if (!FormErrors.ShowError(this, exception.Field, exception.Message))
            {
                CloudMessage.Show(exception.Message, InfoSeverity.Error);
            }
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            LoadCloud();
        }
    }

    private void OnSyncNowClick(object? sender, RoutedEventArgs e) => CloudSyncService.Kick();

    private void OnOpenCloudClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal(SettingsService.Get().Cloud.ServerUrl);

    private async void OnDisconnectClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(this), "Von der Cloud abmelden?",
                "Dieser Computer wird aus deinem Cloud-Konto entfernt und gleicht nicht mehr ab. Die Bibliothek auf diesem Computer bleibt erhalten.",
                "Abmelden", isDestructive: true))
        {
            return;
        }

        await CloudSyncService.DisconnectAsync();
        CloudMessage.Show("Abgemeldet.", InfoSeverity.Info);
        LoadCloud();
    }

    // ---------------------------------------------------------------------------------------------------------------- Updates

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            ShowUpdateResult(await UpdateCoordinator.CheckAsync());
        }
        catch (BusinessException exception)
        {
            UpdateMessage.Show(exception.Message, InfoSeverity.Error);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = UpdateService.CanUpdate;
            LastCheckText.Text = UpdateService.LastCheck is { } last ? Formats.When(last) : "–";
        }
    }

    private void ShowUpdateResult(UpdateCheckResult result)
    {
        _availableUpdate = result.Manifest;
        InstallUpdateButton.IsVisible = result.IsUpdateAvailable;
        UpdateMessage.Show(result.Message, result.IsUpdateAvailable ? InfoSeverity.Success : InfoSeverity.Info);
    }

    private async void OnInstallUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
        {
            return;
        }

        InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = false;
        UpdateProgress.IsVisible = true;
        try
        {
            await UpdateCoordinator.InstallAsync(_availableUpdate, new Progress<double>(value => UpdateProgress.Value = value));
        }
        catch (Exception exception) when (exception is BusinessException or HttpRequestException or IOException)
        {
            UpdateMessage.Show($"Das Update ist fehlgeschlagen: {exception.Message}", InfoSeverity.Error);
            InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = true;
            UpdateProgress.IsVisible = false;
        }
    }

    private void OnReleasePageClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal(UpdateService.ReleasePageUrl(AppInfo.IsLocalBuild ? "release" : AppInfo.Channel));
}
