using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;
using ThixGate.Dialogs;

namespace ThixGate.Pages;

/// <summary>
/// Create / update a connection IN a Matgate (Matgate's app API writes it back; Matgate decides who may). Tabs: Allgemein (name,
/// protocol, address, port, folder, own or shared) | Anmeldung (user, domain for RDP - the password is set in Matgate, never from here)
/// | Anzeige (RDP: keyboard, certificate) | Notizen. Buttons: Speichern, Zurück … Löschen (with "Verwalten" in Matgate).
/// </summary>
public sealed class MatgateConnectionForm : UserControl
{
    /// <summary>Matgate's keyboard layouts (its connection form) with their German names.</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> KeyboardLayouts =
    [
        ("de-de-qwertz", "Deutsch"), ("de-ch-qwertz", "Deutsch (Schweiz)"), ("en-us-qwerty", "Englisch (USA)"),
        ("en-gb-qwerty", "Englisch (Großbritannien)"), ("fr-fr-azerty", "Französisch"), ("fr-be-azerty", "Französisch (Belgien)"),
        ("fr-ch-qwertz", "Französisch (Schweiz)"), ("it-it-qwerty", "Italienisch"), ("es-es-qwerty", "Spanisch"),
        ("es-latam-qwerty", "Spanisch (Lateinamerika)"), ("pt-br-qwerty", "Portugiesisch (Brasilien)"), ("sv-se-qwerty", "Schwedisch"),
        ("da-dk-qwerty", "Dänisch"), ("nb-no-qwerty", "Norwegisch"), ("hu-hu-qwertz", "Ungarisch"), ("tr-tr-qwerty", "Türkisch"),
        ("ja-jp-qwerty", "Japanisch"),
    ];

    private readonly EditLayout _layout = new();
    private readonly TextBox _name = new();
    private readonly ComboBox _protocol = new() { ItemsSource = Protocols.Names, MinWidth = 260 };
    private readonly TextBox _host = new() { Watermark = "server.example.com oder 192.168.1.10" };
    private readonly TextBox _port = new() { Width = 140 };
    private readonly TextBox _folder = new() { Watermark = "z. B. Kunden/Müller GmbH" };
    private readonly ToggleSwitch _shared = new() { OnContent = "", OffContent = "" };
    private readonly TextBox _user = new();
    private readonly TextBox _domain = new();
    private readonly TextBlock _passwordNote = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _keyboard = new() { ItemsSource = KeyboardLayouts.Select(layout => layout.Name).ToList(), MinWidth = 260 };
    private readonly ToggleSwitch _ignoreCertificate = new() { OnContent = "", OffContent = "" };
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140 };
    private readonly TextBox _url = new() { Watermark = "https://intranet.example.com" };
    private readonly TextBox _rootPath = new() { Watermark = "/home/anna oder freigabe/ordner" };
    private readonly TextBox _database = new() { Watermark = "Leer: alle, die der Benutzer sehen darf" };
    private readonly MatDesktop.MatFormField _urlField;
    private readonly MatDesktop.MatFormField _rootField;
    private readonly MatDesktop.MatFormField _databaseField;
    private readonly Control _addressRow;
    private readonly Control _sharedRow;
    private readonly MatDesktop.MatFormField _domainField;
    private readonly TabItem _displayTab;
    private Guid _accountId;
    private MatgateConnectionItem? _existing;
    private string _keptLayout = "de-de-qwertz";

    public MatgateConnectionForm()
    {
        var fields = _layout.Fields;
        AutomationProperties.SetName(_name, "Name");
        AutomationProperties.SetName(_host, "Adresse");
        _protocol.SelectionChanged += (_, _) => ApplyProtocol();

        var general = _layout.AddTab("Allgemein");
        var signIn = _layout.AddTab("Anmeldung");
        _displayTab = _layout.AddTab("Anzeige");
        var notes = _layout.AddTab("Notizen");

        _sharedRow = FormFields.SwitchRow("Mit anderen teilen",
            "Aus: deine eigene Verbindung in Matgate. An: eine gemeinsame, die Matgates Verwalter anderen freigeben.", _shared, isLast: true);
        _urlField = fields.Add(nameof(MatgateConnectionDraft.Url), "Adresse der Website", _url, "Ohne http(s):// nimmt Matgate https.", general);
        _rootField = fields.Add(nameof(MatgateConnectionDraft.RootPath), "Startordner", _rootPath, "SMB: Freigabe/Ordner; leer: ganz oben.", general);
        _databaseField = fields.Add(nameof(MatgateConnectionDraft.Database), "Datenbank", _database, tab: general);
        EditLayout.ColumnOf(general).Children.AddRange(
        [
            fields.Add(nameof(MatgateConnectionDraft.Name), "Name", _name, "So erscheint die Verbindung in Matgate und hier.", general),
            fields.Add(nameof(MatgateConnectionDraft.Protocol), "Protokoll", _protocol, tab: general),
            _addressRow = FormFields.Pair(
                fields.Add(nameof(MatgateConnectionDraft.Host), "Adresse", _host, "Wie Matgate das Ziel erreicht", general),
                fields.Add(nameof(MatgateConnectionDraft.Port), "Port", _port, "Leer: Standard", general)),
            _urlField,
            _rootField,
            _databaseField,
            fields.Add(nameof(MatgateConnectionDraft.Folder), "Ordner in Matgate", _folder, "Unterordner mit „/“; leer: keiner.", general),
            _sharedRow,
        ]);
        _domainField = fields.Add(nameof(MatgateConnectionDraft.Domain), "Domäne", _domain, "Leer bei lokalen Konten; sonst z. B. FIRMA.", signIn);
        EditLayout.ColumnOf(signIn).Children.AddRange(
        [
            fields.Add(nameof(MatgateConnectionDraft.UserName), "Benutzername", _user, tab: signIn),
            _domainField,
            FormFields.Row("Passwort", null, _passwordNote, isLast: true),
        ]);
        EditLayout.ColumnOf(_displayTab).Children.AddRange(
        [
            FormFields.Row("Tastatur", "Das Tastaturlayout, das der Server für diese Sitzung annimmt.", _keyboard),
            FormFields.SwitchRow("Zertifikat nicht prüfen", "Die meisten Server haben ein selbst ausgestelltes Zertifikat.", _ignoreCertificate, isLast: true),
        ]);
        EditLayout.ColumnOf(notes).Children.Add(fields.Add(nameof(MatgateConnectionDraft.Notes), "Notiz", _notes, tab: notes));

        _layout.Save.Click += async (_, _) =>
        {
            MatgateConnectionItem? saved = null;
            if (await _layout.RunAsync(async () => saved = await MatgateService.SaveConnectionAsync(_accountId, _existing?.Id, Collect())))
            {
                Closed?.Invoke($"„{saved!.Name}“ wurde in Matgate gespeichert.");
            }
        };
        _layout.Back.Click += (_, _) => Closed?.Invoke(null);
        _layout.Delete.Click += async (_, _) =>
        {
            if (_existing is not { } item
                || !await ConfirmDialog.AskAsync(Ui.WindowOf(this), "In Matgate löschen?",
                    $"„{item.Name}“ wird in Matgate gelöscht – für alle, die sie dort nutzen. Eigene Verbindungen, die darüber verbinden, gehen dann nicht mehr.",
                    "Löschen", isDestructive: true))
            {
                return;
            }

            if (await _layout.RunAsync(() => MatgateService.DeleteConnectionAsync(_accountId, item.Id)))
            {
                Closed?.Invoke($"„{item.Name}“ wurde in Matgate gelöscht.");
            }
        };
        Content = _layout;
    }

    /// <summary>Called when the form is left (the message for the Matgate page, null: nothing to say).</summary>
    public Action<string?>? Closed { get; set; }

    public EditLayout Layout => _layout;

    public TextBox NameBox => _name;

    public TextBox HostBox => _host;

    public ComboBox ProtocolChoice => _protocol;

    public Control SharedRow => _sharedRow;

    /// <summary>Shows a connection of the account's Matgate (null: a new one).</summary>
    public void Load(Guid accountId, Guid? matgateConnectionId)
    {
        _layout.Reset();
        _accountId = accountId;
        var account = MatgateService.Find(accountId);
        var library = MatgateService.Library(accountId);
        _existing = matgateConnectionId is { } id ? library?.Connections.FirstOrDefault(item => item.Id == id) : null;
        var draft = _existing is { } item ? MatgateConnectionDraft.Of(item) : new MatgateConnectionDraft { Shared = library?.User.CanCreatePrivate != true };

        _name.Text = draft.Name;
        _protocol.SelectedIndex = (int)draft.Protocol;
        _protocol.IsEnabled = _existing is null;
        _host.Text = draft.Host;
        _port.Text = draft.Port > 0 && draft.Port != RemoteConnection.DefaultPort(draft.Protocol) ? draft.Port.ToString(CultureInfo.InvariantCulture) : "";
        _folder.Text = draft.Folder;
        _shared.IsChecked = draft.Shared;
        _user.Text = draft.UserName;
        _domain.Text = draft.Domain;
        _keptLayout = draft.KeyboardLayout;
        _keyboard.SelectedIndex = KeyboardLayouts.ToList().FindIndex(layout => layout.Id == draft.KeyboardLayout);
        _ignoreCertificate.IsChecked = draft.IgnoreCertificate;
        _notes.Text = draft.Notes;
        _url.Text = draft.Url;
        _rootPath.Text = draft.RootPath;
        _database.Text = draft.Database;
        _passwordNote.Text = _existing?.HasStoredLogin == true
            ? "Matgate hat ein Passwort für diese Verbindung gespeichert. Es bleibt dort – ändern nur in Matgate."
            : "ThixGate schickt Matgate nie ein Passwort. Ein gespeichertes Passwort setzt du in Matgate; sonst fragt die Sitzung danach.";

        // Own or shared: only when there is a choice, only for a new one.
        var user = library?.User;
        _sharedRow.IsVisible = _existing is null && user is { CanCreatePrivate: true, CanCreateShared: true };
        var (protocolName, icon) = Protocols.Of(draft.Protocol);
        _layout.SetHead(icon, _existing?.Name ?? "Neue Verbindung in Matgate",
            _existing is null ? $"In „{account?.Name}“" : $"{protocolName} · {draft.Host} · in „{account?.Name}“");
        _layout.Delete.IsVisible = _existing?.CanDelete == true;
        _layout.Save.IsEnabled = _existing?.CanEdit ?? user?.CanCreate == true;
        _layout.Delete.IsEnabled = true;
        _layout.Back.IsEnabled = true;
        if (_existing is { CanEdit: false })
        {
            _layout.Message.Show("Diese Verbindung darfst du in Matgate nicht bearbeiten.", InfoSeverity.Info);
        }

        ApplyProtocol();
    }

    private void ApplyProtocol()
    {
        var protocol = (ConnectionProtocol)Math.Max(0, _protocol.SelectedIndex);
        _port.Watermark = $"Standard: {RemoteConnection.DefaultPort(protocol)}";
        var kind = protocol.KindOf();
        _domainField.IsVisible = protocol.HasDomain();
        _displayTab.IsVisible = protocol.HasCertificate();
        _addressRow.IsVisible = kind != ConnectionKind.Web;
        _urlField.IsVisible = kind == ConnectionKind.Web;
        _rootField.IsVisible = kind == ConnectionKind.Files;
        _databaseField.IsVisible = kind == ConnectionKind.Database;
        if (_layout.Tabs.SelectedItem is TabItem { IsVisible: false })
        {
            _layout.Tabs.SelectedIndex = 0;
        }
    }

    private MatgateConnectionDraft Collect()
    {
        var portText = (_port.Text ?? "").Trim();
        int port;
        if (portText.Length == 0)
        {
            port = 0;
        }
        else if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
        {
            throw new BusinessException("Der Port ist eine Zahl zwischen 1 und 65535 (leer: Standard).", nameof(MatgateConnectionDraft.Port));
        }

        var keyboard = _keyboard.SelectedIndex;
        return new MatgateConnectionDraft
        {
            Name = _name.Text ?? "",
            Protocol = (ConnectionProtocol)Math.Max(0, _protocol.SelectedIndex),
            Host = _host.Text ?? "",
            Port = port,
            UserName = _user.Text ?? "",
            Domain = _domain.Text ?? "",
            Folder = _folder.Text ?? "",
            Notes = _notes.Text ?? "",
            KeyboardLayout = keyboard >= 0 && keyboard < KeyboardLayouts.Count ? KeyboardLayouts[keyboard].Id : _keptLayout,
            IgnoreCertificate = _ignoreCertificate.IsChecked == true,
            Url = _url.Text ?? "",
            RootPath = _rootPath.Text ?? "",
            Database = _database.Text ?? "",
            Shared = _shared.IsChecked == true,
        };
    }
}
