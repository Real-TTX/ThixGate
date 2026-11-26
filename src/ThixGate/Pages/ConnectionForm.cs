using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;
using ThixGate.Dialogs;
using ThixGate.Sessions;

namespace ThixGate.Pages;

/// <summary>
/// Create / update a connection. Tabs: Allgemein (name, protocol, address, port, folder, favourite) | Anmeldung (saved credentials or
/// own user, domain for RDP, key file for SSH) | Anzeige (RDP/VNC: quality, resolution, keyboard, certificate) | Gateway (direct or
/// through a Matgate) | Notizen. Fields that do not apply to the protocol are hidden. Buttons: Speichern, Verbinden, Zurück … Löschen.
/// </summary>
public sealed class ConnectionForm : UserControl
{
    public static readonly IReadOnlyList<(uint Id, string Name)> KeyboardLayouts =
    [
        (0x407, "Deutsch"), (0x807, "Deutsch (Schweiz)"), (0xC07, "Deutsch (Österreich)"), (0x409, "Englisch (USA)"),
        (0x809, "Englisch (Großbritannien)"), (0x40C, "Französisch"), (0x410, "Italienisch"), (0x40A, "Spanisch"), (0x413, "Niederländisch"),
    ];

    private static readonly string[] Qualities = ["Flüssig (60 Bilder/s)", "Mittel (30 Bilder/s)", "Wenig Daten (20 Bilder/s)", "Schnellste Reaktion (weniger Farben)"];

    private readonly EditLayout _layout = new();
    private readonly TextBox _name = new();
    private readonly ComboBox _protocol = new() { ItemsSource = Protocols.Names, MinWidth = 260 };
    private readonly TextBox _host = new() { Watermark = "server.example.com oder 192.168.1.10" };
    private readonly TextBox _port = new() { Width = 140 };
    private readonly ComboBox _folder = new() { MinWidth = 320 };
    private readonly ToggleSwitch _favorite = new() { OnContent = "", OffContent = "" };
    private readonly ComboBox _credential = new() { MinWidth = 320 };
    private readonly TextBox _user = new();
    private readonly TextBox _domain = new();
    private readonly TextBox _keyFile = new() { Watermark = "~/.ssh/id_ed25519" };
    private readonly TextBox _url = new() { Watermark = "https://intranet.example.com" };
    private readonly TextBox _rootPath = new() { Watermark = "/home/anna oder freigabe/ordner" };
    private readonly TextBox _database = new() { Watermark = "Leer: alle, die der Benutzer sehen darf" };
    private readonly MatDesktop.MatFormField _urlField;
    private readonly MatDesktop.MatFormField _rootField;
    private readonly MatDesktop.MatFormField _databaseField;
    private readonly Control _addressRow;
    private readonly Control _generalCertificateRow;
    private readonly ToggleSwitch _generalCertificate = new() { OnContent = "", OffContent = "" };
    private readonly Button _browseKey = Ui.Button("folder-open", "Durchsuchen …");
    private readonly ComboBox _quality = new() { ItemsSource = Qualities, MinWidth = 320 };
    private readonly TextBox _resolution = new() { Watermark = "auto, native oder 1920x1080", Width = 260 };
    private readonly ComboBox _keyboard = new() { ItemsSource = KeyboardLayouts.Select(layout => layout.Name).ToList(), MinWidth = 260 };
    private readonly ToggleSwitch _ignoreCertificate = new() { OnContent = "", OffContent = "" };
    private readonly ComboBox _via = new() { MinWidth = 320 };
    private readonly ComboBox _matgateConnection = new() { MinWidth = 320 };
    private readonly ToggleSwitch _sessionOnMatgate = new() { OnContent = "", OffContent = "" };
    private readonly Control _sessionOnMatgateRow;
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 140 };
    private readonly TextBox _tags = new() { Watermark = "z. B. Kunde, Produktion" };
    private readonly Button _connect = Ui.Button("play", "Verbinden");
    private readonly MatDesktop.MatFormField _domainField;
    private readonly MatDesktop.MatFormField _userField;
    private readonly MatDesktop.MatFormField _keyField;
    private readonly MatDesktop.MatFormField _matgateField;
    private readonly TabItem _displayTab;
    private readonly Control _keyboardRow;
    private readonly Control _certificateRow;
    private IReadOnlyList<RemoteFolder> _folders = [];
    private IReadOnlyList<StoredCredential> _credentials = [];
    private IReadOnlyList<MatgateAccount> _accounts = [];
    private IReadOnlyList<MatgateConnectionItem> _matgateItems = [];
    private Guid? _matgateKeep;
    private RemoteConnection? _existing;
    private Guid? _newFolder;

    public ConnectionForm()
    {
        var fields = _layout.Fields;
        AutomationProperties.SetName(_name, "Name");
        AutomationProperties.SetName(_host, "Adresse");
        AutomationProperties.SetName(_port, "Port");
        _protocol.SelectionChanged += (_, _) => ApplyProtocol();
        _credential.SelectionChanged += (_, _) => ApplyProtocol();
        _via.SelectionChanged += (_, _) => ApplyGateway();
        _browseKey.Click += async (_, _) => await BrowseKeyAsync();

        var general = _layout.AddTab("Allgemein");
        var signIn = _layout.AddTab("Anmeldung");
        _displayTab = _layout.AddTab("Anzeige");
        var gateway = _layout.AddTab("Gateway");
        var notes = _layout.AddTab("Notizen");

        _userField = fields.Add(nameof(RemoteConnection.UserName), "Benutzername", _user, tab: signIn);
        _domainField = fields.Add(nameof(RemoteConnection.Domain), "Domäne", _domain, "Leer bei lokalen Konten; sonst z. B. FIRMA.", signIn);
        _keyField = fields.Add(nameof(RemoteConnection.KeyFile), "Schlüsseldatei", new DockPanel { Children = { Ui.Docked(_browseKey, Dock.Right), _keyFile } },
            "Ein privater Schlüssel statt eines Passworts (optional).", signIn);
        _keyboardRow = FormFields.Row("Tastatur", "Das Tastaturlayout, das der Server für diese Sitzung annimmt.", _keyboard);
        AutomationProperties.SetName(_url, "Adresse der Website");
        AutomationProperties.SetName(_rootPath, "Startordner");
        AutomationProperties.SetName(_database, "Datenbank");
        _urlField = fields.Add(nameof(RemoteConnection.Url), "Adresse der Website", _url, "Ohne http(s):// nimmt ThixGate https.", general);
        _rootField = fields.Add(nameof(RemoteConnection.RootPath), "Startordner", _rootPath, "Der Ordner, in dem die Verbindung beginnt und den sie nicht verlässt (SMB: Freigabe/Ordner; leer: ganz oben).", general);
        _databaseField = fields.Add(nameof(RemoteConnection.Database), "Datenbank", _database, tab: general);
        _generalCertificateRow = FormFields.SwitchRow("Zertifikat nicht prüfen", "Für Server mit selbst ausgestelltem Zertifikat (SQL Server: TrustServerCertificate).", _generalCertificate);
        _generalCertificate.IsCheckedChanged += (_, _) => _ignoreCertificate.IsChecked = _generalCertificate.IsChecked;
        _ignoreCertificate.IsCheckedChanged += (_, _) => _generalCertificate.IsChecked = _ignoreCertificate.IsChecked;
        _certificateRow = FormFields.SwitchRow("Zertifikat nicht prüfen", "Die meisten Server haben ein selbst ausgestelltes Zertifikat.", _ignoreCertificate, isLast: true);

        EditLayout.ColumnOf(general).Children.AddRange(
        [
            fields.Add(nameof(RemoteConnection.Name), "Name", _name, "So erscheint die Verbindung in der Liste.", general),
            fields.Add(nameof(RemoteConnection.Protocol), "Protokoll", _protocol, tab: general),
            _addressRow = FormFields.Pair(
                fields.Add(nameof(RemoteConnection.Host), "Adresse", _host, "Rechnername oder IP-Adresse", general),
                fields.Add(nameof(RemoteConnection.Port), "Port", _port, "Leer: Standard", general)),
            _urlField,
            _rootField,
            _databaseField,
            _generalCertificateRow,
            fields.Add(nameof(RemoteConnection.FolderId), "Ordner", _folder, tab: general),
            FormFields.SwitchRow("Favorit", "Favoriten lassen sich in der Liste herausfiltern.", _favorite, isLast: true),
        ]);
        EditLayout.ColumnOf(signIn).Children.AddRange(
        [
            fields.Add(nameof(RemoteConnection.CredentialId), "Zugangsdaten", _credential,
                "Gespeicherte Zugangsdaten gelten für viele Verbindungen. Ohne: Benutzer hier, das Passwort fragt ThixGate beim Verbinden.", signIn),
            _userField,
            _domainField,
            _keyField,
        ]);
        EditLayout.ColumnOf(_displayTab).Children.AddRange(
        [
            fields.Add(nameof(RemoteConnection.Quality), "Bildqualität", _quality, "Die Stufen der Engine von Matgate.", _displayTab),
            fields.Add(nameof(RemoteConnection.Resolution), "Auflösung", _resolution, "auto: folgt dem Fenster · native: wie der Server sie hat · oder feste Größe", _displayTab),
            _keyboardRow,
            _certificateRow,
        ]);
        _matgateField = fields.Add(nameof(RemoteConnection.MatgateConnectionId), "Verbindung in Matgate", _matgateConnection,
            "Matgate verbindet mit Adresse und Port seiner Verbindung (auch durch seinen Tunnel); Benutzer und Passwort kommen von hier.", gateway);
        _sessionOnMatgateRow = FormFields.SwitchRow("Sitzung auf Matgate",
            "Matgate selbst verbindet sich – mit der Anmeldung, die dort gespeichert ist – und schickt das Bild hierher. Für Verbindungen, deren Passwort nur Matgate kennt oder die du nur ansehen darfst.",
            _sessionOnMatgate, isLast: true);
        _matgateConnection.SelectionChanged += (_, _) => ApplyWatchOnly();
        EditLayout.ColumnOf(gateway).Children.AddRange(
        [
            fields.Add(nameof(RemoteConnection.MatgateAccountId), "Verbinden über", _via,
                "Direkt von diesem Computer – oder über ein Matgate, wenn das Ziel nur von dort erreichbar ist (Matgate-Konten unter „Matgate“).", gateway),
            _matgateField,
            _sessionOnMatgateRow,
        ]);
        EditLayout.ColumnOf(notes).Children.AddRange(
        [
            fields.Add(nameof(RemoteConnection.Notes), "Notiz", _notes, tab: notes),
            fields.Add(nameof(RemoteConnection.Tags), "Schlagwörter", _tags, "Durch Komma getrennt; die Suche findet sie.", notes),
        ]);

        _layout.Extra.Children.Add(_connect);
        _layout.Save.Click += (_, _) =>
        {
            if (TrySave() is { } saved)
            {
                Leave($"„{saved.Name}“ wurde gespeichert.");
            }
        };
        _connect.Click += async (_, _) =>
        {
            if (TrySave() is { } saved)
            {
                try
                {
                    await SessionLauncher.OpenAsync(saved, Ui.WindowOf(this));
                }
                catch (BusinessException exception)
                {
                    _layout.Message.Show(exception.Message, InfoSeverity.Error);
                }
            }
        };
        _layout.Back.Click += (_, _) => Leave(null);
        _layout.Delete.Click += async (_, _) =>
        {
            if (_existing is { } connection && await DeleteAsync(this, connection, _layout.Message.Show))
            {
                Leave($"„{connection.Name}“ wurde gelöscht.");
            }
        };
        Content = _layout;
    }

    /// <summary>Called when the form is left (the message for the list, null: nothing to say).</summary>
    public Action<string?>? Closed { get; set; }

    public EditLayout Layout => _layout;

    public TextBox NameBox => _name;

    public TextBox HostBox => _host;

    public ComboBox ProtocolChoice => _protocol;

    public ComboBox FolderChoice => _folder;

    public TabItem DisplayTab => _displayTab;

    public MatDesktop.MatFormField DomainField => _domainField;

    public MatDesktop.MatFormField KeyFileField => _keyField;

    /// <summary>Shows a connection (null: a new one, in <paramref name="folderId"/>).</summary>
    public void Load(Guid? connectionId, Guid? folderId = null)
    {
        _layout.Reset();
        _existing = connectionId is { } id ? LibraryService.FindConnection(id) : null;
        _newFolder = folderId;
        var connection = _existing ?? new RemoteConnection { FolderId = folderId };
        var locked = AccessPolicy.IsConfigurationLocked;

        _folders = [.. LibraryService.Folders().OrderBy(folder => LibraryService.FolderPath(folder.Id), StringComparer.CurrentCultureIgnoreCase)];
        _folder.ItemsSource = new[] { "Kein Ordner" }.Concat(_folders.Select(folder => LibraryService.FolderPath(folder.Id))).ToList();
        _credentials = CredentialService.List();
        _credential.ItemsSource = new[] { "Keine – Benutzer hier angeben" }.Concat(_credentials.Select(credential =>
            $"{credential.Name} ({(credential.Domain.Length > 0 ? credential.Domain + "\\" : "")}{credential.UserName})")).ToList();
        _accounts = AccessPolicy.IsMatgateDisabled ? [] : MatgateService.Accounts();
        _via.ItemsSource = new[] { "Direkt" }.Concat(_accounts.Select(account => $"Matgate „{account.Name}“")).ToList();

        _name.Text = connection.Name;
        _protocol.SelectedIndex = (int)connection.Protocol;
        _host.Text = connection.Host;
        _port.Text = connection.Port > 0 ? connection.Port.ToString(CultureInfo.InvariantCulture) : "";
        _folder.SelectedIndex = connection.FolderId is { } folder ? _folders.ToList().FindIndex(item => item.Id == folder) + 1 : 0;
        _favorite.IsChecked = connection.IsFavorite;
        _credential.SelectedIndex = connection.CredentialId is { } credentialId ? _credentials.ToList().FindIndex(item => item.Id == credentialId) + 1 : 0;
        _user.Text = connection.UserName;
        _domain.Text = connection.Domain;
        _keyFile.Text = connection.KeyFile;
        _url.Text = connection.Url;
        _rootPath.Text = connection.RootPath;
        _database.Text = connection.Database;
        _quality.SelectedIndex = (int)connection.Quality;
        _resolution.Text = connection.Resolution;
        _keyboard.SelectedIndex = Math.Max(0, KeyboardLayouts.ToList().FindIndex(layout => layout.Id == connection.KeyboardLayout));
        _ignoreCertificate.IsChecked = connection.IgnoreCertificate;
        _generalCertificate.IsChecked = connection.IgnoreCertificate;
        _via.SelectedIndex = connection.Via == ConnectVia.Matgate && connection.MatgateAccountId is { } account
            ? Math.Max(0, _accounts.ToList().FindIndex(item => item.Id == account) + 1)
            : 0;
        _matgateKeep = connection.MatgateConnectionId;
        _matgateConnection.SelectedIndex = -1;
        _matgateItems = [];
        _sessionOnMatgate.IsChecked = connection.SessionOnMatgate;
        ApplyGateway();
        _notes.Text = connection.Notes;
        _tags.Text = connection.Tags;

        var (protocolName, icon) = Protocols.Of(connection.Protocol);
        _layout.SetHead(icon, _existing?.Name ?? "Neue Verbindung",
            _existing is null ? "Remote Desktop, VNC, SSH, Dateien, Website oder Datenbank" : $"{protocolName} · {Protocols.Address(connection)}");
        _layout.Delete.IsVisible = _existing is not null;
        _layout.Save.IsEnabled = _layout.Delete.IsEnabled = !locked;
        ApplyProtocol();
        if (locked)
        {
            _layout.Message.Show(ListLayout<RemoteConnection>.LockedNote("Verbindungen"), InfoSeverity.Info);
        }
    }

    /// <summary>Asks, then deletes a connection; false when the user said no or it failed.</summary>
    public static async Task<bool> DeleteAsync(Control owner, RemoteConnection connection, Action<string, InfoSeverity> show)
    {
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(owner), "Verbindung löschen?",
                $"„{connection.Name}“ wird gelöscht – auf allen Geräten, die mit der ThixCloud abgleichen.", "Löschen", isDestructive: true))
        {
            return false;
        }

        try
        {
            LibraryService.DeleteConnection(connection.Id);
            show($"„{connection.Name}“ wurde gelöscht.", InfoSeverity.Success);
            return true;
        }
        catch (BusinessException exception)
        {
            show(exception.Message, InfoSeverity.Error);
            return false;
        }
    }

    /// <summary>Hide what does not apply: domain and display options only where the protocol has them, the key file only for SSH.</summary>
    // "Verbindung in Matgate": the connections of the chosen Matgate with this protocol (from the copy of its library).
    private void ApplyGateway()
    {
        var viaIndex = _via.SelectedIndex;
        var account = viaIndex > 0 && viaIndex <= _accounts.Count ? _accounts[viaIndex - 1] : null;
        _matgateField.IsVisible = account is not null;
        if (_matgateConnection.SelectedIndex >= 0 && _matgateConnection.SelectedIndex < _matgateItems.Count)
        {
            _matgateKeep = _matgateItems[_matgateConnection.SelectedIndex].Id;
        }

        var protocol = (ConnectionProtocol)Math.Max(0, _protocol.SelectedIndex);
        var library = account is { IsSignedIn: true } ? MatgateService.Library(account.Id) : null;
        _matgateItems = [.. (library?.Connections ?? []).Where(item => item.ProtocolKind == protocol)];
        _matgateConnection.ItemsSource = _matgateItems.Select(item =>
            $"{item.Name} · {item.Host}{(item.Port > 0 ? ":" + item.Port.ToString(CultureInfo.InvariantCulture) : "")}{(item.Folder.Length > 0 ? " (" + item.Folder.Replace("/", " › ") + ")" : "")}").ToList();
        _matgateConnection.SelectedIndex = _matgateKeep is { } keep ? _matgateItems.ToList().FindIndex(item => item.Id == keep) : -1;
        _matgateConnection.PlaceholderText = account is null ? ""
            : !account.IsSignedIn ? "Erst bei diesem Matgate anmelden (Matgate › Anmelden)"
            : library is null ? "Die Verbindungen dieses Matgate sind noch nicht gelesen"
            : _matgateItems.Count == 0 ? "Keine Verbindung dieses Protokolls in Matgate"
            : "Bitte wählen";
        _sessionOnMatgateRow.IsVisible = account is not null && protocol.IsScreen();
        ApplyWatchOnly();
    }

    // A Matgate connection the user may only watch opens as a session on Matgate only.
    private void ApplyWatchOnly()
    {
        var index = _matgateConnection.SelectedIndex;
        var watchOnly = index >= 0 && index < _matgateItems.Count && _matgateItems[index].IsWatchOnly;
        if (watchOnly)
        {
            _sessionOnMatgate.IsChecked = true;
        }

        _sessionOnMatgate.IsEnabled = !watchOnly;
    }

    private void ApplyProtocol()
    {
        var protocol = (ConnectionProtocol)Math.Max(0, _protocol.SelectedIndex);
        var usesCredential = _credential.SelectedIndex > 0;
        _port.Watermark = $"Standard: {RemoteConnection.DefaultPort(protocol)}";
        var kind = protocol.KindOf();
        _userField.IsVisible = !usesCredential;
        _domainField.IsVisible = protocol.HasDomain() && !usesCredential;
        _keyField.IsVisible = protocol.HasKeyFile();
        _displayTab.IsVisible = kind == ConnectionKind.Screen;
        _keyboardRow.IsVisible = _certificateRow.IsVisible = protocol == ConnectionProtocol.Rdp;
        _addressRow.IsVisible = kind != ConnectionKind.Web;
        _urlField.IsVisible = kind == ConnectionKind.Web;
        _rootField.IsVisible = kind == ConnectionKind.Files;
        _databaseField.IsVisible = kind == ConnectionKind.Database;
        _generalCertificateRow.IsVisible = protocol.HasCertificate() && protocol != ConnectionProtocol.Rdp;
        ApplyGateway();
        if (_layout.Tabs.SelectedItem is TabItem { IsVisible: false })
        {
            _layout.Tabs.SelectedIndex = 0;
        }
    }

    private RemoteConnection Collect()
    {
        var portText = (_port.Text ?? "").Trim();
        int port;
        if (portText.Length == 0)
        {
            port = 0;
        }
        else if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
        {
            throw new BusinessException("Der Port ist eine Zahl zwischen 1 und 65535 (leer: Standard).", nameof(RemoteConnection.Port));
        }

        var credentialIndex = _credential.SelectedIndex;
        var viaIndex = _via.SelectedIndex;
        var folderIndex = _folder.SelectedIndex;
        return (_existing ?? new RemoteConnection()) with
        {
            Name = _name.Text ?? "",
            Protocol = (ConnectionProtocol)Math.Max(0, _protocol.SelectedIndex),
            Host = _host.Text ?? "",
            Port = port,
            FolderId = folderIndex > 0 && folderIndex <= _folders.Count ? _folders[folderIndex - 1].Id : null,
            IsFavorite = _favorite.IsChecked == true,
            CredentialId = credentialIndex > 0 && credentialIndex <= _credentials.Count ? _credentials[credentialIndex - 1].Id : null,
            UserName = credentialIndex > 0 ? "" : _user.Text ?? "",
            Domain = credentialIndex > 0 ? "" : _domain.Text ?? "",
            KeyFile = _keyFile.Text ?? "",
            Url = _url.Text ?? "",
            RootPath = _rootPath.Text ?? "",
            Database = _database.Text ?? "",
            Quality = (SessionQuality)Math.Max(0, _quality.SelectedIndex),
            Resolution = _resolution.Text ?? "auto",
            KeyboardLayout = KeyboardLayouts[Math.Max(0, _keyboard.SelectedIndex)].Id,
            IgnoreCertificate = _ignoreCertificate.IsChecked == true,
            Via = viaIndex > 0 ? ConnectVia.Matgate : ConnectVia.Direct,
            MatgateAccountId = viaIndex > 0 && viaIndex <= _accounts.Count ? _accounts[viaIndex - 1].Id : null,
            MatgateConnectionId = viaIndex > 0
                ? (_matgateConnection.SelectedIndex >= 0 && _matgateConnection.SelectedIndex < _matgateItems.Count ? _matgateItems[_matgateConnection.SelectedIndex].Id : _matgateKeep)
                : null,
            SessionOnMatgate = viaIndex > 0 && _sessionOnMatgate.IsChecked == true,
            Notes = _notes.Text ?? "",
            Tags = _tags.Text ?? "",
        };
    }

    private RemoteConnection? TrySave()
    {
        RemoteConnection? saved = null;
        if (_layout.Run(() => saved = LibraryService.SaveConnection(Collect())))
        {
            _existing = saved;
        }

        return saved;
    }

    private async Task BrowseKeyAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Privaten Schlüssel wählen", AllowMultiple = false });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            _keyFile.Text = path;
        }
    }

    private void Leave(string? message)
    {
        _existing = null;
        _newFolder = null;
        Closed?.Invoke(message);
    }
}
