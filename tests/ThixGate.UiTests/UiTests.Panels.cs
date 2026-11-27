using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using ThixGate.Core.Business;
using ThixGate.Core.Models;
using ThixGate.Pages;
using ThixGate.Sessions;

namespace ThixGate.UiTests;

/// <summary>Files, databases and websites as tabs: the form per kind, the website tab with a stand-in browser, and - opt-in - real servers.</summary>
public sealed partial class UiTests
{
    [AvaloniaFact]
    public void The_form_shows_what_each_kind_of_connection_needs()
    {
        using var data = new TestData();
        var window = Open();
        window.ShowConnectionForm(null);
        Settle(window);
        var form = window.ConnectionForm;
        bool Shown(string automationName) => form.GetLogicalDescendants().OfType<Control>()
            .Any(control => Avalonia.Automation.AutomationProperties.GetName(control) == automationName && control.IsEffectivelyVisible);

        form.ProtocolChoice.SelectedIndex = (int)ConnectionProtocol.Web;
        Settle(window);
        Assert.True(Shown("Adresse der Website"));
        Assert.False(Shown("Adresse"));
        Assert.False(form.DisplayTab.IsVisible);
        Save(window, "connection-form-website");

        form.ProtocolChoice.SelectedIndex = (int)ConnectionProtocol.Sftp;
        Settle(window);
        Assert.True(Shown("Startordner"));
        Assert.True(form.KeyFileField.IsVisible);
        Assert.False(form.DomainField.IsVisible);

        form.ProtocolChoice.SelectedIndex = (int)ConnectionProtocol.Smb;
        Settle(window);
        Assert.True(form.DomainField.IsVisible);
        Assert.False(form.KeyFileField.IsVisible);

        form.ProtocolChoice.SelectedIndex = (int)ConnectionProtocol.MsSql;
        Settle(window);
        Assert.True(Shown("Datenbank"));
        Assert.False(Shown("Startordner"));
        Save(window, "connection-form-database");

        form.NameBox.Text = "Intranet";
        form.ProtocolChoice.SelectedIndex = (int)ConnectionProtocol.Web;
        form.GetLogicalDescendants().OfType<TextBox>().First(box => Avalonia.Automation.AutomationProperties.GetName(box) == "Adresse der Website").Text = "intranet.lab/start";
        form.Layout.Save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        var saved = Assert.Single(LibraryService.Connections(), connection => connection.Name == "Intranet");
        Assert.Equal(("https://intranet.lab/start", "intranet.lab"), (saved.Url, saved.Host));
        window.Close();
    }

    [AvaloniaFact]
    public void Every_kind_of_connection_has_a_tidy_form()
    {
        using var data = new TestData();
        var window = Open();
        window.ShowConnectionForm(null);
        Settle(window);
        var form = window.ConnectionForm;
        T Named<T>(string name) where T : Control => form.GetLogicalDescendants().OfType<T>().First(control => Avalonia.Automation.AutomationProperties.GetName(control) == name);

        foreach (var protocol in Enum.GetValues<ConnectionProtocol>())
        {
            form.ProtocolChoice.SelectedIndex = (int)protocol;
            form.Layout.Tabs.SelectedIndex = 0;
            Settle(window);
            Save(window, "connection-form-" + protocol.ToString().ToLowerInvariant());
            if (protocol.KindOf() == ConnectionKind.Web)
            {
                continue;
            }

            // Address and port side by side: the boxes on one line.
            var host = Named<TextBox>("Adresse");
            var port = Named<TextBox>("Port");
            var hostTop = host.TranslatePoint(default, window)!.Value.Y;
            var portTop = port.TranslatePoint(default, window)!.Value.Y;
            Assert.True(Math.Abs(hostTop - portTop) < 1, $"{protocol}: address at {hostTop}, port at {portTop}");
            Assert.Equal(host.Bounds.Height, port.Bounds.Height, 0.5);

            form.Layout.Tabs.SelectedIndex = 1;
            Settle(window);
            Save(window, "connection-form-" + protocol.ToString().ToLowerInvariant() + "-signin");
            Assert.True(protocol.HasKeyFile() == form.Layout.Fields.Find(SshKeys.Field)!.IsVisible, $"{protocol}: SSH key field");
        }

        window.Close();
    }

    [AvaloniaFact]
    public void A_website_opens_as_a_tab_with_the_browser_of_the_platform()
    {
        using var data = new TestData();
        SettingsService.Update(settings => settings with { ConfirmCloseSession = false });
        var browser = new StandInBrowser();
        WebPanel.BrowserFactory = () => browser;
        try
        {
            var window = Open();
            var site = LibraryService.SaveConnection(new RemoteConnection { Name = "Wiki", Protocol = ConnectionProtocol.Web, Url = "https://wiki.lab/start" });
            var view = SessionHub.Open(site);
            for (var until = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < until && view.StateText != "Verbunden";)
            {
                Settle(window);
            }

            Assert.Equal("Verbunden", view.StateText);
            Assert.Same(view, window.ShownSession);
            Assert.Equal("https://wiki.lab/start", browser.Options!.Start.ToString());
            Assert.EndsWith(site.Id.ToString("N"), browser.Options.DataFolder);
            Assert.Null(browser.Options.Proxy); // direct, not über Matgate

            var panel = Assert.IsType<WebPanel>(view.Panel);
            browser.Go("https://wiki.lab/seite-2", "Seite 2 – Wiki");
            Settle(window);
            Assert.Equal("https://wiki.lab/seite-2", panel.Address);
            Save(window, "session-web-tab");
            SessionHub.CloseAll();
            Settle(window);
            Assert.True(browser.Closed);
            window.Close();
        }
        finally
        {
            WebPanel.BrowserFactory = () => null;
        }
    }

    /// <summary>The files tab against a real SFTP server (THIXGATE_E2E_FILES=1; the container of CLAUDE.md).</summary>
    [AvaloniaFact]
    public void Files_tab_against_a_real_sftp_server()
    {
        if (Environment.GetEnvironmentVariable("THIXGATE_E2E_FILES") is null)
        {
            return;
        }

        using var data = new TestData();
        SettingsService.Update(settings => settings with { ConfirmCloseSession = false });
        var credential = CredentialService.Save(new StoredCredential { Name = "Test", UserName = "demo" }, "demo-only-pw");
        var connection = LibraryService.SaveConnection(new RemoteConnection { Name = "Linux-Server (Dateien)", Protocol = ConnectionProtocol.Sftp, Host = "127.0.0.1", Port = 12222, RootPath = "/config", CredentialId = credential.Id });
        var window = Open(1360, 860);
        try
        {
            var view = SessionHub.Open(connection);
            var files = Assert.IsType<FilesView>(view.Panel);
            for (var until = DateTime.UtcNow.AddSeconds(20); DateTime.UtcNow < until && files.Entries.Count == 0;)
            {
                Settle(window);
            }

            Assert.Equal("Verbunden", view.StateText);
            Assert.Contains(files.Entries, entry => entry.IsDirectory);
            Save(window, "session-files");
        }
        finally
        {
            SessionHub.CloseAll();
            window.Close();
        }
    }

    /// <summary>The database tab against a real PostgreSQL (THIXGATE_E2E_DB=1; the container of CLAUDE.md).</summary>
    [AvaloniaFact]
    public void Database_tab_against_a_real_postgres()
    {
        if (Environment.GetEnvironmentVariable("THIXGATE_E2E_DB") is null)
        {
            return;
        }

        using var data = new TestData();
        SettingsService.Update(settings => settings with { ConfirmCloseSession = false });
        var credential = CredentialService.Save(new StoredCredential { Name = "Test", UserName = "demo" }, "demo-only-pw");
        var connection = LibraryService.SaveConnection(new RemoteConnection { Name = "Shop-Datenbank", Protocol = ConnectionProtocol.Postgres, Host = "127.0.0.1", Port = 15432, Database = "shop", CredentialId = credential.Id });
        var window = Open(1360, 860);
        try
        {
            var view = SessionHub.Open(connection);
            var database = Assert.IsType<DatabaseView>(view.Panel);
            for (var until = DateTime.UtcNow.AddSeconds(20); DateTime.UtcNow < until && database.Documents.Items.Count == 0;)
            {
                Settle(window);
            }

            var query = Assert.IsType<QueryDocument>(((TabItem)database.Documents.Items[0]!).Content);
            query.Editor.Text = "CREATE TABLE IF NOT EXISTS kunden (id int PRIMARY KEY, name text, ort text);\n"
                                + "INSERT INTO kunden VALUES (1, 'Anna Schmidt', 'Köln'), (2, 'Ben Wagner', 'Bonn'), (3, 'Clara Weiß', 'Aachen')\n"
                                + "  ON CONFLICT DO NOTHING;\n\n"
                                + "SELECT id, name, ort\n  FROM kunden\n ORDER BY id;";
            var run = query.RunAsync();
            for (var until = DateTime.UtcNow.AddSeconds(20); DateTime.UtcNow < until && !run.IsCompleted;)
            {
                Settle(window);
            }

            Settle(window);
            Assert.True(query.ResultGridsShown.Count > 0, query.MessageText);
            Save(window, "session-database");

            // "Oberste 100 Zeilen auswählen" (Management Studio): the columns one a line, LIMIT 100, run at once.
            Assert.Equal(320, database.TreeWidth);
            var top = database.SelectTopAsync("shop", "public", "kunden");
            for (var until = DateTime.UtcNow.AddSeconds(20); DateTime.UtcNow < until && !top.IsCompleted;)
            {
                Settle(window);
            }

            var selected = top.GetAwaiter().GetResult()!;
            Assert.Equal("SELECT \"id\"\n      ,\"name\"\n      ,\"ort\"\n  FROM \"public\".\"kunden\"\n LIMIT 100", selected.Editor.Text);
            Settle(window);
            Assert.True(selected.ResultGridsShown.Count > 0, selected.MessageText);
            Assert.Equal(3, selected.ResultGridsShown[0].ItemsSource!.Cast<object>().Count());
            Save(window, "session-database-top");
        }
        finally
        {
            SessionHub.CloseAll();
            window.Close();
        }
    }

    /// <summary>A browser that only remembers what it was told (headless has no native view).</summary>
    private sealed class StandInBrowser : IBrowser
    {
        public BrowserOptions? Options { get; private set; }

        public bool Closed { get; private set; }

        public Control Control { get; } = new Border();

        public event Action<string>? AddressChanged;

        public event Action<string>? TitleChanged;

        public void Go(string address, string title)
        {
            AddressChanged?.Invoke(address);
            TitleChanged?.Invoke(title);
        }

        public Task StartAsync(BrowserOptions options)
        {
            Options = options;
            AddressChanged?.Invoke(options.Start.ToString());
            return Task.CompletedTask;
        }

        public void Navigate(Uri address) => AddressChanged?.Invoke(address.ToString());

        public void Back()
        {
        }

        public void Forward()
        {
        }

        public void Reload()
        {
        }

        public void Close() => Closed = true;
    }
}
