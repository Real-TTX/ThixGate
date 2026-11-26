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
using ThixGate.Sessions;

namespace ThixGate.Pages;

/// <summary>
/// "Matgate": the Matgate servers this computer knows (optional - ThixGate works without). Each row: name, address, who is signed in,
/// how many connections Matgate gives and how many go through it. Below: Matgate hinzufügen, Bearbeiten, Anmelden, Prüfen, Im Browser
/// öffnen … Entfernen.
/// </summary>
public sealed class MatgatePage : UserControl
{
    private readonly ListLayout<MatgateAccount> _layout;
    private readonly Button _new = Ui.Button("plus", "Matgate hinzufügen", "MatButton.Primary");
    private readonly Button _edit = Ui.Button("edit", "Bearbeiten");
    private readonly Button _signIn = Ui.Button("login", "Anmelden");
    private readonly Button _check = Ui.Button("plug", "Prüfen");
    private readonly Button _open = Ui.Button("external", "Im Browser öffnen");
    private readonly Button _delete = Ui.Button("trash", "Entfernen", "MatButton.Danger");

    public MatgatePage()
    {
        _layout = new ListLayout<MatgateAccount>("Matgate",
            "Matgate ist ein Gateway für Remote-Verbindungen im Browser. Angemeldet zeigt ThixGate die Verbindungen, mit denen du dort arbeiten darfst, und verbindet sich über Matgate mit ihnen – mit eigener Engine und den Passwörtern von diesem Computer.",
            "Name oder Adresse suchen", "Matgate-Konten", Row);
        _layout.Toolbar.Changed += (_, _) => Refresh();
        _layout.Actions.Children.AddRange([_new, _edit, _signIn, _check, _open, _delete]);
        ActionBar.SetSeparated(_delete, true);
        _layout.SelectionChanged += (_, _) => ApplyButtons();
        _layout.ItemActivated += (_, account) => Ui.MainOf(this)?.ShowMatgateForm(account.Id);
        _new.Click += (_, _) => Ui.MainOf(this)?.ShowMatgateForm(null);
        _edit.Click += (_, _) =>
        {
            if (_layout.SelectedItem is { } account)
            {
                Ui.MainOf(this)?.ShowMatgateForm(account.Id);
            }
        };
        _signIn.Click += async (_, _) =>
        {
            if (_layout.SelectedItem is { } account)
            {
                await MatgateForm.SignInOrOutAsync(this, account, _layout.Message.Show);
            }
        };
        _check.Click += async (_, _) =>
        {
            if (_layout.SelectedItem is { } account)
            {
                await CheckAsync(account, _layout.Message.Show);
            }
        };
        _open.Click += (_, _) =>
        {
            if (_layout.SelectedItem is { } account)
            {
                Ui.OpenExternal(MatgateService.BrowserAddress(account.Id));
            }
        };
        _delete.Click += async (_, _) =>
        {
            if (_layout.SelectedItem is { } account)
            {
                await MatgateForm.DeleteAsync(this, account, _layout.Message.Show);
            }
        };
        Content = _layout;
        Ui.WhileShown(this, () => SettingsService.Changed += OnChanged, () => SettingsService.Changed -= OnChanged, Refresh);
    }

    public ListBox List => _layout.List;

    public void ShowMessage(string text, InfoSeverity severity) => _layout.Message.Show(text, severity);

    public static async Task CheckAsync(MatgateAccount account, Action<string, InfoSeverity> show)
    {
        show($"Prüfe „{account.Name}“ …", InfoSeverity.Info);
        try
        {
            var result = await MatgateService.CheckAsync(account.ServerUrl, account.Id);
            show(result.Message, result.IsMatgate ? InfoSeverity.Success : InfoSeverity.Warning);
        }
        catch (BusinessException exception)
        {
            show(exception.Message, InfoSeverity.Error);
        }
    }

    public void Refresh()
    {
        var locked = AccessPolicy.IsConfigurationLocked;
        _layout.ShowPolicy(AccessPolicy.IsMatgateDisabled
            ? "Von deiner Organisation festgelegt: Matgate ist ausgeschaltet (Richtlinie DisableMatgate)."
            : locked ? ListLayout<MatgateAccount>.LockedNote("Matgate-Konten") : null);
        var search = _layout.Toolbar.SearchText;
        var all = MatgateService.Accounts();
        var list = all.Where(account => search.Length == 0
                || account.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || account.ServerUrl.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(account => account.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var empty = all.Count == 0
            ? new EmptyState("server", "Kein Matgate eingerichtet", "ThixGate braucht kein Matgate. Hast du eines, trägst du es hier ein.", locked || AccessPolicy.IsMatgateDisabled ? null : NewButton())
            : new EmptyState("search", "Nichts gefunden", "Kein Matgate passt zur Suche.");
        _layout.Show(list, account => account.Id, all.Count == 1 ? "1 Matgate" : $"{all.Count} Matgates", empty);
        ApplyButtons();
    }

    private Button NewButton()
    {
        var button = Ui.Button("plus", "Matgate hinzufügen", "MatButton.Primary");
        button.Click += (_, _) => Ui.MainOf(this)?.ShowMatgateForm(null);
        return button;
    }

    private void OnChanged(object? sender, AppSettings settings) => Ui.Post(Refresh);

    private void ApplyButtons()
    {
        var account = _layout.SelectedItem;
        var allowed = !AccessPolicy.IsConfigurationLocked && !AccessPolicy.IsMatgateDisabled;
        _new.IsEnabled = allowed;
        _edit.IsEnabled = account is not null;
        _check.IsEnabled = _open.IsEnabled = _signIn.IsEnabled = account is not null && !AccessPolicy.IsMatgateDisabled;
        _signIn.Content = new IconLabel { Icon = account?.IsSignedIn == true ? "logout" : "login", Text = account?.IsSignedIn == true ? "Abmelden" : "Anmelden" };
        _delete.IsEnabled = account is not null && !AccessPolicy.IsConfigurationLocked;
    }

    private static Control Row(MatgateAccount account)
    {
        var through = LibraryService.Connections().Count(connection => connection.MatgateAccountId == account.Id);
        var library = account.IsSignedIn ? MatgateService.Library(account.Id) : null;
        var details = new List<string> { account.ServerUrl };
        if (account.IsSignedIn)
        {
            details.Add(account.DisplayName.Length > 0 && account.DisplayName != account.UserName ? $"{account.DisplayName} ({account.UserName})" : account.UserName);
        }

        if (library is not null)
        {
            details.Add(library.Connections.Count == 1 ? "1 Verbindung in Matgate" : $"{library.Connections.Count} Verbindungen in Matgate");
        }

        details.Add(through == 1 ? "1 eigene darüber" : $"{through} eigene darüber");
        var text = new StackPanel
        {
            Margin = new Thickness(16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = account.Name, FontWeight = FontWeight.SemiBold, FontSize = MatTokens.Type.FsLg, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = string.Join(" · ", details), TextTrimming = TextTrimming.CharacterEllipsis }.Themed("MatText.Muted"),
            },
        };
        var state = account.IsSignedIn
            ? new StatePill("Angemeldet", "check", Tone.Success)
            : account.LastChecked is not null
                ? new StatePill(account.LastVersion.Length > 0 ? $"Matgate {account.LastVersion}" : "Erreichbar", "plug", Tone.Muted)
                : new StatePill("Nicht angemeldet", "help-circle", Tone.Muted);
        ToolTip.SetTip(state, account.IsSignedIn
            ? $"Bibliothek zuletzt gelesen {Formats.When(account.LibraryFetched)}"
            : account.LastChecked is { } at ? $"Zuletzt geprüft {Formats.When(at)}" : null);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinHeight = 52 };
        row.Children.Add(Ui.IconTile("server", 44, 22));
        row.Children.Add(Ui.Cell(text, 0, 1));
        row.Children.Add(Ui.Cell(state, 0, 2));
        return row;
    }
}

/// <summary>
/// Add / edit a Matgate. Tabs: Allgemein (name, address, sign-in) | Verbindungen (Matgate's library: Verbinden, In Bibliothek
/// übernehmen, Neu in Matgate, Bearbeiten, Aktualisieren - as far as Matgate allows; deleting on <see cref="MatgateConnectionForm"/>).
/// Buttons: Speichern, Anmelden/Abmelden, Prüfen, Zurück … Entfernen.
/// </summary>
public sealed class MatgateForm : UserControl
{
    private readonly EditLayout _layout = new();
    private readonly TextBox _name = new() { Watermark = "Matgate Büro" };
    private readonly TextBox _url = new() { Watermark = "https://matgate.example.com" };
    private readonly TextBlock _signInState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _signIn = Ui.Button("login", "Anmelden");
    private readonly Button _check = Ui.Button("plug", "Prüfen");
    private readonly DataTable<MatgateConnectionItem> _connections = new("Verbindungen in Matgate") { Height = 340 };
    private readonly TextBlock _libraryState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _connect = Ui.Button("play", "Verbinden", "MatButton.Primary");
    private readonly Button _takeOver = Ui.Button("download", "Übernehmen");
    private readonly Button _reload = Ui.Button("refresh", "Aktualisieren");
    private readonly Button _newInMatgate = Ui.Button("plus", "Neu in Matgate");
    private readonly Button _editInMatgate = Ui.Button("edit", "Bearbeiten");
    private readonly TabItem _connectionsTab;
    private MatgateAccount? _existing;

    public MatgateForm()
    {
        AutomationProperties.SetName(_name, "Name");
        AutomationProperties.SetName(_url, "Adresse");
        var general = _layout.AddTab("Allgemein");
        _connectionsTab = _layout.AddTab("Verbindungen");
        EditLayout.ColumnOf(general).Children.AddRange(
        [
            _layout.Fields.Add(nameof(MatgateAccount.Name), "Name", _name, tab: general),
            _layout.Fields.Add(nameof(MatgateAccount.ServerUrl), "Adresse", _url, "Die Adresse, unter der du Matgate im Browser öffnest.", general),
            FormFields.Row("Anmeldung", "Anmelden öffnet Matgate im Browser: dort meldest du dich wie gewohnt an und lässt ThixGate auf diesem Computer zu. Abmelden geht auch in Matgate unter Einstellungen › Sicherheit › Geräte.", _signInState, isLast: true),
        ]);

        _connections
            .AddColumn("Name", "2*", item => DataTable<MatgateConnectionItem>.TextCell(item.Name, isStrong: true))
            .AddColumn("Protokoll", "110", item => DataTable<MatgateConnectionItem>.TextCell(item.ProtocolKind is { } kind ? Protocols.Of(kind).Name : item.Protocol))
            .AddColumn("Adresse", "2*", item => DataTable<MatgateConnectionItem>.TextCell(item.Port > 0 ? $"{item.Host}:{item.Port}" : item.Host))
            .AddColumn("Ordner", "1.5*", item => DataTable<MatgateConnectionItem>.TextCell(item.Folder.Length > 0 ? item.Folder.Replace("/", " › ") : "–", "Mat.TextMuted"))
            .AddColumn("Recht", "110", item => DataTable<MatgateConnectionItem>.TextCell(LevelName(item.Level), "Mat.TextMuted"))
            .AddColumn("Eigene", "120", item => DataTable<MatgateConnectionItem>.TextCell(_existing is { } account && MatgateService.TakenOver(account.Id, item.Id) is not null ? "übernommen" : "–", "Mat.TextMuted"));
        _connections.SelectionChanged += (_, _) => ApplyLibraryButtons();
        _connections.ItemActivated += async (_, item) => await ConnectAsync(item);
        var libraryActions = new ActionBar { Children = { _connect, _takeOver, _newInMatgate, _editInMatgate, _reload } };
        ToolTip.SetTip(_takeOver, "In die eigene Bibliothek übernehmen");
        EditLayout.ColumnOf(_connectionsTab).Children.AddRange([_libraryState, _connections, libraryActions]);
        _newInMatgate.Click += (_, _) =>
        {
            if (_existing is { } account)
            {
                Ui.MainOf(this)?.ShowMatgateConnectionForm(account.Id, null);
            }
        };
        _editInMatgate.Click += (_, _) =>
        {
            if (_existing is { } account && _connections.SelectedItem is { } item)
            {
                Ui.MainOf(this)?.ShowMatgateConnectionForm(account.Id, item.Id);
            }
        };
        _connect.Click += async (_, _) =>
        {
            if (_connections.SelectedItem is { } item)
            {
                await ConnectAsync(item);
            }
        };
        _takeOver.Click += (_, _) => TakeOver();
        _reload.Click += async (_, _) => await ReloadAsync();

        _layout.Extra.Children.AddRange([_signIn, _check]);
        _signIn.Click += async (_, _) =>
        {
            if (Persist() is { } account)
            {
                await SignInOrOutAsync(this, account, _layout.Message.Show);
                Load(account.Id);
            }
        };
        _check.Click += async (_, _) =>
        {
            try
            {
                var result = await MatgateService.CheckAsync(_url.Text ?? "", _existing?.Id);
                _layout.Message.Show(result.Message, result.IsMatgate ? InfoSeverity.Success : InfoSeverity.Warning);
            }
            catch (BusinessException exception)
            {
                _layout.Fields.ShowError(nameof(MatgateAccount.ServerUrl), exception.Message);
            }
        };
        _layout.Save.Click += (_, _) =>
        {
            if (Persist() is { } saved)
            {
                Closed?.Invoke($"„{saved.Name}“ wurde gespeichert.");
            }
        };
        _layout.Back.Click += (_, _) => Closed?.Invoke(null);
        _layout.Delete.Content = new IconLabel { Icon = "trash", Text = "Entfernen" };
        _layout.Delete.Click += async (_, _) =>
        {
            if (_existing is { } account && await DeleteAsync(this, account, _layout.Message.Show))
            {
                Closed?.Invoke($"„{account.Name}“ wurde entfernt.");
            }
        };
        Content = _layout;
    }

    public Action<string?>? Closed { get; set; }

    public EditLayout Layout => _layout;

    public TextBox NameBox => _name;

    public TextBox UrlBox => _url;

    public DataTable<MatgateConnectionItem> Connections => _connections;

    public void Load(Guid? accountId)
    {
        _layout.Reset();
        _existing = accountId is { } id ? MatgateService.Find(id) : null;
        _name.Text = _existing?.Name ?? "";
        _url.Text = _existing?.ServerUrl ?? "";
        _layout.SetHead("server", _existing?.Name ?? "Matgate hinzufügen", _existing?.ServerUrl ?? "Ein Matgate als Gateway");
        _layout.Delete.IsVisible = _existing is not null;
        var allowed = !AccessPolicy.IsConfigurationLocked && !AccessPolicy.IsMatgateDisabled;
        _layout.Save.IsEnabled = _check.IsEnabled = allowed;
        _signIn.IsEnabled = !AccessPolicy.IsMatgateDisabled && (allowed || _existing is not null);
        _layout.Delete.IsEnabled = !AccessPolicy.IsConfigurationLocked;
        var signedIn = _existing?.IsSignedIn == true;
        _signIn.Content = new IconLabel { Icon = signedIn ? "logout" : "login", Text = signedIn ? "Abmelden" : "Anmelden" };
        _signInState.Text = signedIn
            ? $"Angemeldet als {(_existing!.DisplayName.Length > 0 ? _existing.DisplayName : _existing.UserName)} ({_existing.UserName}) – dieser Computer heißt dort „{Core.Infrastructure.AppInfo.MachineName}“."
            : "Nicht angemeldet.";
        _connectionsTab.IsVisible = signedIn;
        ShowLibrary();
    }

    /// <summary>Signs in (browser) or out, with the messages on <paramref name="show"/>.</summary>
    public static async Task SignInOrOutAsync(Control owner, MatgateAccount account, Action<string, InfoSeverity> show)
    {
        try
        {
            if (account.IsSignedIn)
            {
                if (!await ConfirmDialog.AskAsync(Ui.WindowOf(owner), "Abmelden?",
                        $"Dieser Computer wird bei „{account.Name}“ abgemeldet. Eigene Verbindungen über dieses Matgate verbinden erst nach einer neuen Anmeldung wieder.",
                        "Abmelden"))
                {
                    return;
                }

                await MatgateService.SignOutAsync(account.Id);
                show($"Bei „{account.Name}“ abgemeldet.", InfoSeverity.Success);
                return;
            }

            show($"Bitte im Browser bei „{account.Name}“ anmelden und ThixGate zulassen …", InfoSeverity.Info);
            var signedIn = await MatgateService.SignInAsync(account.Id, Ui.OpenExternal);
            var library = await MatgateService.RefreshLibraryAsync(account.Id);
            show($"Bei „{account.Name}“ angemeldet als {signedIn.UserName}: {(library.Connections.Count == 1 ? "1 Verbindung" : $"{library.Connections.Count} Verbindungen")}.", InfoSeverity.Success);
        }
        catch (BusinessException exception)
        {
            show(exception.Message, InfoSeverity.Error);
        }
    }

    public static async Task<bool> DeleteAsync(Control owner, MatgateAccount account, Action<string, InfoSeverity> show)
    {
        var through = LibraryService.Connections().Count(connection => connection.MatgateAccountId == account.Id);
        var message = through == 0
            ? $"„{account.Name}“ wird von diesem Computer entfernt."
            : $"„{account.Name}“ wird entfernt. {through} Verbindung(en) verbinden danach wieder direkt.";
        message += account.IsSignedIn ? " Dieser Computer meldet sich dort ab." : " In Matgate selbst ändert sich nichts.";
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(owner), "Matgate entfernen?", message, "Entfernen", isDestructive: true))
        {
            return false;
        }

        try
        {
            if (account.IsSignedIn)
            {
                await MatgateService.SignOutAsync(account.Id);
            }

            MatgateService.Delete(account.Id);
            show($"„{account.Name}“ wurde entfernt.", InfoSeverity.Success);
            return true;
        }
        catch (BusinessException exception)
        {
            show(exception.Message, InfoSeverity.Error);
            return false;
        }
    }

    // Saves the form (a new account needs to exist before it can sign in); null when a field is wrong.
    private MatgateAccount? Persist()
    {
        MatgateAccount? saved = null;
        if (_existing is not null && AccessPolicy.IsConfigurationLocked)
        {
            return _existing;
        }

        return _layout.Run(() => saved = MatgateService.Save(Collect())) ? _existing = saved : null;
    }

    private void ShowLibrary()
    {
        var library = _existing is { IsSignedIn: true } account ? MatgateService.Library(account.Id) : null;
        _connections.SetItems(library?.Connections ?? [], item => item.Id);
        _connections.EmptyText = library is null ? "Noch nicht gelesen – „Aktualisieren“ holt die Verbindungen." : "Matgate gibt dir keine Remote-Desktops oder Terminals.";
        _libraryState.Text = library is null
            ? ""
            : $"Die Remote-Desktops und Terminals, mit denen du in Matgate arbeiten darfst – ohne Passwörter. Hat Matgate das Passwort gespeichert oder darfst du nur ansehen, läuft die Sitzung auf Matgate. Zuletzt gelesen {Formats.When(_existing!.LibraryFetched)}.";
        ApplyLibraryButtons();
    }

    private void ApplyLibraryButtons()
    {
        var item = _connections.SelectedItem;
        var library = _existing is { IsSignedIn: true } signedIn ? MatgateService.Library(signedIn.Id) : null;
        _connect.IsEnabled = item?.ProtocolKind is not null && !(item.IsWatchOnly && item.ProtocolKind == ConnectionProtocol.Ssh);
        _takeOver.IsEnabled = item is not null && !AccessPolicy.IsConfigurationLocked
            && (_existing is not { } account || MatgateService.TakenOver(account.Id, item.Id) is null);
        _reload.IsEnabled = _existing?.IsSignedIn == true;
        // What Matgate allows this user: create (own or shared), change ("Bearbeiten"), delete ("Verwalten").
        _newInMatgate.IsVisible = library?.User.CanCreate == true;
        _editInMatgate.IsEnabled = item?.CanEdit == true;
        _editInMatgate.IsVisible = library?.Connections.Any(connection => connection.CanEdit) == true;
    }

    /// <summary>Back from a connection in Matgate: the list as Matgate has it now, and what happened.</summary>
    public void ShowLibraryMessage(string text, InfoSeverity severity)
    {
        _existing = _existing is { } account ? MatgateService.Find(account.Id) : null;
        ShowLibrary();
        _layout.Message.Show(text, severity);
    }

    public static string LevelName(string level) => level switch
    {
        "ReadOnly" => "Ansehen",
        "Connect" => "Verbinden",
        "Edit" => "Bearbeiten",
        "Manage" => "Verwalten",
        _ => level,
    };

    private async Task ConnectAsync(MatgateConnectionItem item)
    {
        if (_existing is not { } account)
        {
            return;
        }

        try
        {
            await SessionLauncher.OpenAsync(MatgateService.TakenOver(account.Id, item.Id) ?? MatgateService.AsConnection(account.Id, item), Ui.WindowOf(this));
        }
        catch (BusinessException exception)
        {
            _layout.Message.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private void TakeOver()
    {
        if (_existing is not { } account || _connections.SelectedItem is not { } item)
        {
            return;
        }

        try
        {
            var connection = MatgateService.TakeOver(account.Id, item.Id);
            _layout.Message.Show($"„{connection.Name}“ steht jetzt in der eigenen Bibliothek und verbindet über „{account.Name}“.", InfoSeverity.Success);
            ShowLibrary();
        }
        catch (BusinessException exception)
        {
            _layout.Message.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private async Task ReloadAsync()
    {
        if (_existing is not { } account)
        {
            return;
        }

        try
        {
            var library = await MatgateService.RefreshLibraryAsync(account.Id);
            _existing = MatgateService.Find(account.Id);
            ShowLibrary();
            _layout.Message.Show(library.Connections.Count == 1 ? "1 Verbindung aus Matgate." : $"{library.Connections.Count} Verbindungen aus Matgate.", InfoSeverity.Success);
        }
        catch (BusinessException exception)
        {
            _layout.Message.Show(exception.Message, InfoSeverity.Error);
            Load(account.Id);
        }
    }

    private MatgateAccount Collect() => (_existing ?? new MatgateAccount()) with
    {
        Name = _name.Text ?? "",
        ServerUrl = _url.Text ?? "",
    };
}
