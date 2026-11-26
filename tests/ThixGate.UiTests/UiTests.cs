using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ThixGate.Controls;
using MatDesktop;
using ThixGate.Core.Business;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;
using ThixGate.Pages;
using ThixGate.Sessions;

[assembly: AvaloniaTestApplication(typeof(ThixGate.UiTests.UiTestApp))]

namespace ThixGate.UiTests;

/// <summary>The real app (App.axaml, themes, pages) rendered headless with Skia: real pixels, no screen needed.</summary>
public static class UiTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// The main window driven like a user would (the sections, the forms, the toolbar, connecting) and screenshots of every page in light and
/// dark (set THIXGATE_SCREENSHOTS to a folder to keep them; docs/images has the ones of the README, from the snapshot command).
/// </summary>
public sealed class UiTests
{
    private static readonly string? ShotFolder = Environment.GetEnvironmentVariable("THIXGATE_SCREENSHOTS");

    [AvaloniaFact]
    public void Every_page_renders_in_light_and_dark_and_the_keys_switch_the_sections()
    {
        using var data = new TestData();
        SampleData.Create();
        var window = Open();
        foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
        {
            Application.Current!.RequestedThemeVariant = theme;
            window.KeyPressQwerty(PhysicalKey.Digit1, CommandModifier);
            Settle(window);
            Assert.IsType<ConnectionsPage>(window.CurrentPage);
            Assert.Equal(6, window.ConnectionsPage.List.ItemCount);
            Save(window, $"connections-{theme}");

            window.KeyPressQwerty(PhysicalKey.Digit2, CommandModifier);
            Settle(window);
            Assert.Equal(4, Assert.IsType<FoldersPage>(window.CurrentPage).List.ItemCount);
            Save(window, $"folders-{theme}");

            window.KeyPressQwerty(PhysicalKey.Digit3, CommandModifier);
            Settle(window);
            Assert.Equal(2, Assert.IsType<CredentialsPage>(window.CurrentPage).List.ItemCount);
            Save(window, $"credentials-{theme}");

            window.KeyPressQwerty(PhysicalKey.Digit4, CommandModifier);
            Settle(window);
            Assert.Equal(1, Assert.IsType<MatgatePage>(window.CurrentPage).List.ItemCount);
            Save(window, $"matgate-{theme}");

            window.KeyPressQwerty(PhysicalKey.Comma, CommandModifier);
            Settle(window);
            Assert.IsType<SettingsPage>(window.CurrentPage);
            Save(window, $"settings-{theme}");

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Settle(window);
            Assert.IsType<ConnectionsPage>(window.CurrentPage);
        }

        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        window.Close();
    }

    [AvaloniaFact]
    public void The_connection_form_hides_what_the_protocol_lacks_shows_errors_at_their_field_and_saves()
    {
        using var data = new TestData();
        var window = Open();
        window.ShowConnectionForm(null);
        Settle(window);
        var form = window.ConnectionForm;

        // RDP: domain and display; no key file
        Assert.True(form.DomainField.IsVisible);
        Assert.True(form.DisplayTab.IsVisible);
        Assert.False(form.KeyFileField.IsVisible);

        // SSH: the key file, no domain, no display tab
        form.ProtocolChoice.SelectedIndex = (int)ConnectionProtocol.Ssh;
        Settle(window);
        Assert.False(form.DomainField.IsVisible);
        Assert.False(form.DisplayTab.IsVisible);
        Assert.True(form.KeyFileField.IsVisible);

        // errors at their field, on their tab
        form.NameBox.Text = "Webserver";
        form.Layout.Save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal("Bitte geben Sie die Adresse (Rechnername oder IP) an.", form.Layout.Fields.Find(nameof(RemoteConnection.Host))!.Error);
        Assert.Equal(0, form.Layout.Tabs.SelectedIndex);
        Save(window, "connection-form-error");

        form.HostBox.Text = "web.example.com";
        form.Layout.Save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        var saved = Assert.Single(LibraryService.Connections());
        Assert.Equal(("Webserver", ConnectionProtocol.Ssh, "web.example.com"), (saved.Name, saved.Protocol, saved.Host));
        Assert.IsType<ConnectionsPage>(window.CurrentPage);
        Assert.Contains("gespeichert", window.ConnectionsPage.Message.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void The_toolbar_searches_and_filters_and_a_double_click_connects()
    {
        using var data = new TestData();
        SampleData.Create();
        var opened = new List<RemoteConnection>();
        SessionLauncher.OpenForTests = opened.Add;
        try
        {
            var window = Open();
            var page = window.ConnectionsPage;
            page.SearchBox.Text = "müller";
            Settle(window);
            Assert.Equal(2, page.List.ItemCount);
            page.SearchBox.Text = "";
            page.ProtocolChoice.SelectedIndex = 1 + (int)ConnectionProtocol.Vnc;
            Settle(window);
            Assert.Equal("Raspberry Pi Werkstatt", Assert.IsType<RemoteConnection>(Assert.Single(page.List.Items)).Name);

            page.List.SelectedIndex = 0;
            page.ConnectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle(window);
            Assert.Equal(SampleData.RaspberryId, Assert.Single(opened).Id);
            window.Close();
        }
        finally
        {
            SessionLauncher.OpenForTests = null;
        }
    }

    [AvaloniaFact]
    public void Folders_and_credentials_are_created_through_their_forms()
    {
        using var data = new TestData();
        var window = Open();
        window.ShowFolderForm(null);
        Settle(window);
        window.FolderForm.NameBox.Text = "Kunden";
        window.FolderForm.Layout.Save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal("Kunden", Assert.Single(LibraryService.Folders()).Name);

        window.ShowCredentialForm(null);
        Settle(window);
        var form = window.CredentialForm;
        form.NameBox.Text = "Admin";
        form.Layout.Save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal("Bitte geben Sie den Benutzernamen an.", form.Layout.Fields.Find(nameof(StoredCredential.UserName))!.Error);
        form.UserBox.Text = "admin";
        form.PasswordBox.Text = "geheim";
        form.Layout.Save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        var credential = Assert.Single(CredentialService.List());
        Assert.True(credential.HasPassword);
        Assert.Equal("geheim", CredentialService.ReadPassword(credential.Id));
        window.Close();
    }

    [AvaloniaFact]
    public void A_connection_in_Matgate_is_edited_as_far_as_Matgate_allows()
    {
        using var data = new TestData();
        SampleData.Create();
        var window = Open();
        window.ShowMatgateForm(SampleData.MatgateAccountId);
        window.MatgateForm.Layout.Tabs.SelectedIndex = 1;
        Settle(window);
        Assert.Equal(4, window.MatgateForm.Connections.ItemCount);

        // One she manages: everything but the protocol, delete too.
        window.ShowMatgateConnectionForm(SampleData.MatgateAccountId, SampleData.MatgateSqlId);
        Settle(window);
        var form = window.MatgateConnectionForm;
        Assert.Same(form, window.CurrentPage);
        Assert.Equal("SQL-Server", form.NameBox.Text);
        Assert.False(form.ProtocolChoice.IsEnabled);
        Assert.True(form.Layout.Delete.IsVisible);
        Assert.True(form.Layout.Save.IsEnabled);

        // A name is checked here, before anything goes to Matgate.
        form.NameBox.Text = "";
        form.Layout.Save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal("Bitte geben Sie der Verbindung einen Namen.", form.Layout.Fields.Find(nameof(MatgateConnectionDraft.Name))!.Error);

        // One she may only watch: no saving, no deleting. A new one: her own (no choice to share).
        window.ShowMatgateConnectionForm(SampleData.MatgateAccountId, SampleData.MatgateWatchId);
        Settle(window);
        Assert.False(form.Layout.Save.IsEnabled);
        Assert.False(form.Layout.Delete.IsVisible);
        window.ShowMatgateConnectionForm(SampleData.MatgateAccountId, null);
        Settle(window);
        Assert.True(form.ProtocolChoice.IsEnabled);
        Assert.False(form.SharedRow.IsVisible);
        form.Layout.Back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.IsType<MatgateForm>(window.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public void The_snapshot_draws_every_window_in_light_and_dark_at_its_natural_size()
    {
        using var data = new TestData();
        var folder = Path.Combine(data.Folder, "snapshot");
        var report = Snapshot.Render(folder);

        // Every picture is written – only the setup assistant is left out where it does not exist (macOS).
        Assert.Equal(Snapshot.FileNames.Order(), report.Written.Concat(report.Skipped.Select(skipped => skipped.File)).Order());
        Assert.All(report.Skipped, skipped => Assert.StartsWith("setup-", skipped.File));
        if (OperatingSystem.IsWindows())
        {
            Assert.Empty(report.Skipped);
        }

        foreach (var name in report.Written)
        {
            Assert.True(new FileInfo(Path.Combine(folder, name)).Length > 0, name);
        }

        Assert.Equal((1280, 820), PictureSize(folder, "main-connections-dark.png"));
        Assert.Equal((64, 64), PictureSize(folder, "tray-icon-windows-running.png"));

        // Clock and version run and read as before.
        Assert.Null(AppClock.Pinned);
        Assert.NotEqual(Snapshot.Version, AppInfo.Version);
    }

    [AvaloniaFact]
    public void The_version_reads_the_same_everywhere_and_opens_about()
    {
        using var data = new TestData();
        var expected = MatVersion.Format(AppInfo.Version, AppInfo.Channel);
        Assert.Matches(@"^Version \S+ · (release|nightly|local)$", expected);

        // SETUP-07: at the foot of the menu (it opens About), in About and in the footer of the setup.
        var window = Open();
        window.ShowSettings();
        Settle(window);
        window.VersionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal(expected, Assert.IsType<AboutPage>(window.CurrentPage).VersionText.Text);
        window.Close();

        if (OperatingSystem.IsWindows())
        {
            foreach (var (uninstall, update) in new[] { (false, false), (false, true), (true, false) })
            {
                var setup = Setup.ThixGateSetup.Create(uninstall, update, AppInfo.Version, @"C:\Users\Anna\AppData\Local\Programs\ThixGate", _ => Task.CompletedTask);
                Assert.Equal(expected, setup.VersionText.Text);
            }
        }
    }

    [AvaloniaFact]
    public void The_tray_menu_has_the_order_of_every_tray_and_offers_favourites_and_recent_connections()
    {
        using var data = new TestData();
        SampleData.Create();
        using var tray = new Tray.TrayIcon(App.Instance);
        var entries = tray.Menu.Build();
        var roles = entries.Select(entry => entry.Role).ToList();

        // TRAY-01: name and version, the state, Open, ThixGate's own items, Settings and the log folder, Quit last.
        Assert.Equal([MatTrayRole.Title, MatTrayRole.State, MatTrayRole.Separator, MatTrayRole.Open, MatTrayRole.Separator], roles.Take(5));
        Assert.Equal([MatTrayRole.Settings, MatTrayRole.LogFolder, MatTrayRole.Separator, MatTrayRole.Quit], roles.TakeLast(4));
        Assert.Equal($"ThixGate {AppInfo.Version}", entries[0].Text);
        Assert.Contains(entries, entry => entry.Role == MatTrayRole.Own && entry.Text.Trim() == "Terminalserver (RDP)" && entry.IsEnabled);
        Assert.Contains(entries, entry => entry.Role == MatTrayRole.Own && entry.Text.Trim() == "Raspberry Pi Werkstatt (VNC)");
        Assert.Equal("Beenden", entries[^1].Text);

        tray.SetUpdateAvailable(true);
        Assert.Equal(MatTrayRole.InstallUpdate, tray.Menu.Build()[^3].Role);
    }

    [Fact]
    public void The_setup_assistant_asks_what_installer_json_offers_with_its_defaults()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var options = Setup.ThixGateSetup.Options().ToDictionary(option => option.Key, option => option.IsOn);
        Assert.Equal(new Dictionary<string, bool> { ["autostart"] = true, ["shortcut"] = true, ["updates"] = true }, options);
    }

    [Fact]
    public void Keys_become_scancodes_for_rdp_and_keysyms_for_vnc()
    {
        Assert.Equal((0x1E, false), KeyMap.ScancodeOf(PhysicalKey.A));
        Assert.Equal((0x48, true), KeyMap.ScancodeOf(PhysicalKey.ArrowUp));
        Assert.Equal((0x5B, true), KeyMap.ScancodeOf(PhysicalKey.MetaLeft));
        Assert.Equal(0xFF0Du, KeyMap.KeysymOf(PhysicalKey.Enter, "\r"));
        Assert.Equal((uint)'a', KeyMap.KeysymOf(PhysicalKey.A, "a"));
        Assert.Equal((uint)'ä', KeyMap.KeysymOf(PhysicalKey.Quote, "ä"));
        Assert.Equal(0x010020ACu, KeyMap.KeysymOf(PhysicalKey.E, "€"));
    }

    [Fact]
    public void No_window_writes_a_colour_of_its_own()
    {
        // DESK-02: every colour comes from MatDesktop's theme; the one value ThixGate gives is its accent (App.Accent). The picture of a
        // session is the remote desktop's own: its black background is the only exception.
        var source = Path.Combine(Repository.Root, "src", "ThixGate");
        var files = Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}MatDesktop{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var literal = new System.Text.RegularExpressions.Regex(@"#[0-9A-Fa-f]{6}\b|#[0-9A-Fa-f]{8}\b|\bColor\.(Parse|FromRgb|FromArgb)|\bColors\.\w+|\bBrushes\.\w+");
        var findings = files
            .SelectMany(file => File.ReadAllLines(file).Select((line, index) => (file, line, index)))
            .Where(item => literal.IsMatch(item.line)
                           && !item.line.Contains("public const string Accent", StringComparison.Ordinal)
                           && !item.line.Contains("_surface.Background = Brushes.Black", StringComparison.Ordinal))
            .Select(item => $"{Path.GetRelativePath(source, item.file)}:{item.index + 1}: {item.line.Trim()}")
            .ToList();
        Assert.Empty(findings);
    }

    [Fact]
    public void The_copy_of_MatDesktop_is_unchanged()
    {
        var folder = Path.Combine(Repository.Root, "src", "ThixGate", "MatDesktop");
        var lines = File.ReadAllLines(Path.Combine(folder, "matdesktop.version"));
        Assert.StartsWith("MatDesktop ", lines[0]);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var (hash, file) = (line[..64], line[66..]);
            var text = File.ReadAllText(Path.Combine(folder, file)).Replace("\r\n", "\n", StringComparison.Ordinal);
            var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            Assert.True(hash == actual, $"MatDesktop/{file} was changed: copy it again from MatDesign instead of editing it");
        }
    }

    [Fact]
    public void The_copy_of_the_MatCloud_contract_is_unchanged()
    {
        var folder = Path.Combine(Repository.Root, "src", "ThixGate.Core", "MatCloud");
        var lines = File.ReadAllLines(Path.Combine(folder, "matcloud.version"));
        Assert.StartsWith("MatCloud.Contract ", lines[0]);
        var listed = new List<string>();
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var (hash, file) = (line[..64], line[66..]);
            listed.Add(file);
            var text = File.ReadAllText(Path.Combine(folder, file)).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.StartsWith("// MatCloud.Contract ", text);
            var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            Assert.True(hash == actual, $"MatCloud/{file} was changed: copy the contract again from MatCloud instead of editing it");
        }

        var present = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/'));
        Assert.Equal(listed.Order(), present.Order());
    }

    private static (int Width, int Height) PictureSize(string folder, string name)
    {
        using var codec = SkiaSharp.SKCodec.Create(Path.Combine(folder, name));
        return (codec.Info.Width, codec.Info.Height);
    }

    [AvaloniaFact]
    public void The_menu_is_the_library_as_a_tree_with_folders_search_forms_and_the_rail()
    {
        using var data = new TestData();
        SampleData.Create();
        var window = Open(1280, 820);
        var tree = window.LibraryTree;
        Assert.True(tree.IsVisible);
        Assert.True(window.Menu.ConnectionsItem.IsCurrent);
        Assert.Equal(["Zugangsdaten", "Matgate"], window.Menu.Items.Select(item => item.Label));
        string[] Names() => [.. tree.Rows.Select(row => row.Name)];
        Assert.Equal(["Büro", "Kunden", "Labor"], Names());

        // The keys of a tree: Right opens, Down goes on, Enter on a folder closes it again, Left goes up to the folder.
        tree.List.SelectedIndex = 0;
        Settle(window);
        (tree.List.ContainerFromIndex(0) as ListBoxItem)!.Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Settle(window);
        Assert.Equal(["Büro", "Dateiserver", "Terminalserver", "Kunden", "Labor"], Names());
        Assert.Equal([SampleData.OfficeFolderId], SettingsService.Get().ExpandedFolders);
        tree.List.SelectedIndex = 2;
        Settle(window);
        (tree.List.ContainerFromIndex(2) as ListBoxItem)!.Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Settle(window);
        Assert.Equal("Büro", tree.Selected!.Name);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle(window);
        Assert.Equal(["Büro", "Kunden", "Labor"], Names());

        // A click on a folder opens it and shows its connections; "Alle Verbindungen" shows all again.
        var labor = (ListBoxItem)tree.List.ContainerFromIndex(2)!;
        var point = labor.TranslatePoint(new Point(40, labor.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
        Assert.Equal(["Büro", "Kunden", "Labor", "Build-Agent", "Raspberry Pi Werkstatt"], Names());
        Assert.Same(window.ConnectionsPage, window.CurrentPage);
        Assert.Equal(2, window.ConnectionsPage.List.ItemCount);
        Assert.Equal("Labor", tree.Selected!.Name);
        Assert.False(window.Menu.ConnectionsItem.IsCurrent);
        window.Menu.ConnectionsItem.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal(6, window.ConnectionsPage.List.ItemCount);
        Assert.Null(tree.Selected);
        Assert.True(window.Menu.ConnectionsItem.IsCurrent);

        // A connection: what the right mouse button offers.
        tree.Reveal(SampleData.RaspberryId);
        Settle(window);
        var raspberry = (ListBoxItem)tree.List.ContainerFromIndex(tree.Rows.ToList().FindIndex(row => row.Id == SampleData.RaspberryId))!;
        Assert.Equal(["Verbinden", "Bearbeiten", "Duplizieren", "Zu den Favoriten", "Löschen …"],
            raspberry.ContextMenu!.ItemsSource!.OfType<MenuItem>().Select(item => (string)item.Header!));

        // The search opens the way to what matches.
        tree.SearchBox.Text = "webserver";
        Settle(window);
        Assert.Equal(["Kunden", "Müller GmbH", "Webserver Müller"], Names());
        tree.SearchBox.Text = "";

        // A form of a connection opens its folders in the tree and chooses it; "Neue Verbindung" of the tree goes into the chosen folder.
        window.ShowConnectionForm(SampleData.MuellerErpId);
        Settle(window);
        Assert.Equal(SampleData.MuellerErpId, tree.Selected!.Id);
        Assert.Contains(SampleData.MuellerFolderId, SettingsService.Get().ExpandedFolders);
        tree.NewConnectionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Same(window.ConnectionForm, window.CurrentPage);
        Assert.Equal("Kunden › Müller GmbH", window.ConnectionForm.FolderChoice.SelectedItem);
        Save(window, "tree-light");

        // Beside the wider menu the buttons below the list keep one line: only their icons.
        window.ShowConnections();
        Settle(window);
        var actions = window.ConnectionsPage.GetVisualDescendants().OfType<ActionBar>().First();
        Assert.True(actions.IsCompact);

        window.Width = 1700;
        Settle(window);
        Assert.False(actions.IsCompact); // wide enough: the texts again

        // Folded (Ctrl/Cmd+B) the menu is a rail of icons: "Alle Verbindungen", the sections, Einstellungen; the tree waits.
        window.KeyPressQwerty(PhysicalKey.B, CommandModifier);
        Settle(window);
        Assert.True(window.IsSidebarCollapsed);
        Assert.False(tree.IsVisible);
        Assert.True(window.Menu.ConnectionsItem.IsVisible && window.Menu.ConnectionsItem.IsFolded);
        Assert.Equal(LibraryNav.RailWidth, window.Menu.Bounds.Width);
        window.KeyPressQwerty(PhysicalKey.B, CommandModifier);
        Settle(window);
        Assert.True(tree.IsVisible);
        Assert.Equal(SettingsService.Get().TreeWidth, window.Menu.Bounds.Width);
        window.Close();
    }

    private static RawInputModifiers CommandModifier => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static MainWindow Open(double width = 1360, double height = 860)
    {
        SettingsService.Remember(settings => settings with { WindowWidth = width, WindowHeight = height });
        var window = new MainWindow { Width = width, Height = height };
        window.ShowFromTray();
        window.Width = width;
        window.Height = height;
        Settle(window);
        return window;
    }

    private static void Settle(Window? window = null)
    {
        for (var round = 0; round < 8; round++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(15);
        }

        Dispatcher.UIThread.RunJobs();
        window?.UpdateLayout();
    }

    private static void Save(Window window, string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (ShotFolder is null)
        {
            return;
        }

        Directory.CreateDirectory(ShotFolder);
        frame.Save(Path.Combine(ShotFolder, $"{name}.png"));
    }
}
