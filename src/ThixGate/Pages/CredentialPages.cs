using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;
using ThixGate.Dialogs;

namespace ThixGate.Pages;

/// <summary>
/// "Zugangsdaten": the vault - saved credentials for many connections. Each row: name, user, whether a password is stored on THIS
/// computer and how many connections use it. Below: Neue Zugangsdaten, Bearbeiten … Löschen.
/// </summary>
public sealed class CredentialsPage : UserControl
{
    private readonly ListLayout<StoredCredential> _layout;
    private readonly ComboBox _filter;
    private readonly Button _new = Ui.Button("plus", "Neue Zugangsdaten", "MatButton.Primary");
    private readonly Button _edit = Ui.Button("edit", "Bearbeiten");
    private readonly Button _delete = Ui.Button("trash", "Löschen", "MatButton.Danger");

    public CredentialsPage()
    {
        _layout = new ListLayout<StoredCredential>("Zugangsdaten",
            "Benutzer und Passwort einmal speichern und in vielen Verbindungen verwenden. Passwörter liegen geschützt auf diesem Computer (Windows: DPAPI, macOS: Schlüsselbund) und gehen nie in die Cloud.",
            "Name, Benutzer oder Domäne suchen", "Zugangsdaten", Row);
        _filter = _layout.Toolbar.AddChoice("Passwort", ["Alle", "Mit Passwort auf diesem Computer", "Ohne Passwort"], 260);
        _layout.Toolbar.Changed += (_, _) => Refresh();
        _layout.Actions.Children.AddRange([_new, _edit, _delete]);
        ActionBar.SetSeparated(_delete, true);
        _layout.SelectionChanged += (_, _) => ApplyButtons();
        _layout.ItemActivated += (_, credential) => Ui.MainOf(this)?.ShowCredentialForm(credential.Id);
        _new.Click += (_, _) => Ui.MainOf(this)?.ShowCredentialForm(null);
        _edit.Click += (_, _) =>
        {
            if (_layout.SelectedItem is { } credential)
            {
                Ui.MainOf(this)?.ShowCredentialForm(credential.Id);
            }
        };
        _delete.Click += async (_, _) =>
        {
            if (_layout.SelectedItem is { } credential)
            {
                await CredentialForm.DeleteAsync(this, credential, _layout.Message.Show);
            }
        };
        Content = _layout;
        Ui.WhileShown(this, () => LibraryService.Changed += OnChanged, () => LibraryService.Changed -= OnChanged, Refresh);
    }

    public ListBox List => _layout.List;

    public void ShowMessage(string text, InfoSeverity severity) => _layout.Message.Show(text, severity);

    public void Refresh()
    {
        var locked = AccessPolicy.IsConfigurationLocked;
        _layout.ShowPolicy(locked ? ListLayout<StoredCredential>.LockedNote("Zugangsdaten")
            : AccessPolicy.ArePasswordsDisabled ? "Von deiner Organisation festgelegt: Passwörter werden nicht gespeichert, ThixGate fragt bei jeder Verbindung (Richtlinie DisableSavedPasswords)." : null);
        var all = CredentialService.List();
        var list = CredentialService.List(_layout.Toolbar.SearchText).Where(credential => _filter.SelectedIndex switch
        {
            1 => credential.HasPassword,
            2 => !credential.HasPassword,
            _ => true,
        }).ToList();
        var count = list.Count == all.Count ? (all.Count == 1 ? "1 Eintrag" : $"{all.Count} Einträge") : $"{list.Count} von {all.Count} Einträgen";
        var empty = all.Count == 0
            ? new EmptyState("key", "Noch keine Zugangsdaten", "Speichere Benutzer und Passwort einmal und wähle sie in den Verbindungen.", locked ? null : NewButton())
            : new EmptyState("search", "Nichts gefunden", "Keine Zugangsdaten passen zu Suche und Filter.");
        _layout.Show(list, credential => credential.Id, count, empty);
        ApplyButtons();
    }

    private Button NewButton()
    {
        var button = Ui.Button("plus", "Neue Zugangsdaten", "MatButton.Primary");
        button.Click += (_, _) => Ui.MainOf(this)?.ShowCredentialForm(null);
        return button;
    }

    private void OnChanged(object? sender, EventArgs e) => Ui.Post(Refresh);

    private void ApplyButtons()
    {
        var credential = _layout.SelectedItem;
        var locked = AccessPolicy.IsConfigurationLocked;
        _new.IsEnabled = !locked;
        _edit.IsEnabled = credential is not null;
        _delete.IsEnabled = credential is not null && !locked;
    }

    private static Control Row(StoredCredential credential)
    {
        var used = CredentialService.UsageCount(credential.Id);
        var user = credential.Domain.Length > 0 ? $"{credential.Domain}\\{credential.UserName}" : credential.UserName;
        var text = new StackPanel
        {
            Margin = new Thickness(16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = credential.Name, FontWeight = FontWeight.SemiBold, FontSize = MatTokens.Type.FsLg, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = $"{user} · {(used == 1 ? "1 Verbindung" : $"{used} Verbindungen")}", TextTrimming = TextTrimming.CharacterEllipsis }.Themed("MatText.Muted"),
            },
        };
        var state = credential.HasPassword
            ? new StatePill("Passwort gespeichert", "lock", Tone.Success)
            : new StatePill("Fragt beim Verbinden", "key", Tone.Muted);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinHeight = 52 };
        row.Children.Add(Ui.IconTile("key", 44, 22));
        row.Children.Add(Ui.Cell(text, 0, 1));
        row.Children.Add(Ui.Cell(state, 0, 2));
        return row;
    }
}

/// <summary>
/// Create / update saved credentials. Tabs: Allgemein (name, user, domain, password) | Verwendung (the connections that use them) |
/// Notizen. The password box is empty: typing replaces the stored one, "Passwort entfernen" deletes it.
/// </summary>
public sealed class CredentialForm : UserControl
{
    private readonly EditLayout _layout = new();
    private readonly TextBox _name = new();
    private readonly TextBox _user = new();
    private readonly TextBox _domain = new();
    private readonly TextBox _password = new() { PasswordChar = '●', RevealPassword = false };
    private readonly CheckBox _removePassword = new() { Content = "Gespeichertes Passwort entfernen" };
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140 };
    private readonly StackPanel _usage = new() { Spacing = 6 };
    private readonly MatFormField _passwordField;
    private StoredCredential? _existing;

    public CredentialForm()
    {
        AutomationProperties.SetName(_name, "Name");
        AutomationProperties.SetName(_password, "Passwort");
        var general = _layout.AddTab("Allgemein");
        var usage = _layout.AddTab("Verwendung");
        var notes = _layout.AddTab("Notizen");
        _passwordField = _layout.Fields.Add("Password", "Passwort", new StackPanel { Spacing = 8, Children = { _password, _removePassword } },
            "Bleibt geschützt auf diesem Computer und geht nie in die Cloud.", general);
        EditLayout.ColumnOf(general).Children.AddRange(
        [
            _layout.Fields.Add(nameof(StoredCredential.Name), "Name", _name, "z. B. „Domänen-Admin Büro“", general),
            FormFields.Pair(
                _layout.Fields.Add(nameof(StoredCredential.UserName), "Benutzername", _user, tab: general),
                _layout.Fields.Add(nameof(StoredCredential.Domain), "Domäne", _domain, "Nur für Remote Desktop, sonst leer", general)),
            _passwordField,
        ]);
        EditLayout.ColumnOf(usage).Children.Add(_usage);
        EditLayout.ColumnOf(notes).Children.Add(_layout.Fields.Add(nameof(StoredCredential.Notes), "Notiz", _notes, tab: notes));

        _layout.Save.Click += (_, _) =>
        {
            StoredCredential? saved = null;
            var typed = _password.Text ?? "";
            string? newPassword = _removePassword.IsChecked == true ? "" : typed.Length > 0 ? typed : null;
            if (_layout.Run(() => saved = CredentialService.Save(Collect(), newPassword)))
            {
                _password.Text = "";
                Closed?.Invoke($"„{saved!.Name}“ wurde gespeichert.");
            }
        };
        _layout.Back.Click += (_, _) => Closed?.Invoke(null);
        _layout.Delete.Click += async (_, _) =>
        {
            if (_existing is { } credential && await DeleteAsync(this, credential, _layout.Message.Show))
            {
                Closed?.Invoke($"„{credential.Name}“ wurde gelöscht.");
            }
        };
        Content = _layout;
    }

    public Action<string?>? Closed { get; set; }

    public EditLayout Layout => _layout;

    public TextBox NameBox => _name;

    public TextBox UserBox => _user;

    public TextBox PasswordBox => _password;

    public void Load(Guid? credentialId)
    {
        _layout.Reset();
        _existing = credentialId is { } id ? CredentialService.Find(id) : null;
        var credential = _existing ?? new StoredCredential();
        _name.Text = credential.Name;
        _user.Text = credential.UserName;
        _domain.Text = credential.Domain;
        _notes.Text = credential.Notes;
        _password.Text = "";
        _password.Watermark = credential.HasPassword ? "unverändert (gespeichert)" : "nicht gespeichert – fragt beim Verbinden";
        _removePassword.IsChecked = false;
        _removePassword.IsVisible = credential.HasPassword;
        _passwordField.IsVisible = !AccessPolicy.ArePasswordsDisabled;

        _usage.Children.Clear();
        var connections = LibraryService.Connections().Where(connection => connection.CredentialId == credential.Id && _existing is not null)
            .OrderBy(connection => connection.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (connections.Count == 0)
        {
            _usage.Children.Add(new TextBlock { Text = "Noch keine Verbindung verwendet diese Zugangsdaten.", TextWrapping = TextWrapping.Wrap }.Themed("MatText.Muted"));
        }

        foreach (var connection in connections)
        {
            var (protocol, icon) = Protocols.Of(connection.Protocol);
            var open = new Button { Content = new IconLabel { Icon = icon, Text = $"{connection.Name} · {protocol} · {Protocols.Address(connection)}" } }.Themed("MatButton.Ghost");
            var connectionId = connection.Id;
            open.Click += (_, _) => Ui.MainOf(this)?.ShowConnectionForm(connectionId);
            _usage.Children.Add(open);
        }

        _layout.SetHead("key", _existing?.Name ?? "Neue Zugangsdaten",
            _existing is null ? "Benutzer und Passwort für viele Verbindungen" : $"{(credential.Domain.Length > 0 ? credential.Domain + "\\" : "")}{credential.UserName} · {connections.Count} Verbindung(en)");
        _layout.Delete.IsVisible = _existing is not null;
        _layout.Save.IsEnabled = _layout.Delete.IsEnabled = !AccessPolicy.IsConfigurationLocked;
    }

    public static async Task<bool> DeleteAsync(Control owner, StoredCredential credential, Action<string, InfoSeverity> show)
    {
        var used = CredentialService.UsageCount(credential.Id);
        var message = used == 0
            ? $"„{credential.Name}“ und das gespeicherte Passwort werden gelöscht."
            : $"„{credential.Name}“ wird gelöscht. {used} Verbindung(en) behalten den Benutzer und fragen künftig nach dem Passwort.";
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(owner), "Zugangsdaten löschen?", message, "Löschen", isDestructive: true))
        {
            return false;
        }

        try
        {
            CredentialService.Delete(credential.Id);
            show($"„{credential.Name}“ wurde gelöscht.", InfoSeverity.Success);
            return true;
        }
        catch (BusinessException exception)
        {
            show(exception.Message, InfoSeverity.Error);
            return false;
        }
    }

    private StoredCredential Collect() => (_existing ?? new StoredCredential()) with
    {
        Name = _name.Text ?? "",
        UserName = _user.Text ?? "",
        Domain = _domain.Text ?? "",
        Notes = _notes.Text ?? "",
    };
}
