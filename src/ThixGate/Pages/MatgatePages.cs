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
/// "Matgate": the Matgate servers this computer knows (optional - ThixGate works without). Each row: name, address, the version of the
/// last check and how many connections go through it. Below: Matgate hinzufügen, Bearbeiten, Prüfen, Im Browser öffnen … Entfernen.
/// </summary>
public sealed class MatgatePage : UserControl
{
    private readonly ListLayout<MatgateAccount> _layout;
    private readonly Button _new = Ui.Button("plus", "Matgate hinzufügen", "MatButton.Primary");
    private readonly Button _edit = Ui.Button("edit", "Bearbeiten");
    private readonly Button _check = Ui.Button("plug", "Prüfen");
    private readonly Button _open = Ui.Button("external", "Im Browser öffnen");
    private readonly Button _delete = Ui.Button("trash", "Entfernen", "MatButton.Danger");

    public MatgatePage()
    {
        _layout = new ListLayout<MatgateAccount>("Matgate",
            "Matgate ist ein Gateway für Remote-Verbindungen im Browser. ThixGate kann Verbindungen über ein Matgate führen, wenn das Ziel nur von dort erreichbar ist; als Nächstes kommen die Bibliothek aus Matgate und die Durchleitung über seine App-Schnittstelle.",
            "Name oder Adresse suchen", "Matgate-Konten", Row);
        _layout.Toolbar.Changed += (_, _) => Refresh();
        _layout.Actions.Children.AddRange([_new, _edit, _check, _open, _delete]);
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
        _check.IsEnabled = _open.IsEnabled = account is not null && !AccessPolicy.IsMatgateDisabled;
        _delete.IsEnabled = account is not null && !AccessPolicy.IsConfigurationLocked;
    }

    private static Control Row(MatgateAccount account)
    {
        var through = LibraryService.Connections().Count(connection => connection.MatgateAccountId == account.Id);
        var details = new List<string> { account.ServerUrl };
        if (account.UserName.Length > 0)
        {
            details.Add(account.UserName);
        }

        details.Add(through == 1 ? "1 Verbindung darüber" : $"{through} Verbindungen darüber");
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
        var state = account.LastChecked is { } checkedAt
            ? new StatePill(account.LastVersion.Length > 0 ? $"Matgate {account.LastVersion}" : "Erreichbar", "check", Tone.Success)
            : new StatePill("Nicht geprüft", "help-circle", Tone.Muted);
        ToolTip.SetTip(state, account.LastChecked is { } at ? $"Zuletzt geprüft {Formats.When(at)}" : null);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinHeight = 52 };
        row.Children.Add(Ui.IconTile("server", 44, 22));
        row.Children.Add(Ui.Cell(text, 0, 1));
        row.Children.Add(Ui.Cell(state, 0, 2));
        return row;
    }
}

/// <summary>Add / edit a Matgate: name, address, user. Buttons: Speichern, Prüfen, Zurück … Entfernen.</summary>
public sealed class MatgateForm : UserControl
{
    private readonly EditLayout _layout = new();
    private readonly TextBox _name = new() { Watermark = "Matgate Büro" };
    private readonly TextBox _url = new() { Watermark = "https://matgate.example.com" };
    private readonly TextBox _user = new();
    private readonly Button _check = Ui.Button("plug", "Prüfen");
    private MatgateAccount? _existing;

    public MatgateForm()
    {
        AutomationProperties.SetName(_name, "Name");
        AutomationProperties.SetName(_url, "Adresse");
        var general = _layout.AddTab("Allgemein");
        EditLayout.ColumnOf(general).Children.AddRange(
        [
            _layout.Fields.Add(nameof(MatgateAccount.Name), "Name", _name, tab: general),
            _layout.Fields.Add(nameof(MatgateAccount.ServerUrl), "Adresse", _url, "Die Adresse, unter der du Matgate im Browser öffnest.", general),
            _layout.Fields.Add(nameof(MatgateAccount.UserName), "Benutzer", _user, "Dein Benutzer in Matgate (für die spätere App-Anmeldung).", general),
        ]);
        _layout.Extra.Children.Add(_check);
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
            MatgateAccount? saved = null;
            if (_layout.Run(() => saved = MatgateService.Save(Collect())))
            {
                Closed?.Invoke($"„{saved!.Name}“ wurde gespeichert.");
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

    public void Load(Guid? accountId)
    {
        _layout.Reset();
        _existing = accountId is { } id ? MatgateService.Find(id) : null;
        _name.Text = _existing?.Name ?? "";
        _url.Text = _existing?.ServerUrl ?? "";
        _user.Text = _existing?.UserName ?? "";
        _layout.SetHead("server", _existing?.Name ?? "Matgate hinzufügen", _existing?.ServerUrl ?? "Ein Matgate als Gateway");
        _layout.Delete.IsVisible = _existing is not null;
        var allowed = !AccessPolicy.IsConfigurationLocked && !AccessPolicy.IsMatgateDisabled;
        _layout.Save.IsEnabled = _check.IsEnabled = allowed;
        _layout.Delete.IsEnabled = !AccessPolicy.IsConfigurationLocked;
    }

    public static async Task<bool> DeleteAsync(Control owner, MatgateAccount account, Action<string, InfoSeverity> show)
    {
        var through = LibraryService.Connections().Count(connection => connection.MatgateAccountId == account.Id);
        var message = through == 0
            ? $"„{account.Name}“ wird von diesem Computer entfernt. In Matgate selbst ändert sich nichts."
            : $"„{account.Name}“ wird entfernt. {through} Verbindung(en) verbinden danach wieder direkt. In Matgate selbst ändert sich nichts.";
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(owner), "Matgate entfernen?", message, "Entfernen", isDestructive: true))
        {
            return false;
        }

        try
        {
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

    private MatgateAccount Collect() => (_existing ?? new MatgateAccount()) with
    {
        Name = _name.Text ?? "",
        ServerUrl = _url.Text ?? "",
        UserName = _user.Text ?? "",
    };
}
