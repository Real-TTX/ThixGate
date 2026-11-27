using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Matgate.Display;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>
/// "Anzeige" of a running remote desktop, as Matgate's web app has it: the size (fit the window, as on the server, a fixed size), the
/// picture quality (Flüssig, Mittel, Wenig Daten, Schnellste Reaktion), the sound and the numbers in the picture - all while it runs. A
/// session on Matgate changes its encoder at once; a direct one (the picture comes as the server draws it) changes its colours at the
/// next connect, and offers to connect again now. The choice is kept with the connection. The way the session goes is shown next to its
/// name: direct, über Matgate (its relay) or as a session on Matgate.
/// </summary>
public sealed partial class SessionView
{
    private static readonly (string Name, string Title, string Hint)[] Qualities =
    [
        ("auto", "Flüssig", "60 Bilder/s"),
        ("medium", "Mittel", "30 Bilder/s"),
        ("low", "Wenig Daten", "20 Bilder/s"),
        ("fast", "Schnellste Reaktion", "weniger Farben"),
    ];

    private static readonly string[] FixedSizes = ["1280x720", "1366x768", "1600x900", "1920x1080", "2560x1440"];

    private readonly Button _display = new();
    private readonly ToggleButton _sound = new();
    private readonly Border _route = new();
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ComboBox _sizeChoice = new() { MinWidth = 240, ItemsSource = new[] { "An Fenster anpassen", "Wie am Server", "Feste Auflösung" } };
    private readonly ComboBox _fixedChoice = new() { MinWidth = 160, ItemsSource = FixedSizes.Select(size => size.Replace("x", " × ")).ToList() };
    private readonly List<QualityCard> _qualityCards = [];
    private readonly InfoBar _qualityNote = new();
    private readonly Button _reconnectNow = Ui.Button("refresh", "Jetzt neu verbinden", "MatButton.Primary");
    private readonly ToggleSwitch _soundSwitch = new() { OnContent = "", OffContent = "" };
    private readonly AudioPlayer _audio = new();
    private string _quality = "auto";
    private string _resolution = "auto";
    private bool _soundOn;
    private bool _choosing;

    /// <summary>The way the session goes, short: "Direkt", "Über Matgate · Home", "Auf Matgate · Home" (the status, the bars, tests).</summary>
    internal string RouteText()
    {
        if (_connection.Via == ConnectVia.Direct)
        {
            return "Direkt";
        }

        var name = _connection.MatgateAccountId is { } id && MatgateService.Find(id) is { } account ? account.Name : "Matgate";
        return _connection.SessionOnMatgate ? $"Auf Matgate · {name}" : $"Über Matgate · {name}";
    }

    /// <summary>The quality the session runs with ("auto", "medium", "low", "fast"; tests).</summary>
    internal string QualityName => _quality;

    /// <summary>The sound plays (tests).</summary>
    internal bool IsSoundOn => _soundOn;

    private static string QualityTitle(string name) => Qualities.FirstOrDefault(quality => quality.Name == name).Title ?? "Flüssig";

    private static string QualityOf(SessionQuality quality) => quality switch
    {
        SessionQuality.Medium => "medium",
        SessionQuality.Low => "low",
        SessionQuality.Fast => "fast",
        _ => "auto",
    };

    private static SessionQuality QualityOf(string name) => name switch
    {
        "medium" => SessionQuality.Medium,
        "low" => SessionQuality.Low,
        "fast" => SessionQuality.Fast,
        _ => SessionQuality.Auto,
    };

    /// <summary>The name and the way it goes, left in the bar.</summary>
    private Control BuildTitle()
    {
        _title.Text = _connection.Name;
        _title.Use(TextBlock.ForegroundProperty, "Mat.Text");
        var (icon, text, tone) = _connection.Via == ConnectVia.Direct
            ? ("link", "Direkt", "Mat.TextMuted")
            : _connection.SessionOnMatgate ? ("server", "Sitzung auf Matgate", "Mat.AccentText") : ("route", "Über Matgate", "Mat.AccentText");
        var label = new IconLabel { Icon = icon, Text = text };
        label.Use(TemplatedControl.ForegroundProperty, tone);
        _route.Child = label;
        _route.Padding = new Thickness(8, 2);
        _route.CornerRadius = new CornerRadius(999);
        _route.VerticalAlignment = VerticalAlignment.Center;
        _route.Use(Border.BackgroundProperty, _connection.Via == ConnectVia.Direct ? "Mat.Bg" : "Mat.AccentSoft");
        ToolTip.SetTip(_route, RouteText());
        AutomationProperties.SetName(_route, RouteText());
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { _title, _route } };
    }

    /// <summary>"Anzeige" and "Ton" in the bar (remote desktops only).</summary>
    private void BuildDisplay()
    {
        _quality = QualityOf(_connection.Quality);
        _resolution = _connection.Resolution;
        _soundOn = _connection.PlaySound && _connection.Protocol == ConnectionProtocol.Rdp;

        _display.Click += (_, _) => ShowDisplayChoice();
        _display.Flyout = new Flyout { Content = BuildDisplayPanel(), Placement = PlacementMode.BottomEdgeAlignedRight };
        _display.Flyout.Opened += (_, _) => ShowDisplayChoice();
        AutomationProperties.SetName(_display, "Anzeige");
        ToolTip.SetTip(_display, "Größe, Bildqualität, Ton und Leistungsanzeige");
        ShowDisplayLabel();

        _sound.IsChecked = _soundOn;
        _sound.IsVisible = _connection.Protocol == ConnectionProtocol.Rdp;
        AutomationProperties.SetName(_sound, "Ton");
        _sound.IsCheckedChanged += (_, _) => SetSound(_sound.IsChecked == true);
        ShowSoundLabel();
    }

    private void ShowDisplayLabel() => _display.Content = new IconLabel { Icon = "sliders", Text = $"Anzeige: {QualityTitle(_quality)}" };

    private void ShowSoundLabel()
    {
        _sound.Content = new IconLabel { Icon = "bell", Text = _soundOn ? "Ton an" : "Ton aus" };
        ToolTip.SetTip(_sound, _soundOn ? "Ton ausschalten" : "Ton einschalten");
    }

    private Control BuildDisplayPanel()
    {
        AutomationProperties.SetName(_sizeChoice, "Größe");
        AutomationProperties.SetName(_fixedChoice, "Feste Auflösung");
        _sizeChoice.SelectionChanged += (_, _) => ChooseSize();
        _reconnectNow.IsVisible = false;
        _reconnectNow.Click += (_, _) =>
        {
            _reconnectNow.IsVisible = false;
            _qualityNote.Hide();
            _display.Flyout?.Hide();
            Start();
        };
        _fixedChoice.SelectionChanged += (_, _) => ChooseSize();

        var cards = new UniformGrid { Columns = 2, ColumnSpacing = 8, RowSpacing = 8 };
        foreach (var (name, title, hint) in Qualities)
        {
            var card = new QualityCard(name switch { "fast" => "rocket", "low" => "download", "medium" => "progress-play", _ => "play" }, title, hint);
            card.Click += (_, _) => ChooseQuality(name);
            _qualityCards.Add(card);
            cards.Children.Add(card);
        }

        AutomationProperties.SetName(_soundSwitch, "Ton übertragen");
        _soundSwitch.IsCheckedChanged += (_, _) =>
        {
            if (!_choosing)
            {
                SetSound(_soundSwitch.IsChecked == true);
            }
        };

        var done = Ui.Button("check", "Fertig", "MatButton.Primary");
        done.Click += (_, _) => _display.Flyout?.Hide();
        var keep = Ui.Colored("Gilt sofort und bleibt für diese Verbindung.", "Mat.TextMuted", 11.5);
        keep.TextWrapping = TextWrapping.Wrap;
        return new StackPanel
        {
            Spacing = 10,
            Width = 380,
            Children =
            {
                Ui.Text("Anzeige", null, 15, FontWeight.SemiBold),
                Section("Größe"),
                new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { _sizeChoice, _fixedChoice } },
                Section("Bildqualität"),
                cards,
                _qualityNote,
                _reconnectNow,
                Section("Ton"),
                new DockPanel { Children = { Ui.Docked(_soundSwitch, Dock.Right), Ui.Text("Ton des Remote-Desktops hier abspielen") } },
                keep,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { done } },
            },
        };
    }

    /// <summary>
    /// A choice of picture quality as a card: quiet with a thin line when not chosen; chosen, the accent's soft colour with an accent line and
    /// the title in the accent's text colour - the hint stays readable on both (no white on accent).
    /// </summary>
    private sealed class QualityCard : Button
    {
        private readonly Border _frame = new() { Padding = new Thickness(12, 9), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5) };
        private readonly IconLabel _title;
        private readonly TextBlock _hint;
        private bool _selected;

        public QualityCard(string icon, string title, string hint)
        {
            this.Themed("MatButton.Ghost");
            Padding = new Thickness(0);
            CornerRadius = new CornerRadius(10);
            HorizontalAlignment = HorizontalAlignment.Stretch;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            _title = new IconLabel { Icon = icon, Text = title };
            _hint = Ui.Colored(hint, "Mat.TextMuted", 12);
            _frame.Child = new StackPanel { Spacing = 3, Children = { _title, _hint } };
            Content = _frame;
            AutomationProperties.SetName(this, title);
            Paint();
        }

        public bool IsSelected
        {
            get => _selected;
            set
            {
                _selected = value;
                Paint();
            }
        }

        private void Paint()
        {
            _frame.Use(Border.BackgroundProperty, _selected ? "Mat.AccentSoft" : "Mat.Surface");
            _frame.Use(Border.BorderBrushProperty, _selected ? "Mat.Accent" : "Mat.Border");
            _title.Use(TemplatedControl.ForegroundProperty, _selected ? "Mat.AccentText" : "Mat.Text");
        }
    }

    private static TextBlock Section(string text) => Ui.Colored(text, "Mat.TextMuted", 12, FontWeight.SemiBold);

    /// <summary>The panel shows what the session runs with.</summary>
    private void ShowDisplayChoice()
    {
        _choosing = true;
        try
        {
            _sizeChoice.SelectedIndex = _resolution switch { "auto" => 0, "native" => 1, _ => 2 };
            _fixedChoice.IsVisible = _sizeChoice.SelectedIndex == 2;
            _fixedChoice.SelectedIndex = Math.Max(0, Array.IndexOf(FixedSizes, _resolution));
            for (var index = 0; index < Qualities.Length; index++)
            {
                _qualityCards[index].IsSelected = Qualities[index].Name == _quality;
            }

            _soundSwitch.IsChecked = _soundOn;
            _soundSwitch.IsEnabled = _connection.Protocol == ConnectionProtocol.Rdp;
        }
        finally
        {
            _choosing = false;
        }
    }

    private void ChooseSize()
    {
        if (_choosing)
        {
            return;
        }

        _fixedChoice.IsVisible = _sizeChoice.SelectedIndex == 2;
        var resolution = _sizeChoice.SelectedIndex switch
        {
            0 => "auto",
            1 => "native",
            _ => FixedSizes[Math.Max(0, _fixedChoice.SelectedIndex)],
        };
        if (resolution == _resolution)
        {
            return;
        }

        _resolution = resolution;
        Keep(connection => connection with { Resolution = resolution });
        if (!_isConnected || _backend is not { } backend)
        {
            return;
        }

        // The desktop follows at once: the window's size, or the fixed one; "as on the server" keeps what it has now.
        if (resolution == "auto")
        {
            backend.Resize(_wantedWidth, _wantedHeight);
        }
        else if (resolution.Split('x') is [var w, var h] && int.TryParse(w, out var width) && int.TryParse(h, out var height))
        {
            backend.Resize(width, height);
        }
    }

    private void ChooseQuality(string quality)
    {
        foreach (var (card, index) in _qualityCards.Select((card, index) => (card, index)))
        {
            card.IsSelected = Qualities[index].Name == quality;
        }

        if (quality == _quality)
        {
            return;
        }

        var colours = (quality == "fast") != (_quality == "fast");
        _quality = quality;
        Keep(connection => connection with { Quality = QualityOf(quality) });
        ShowDisplayLabel();
        _qualityNote.Hide();
        _reconnectNow.IsVisible = false;
        if (_backend is MatgateScreen matgate)
        {
            // Matgate changes its encoder at once.
            matgate.Quality(quality);
            return;
        }

        if (_isConnected && colours)
        {
            // A direct session gets its picture as the server draws it: the colours (and the looks the server spends on) change only
            // with a new connection.
            _qualityNote.Show("Die Farben ändern sich beim nächsten Verbinden.", InfoSeverity.Info);
            _reconnectNow.IsVisible = true;
        }
        else if (_isConnected)
        {
            _qualityNote.Show("Direkt verbunden kommt das Bild so, wie der Server es zeichnet: weniger Daten spart vor allem „Schnellste Reaktion“ (weniger Farben, ohne Hintergrund).", InfoSeverity.Info);
        }
    }

    private void SetSound(bool on)
    {
        _soundOn = on && _connection.Protocol == ConnectionProtocol.Rdp;
        _choosing = true;
        _sound.IsChecked = _soundOn;
        _soundSwitch.IsChecked = _soundOn;
        _choosing = false;
        ShowSoundLabel();
        _backend?.SoundEnabled(_soundOn);
        if (!_soundOn)
        {
            _audio.Stop();
        }

        Keep(connection => connection with { PlaySound = _soundOn });
    }

    /// <summary>A choice made in the session stays with the connection (when the configuration may be changed).</summary>
    private void Keep(Func<RemoteConnection, RemoteConnection> change)
    {
        try
        {
            var current = LibraryService.FindConnection(_connection.Id) ?? _connection;
            _connection = LibraryService.SaveConnection(change(current));
        }
        catch (BusinessException exception)
        {
            _connection = change(_connection);
            Log.Info($"Session {_connection.Id}: the choice is not kept ({exception.Message}).");
        }
    }

    private void PlayAudio(ReadOnlySpan<byte> pcm, int rate, int channels)
    {
        if (_soundOn)
        {
            _audio.Play(pcm, rate, channels);
        }
    }

}
