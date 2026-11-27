using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Matgate.Display;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>
/// The status of a remote desktop as Matgate's web app shows it: below the session a status bar - its address and the way it goes on the
/// left, on the right a pill with a dot, the last minute of frames as a small graph and three chips (frames a second, latency coloured
/// green / amber / red at 60 and 150 ms, data rate). A click opens the details: the minute as a graph (frames, latency, data rate) and
/// every number. On request the numbers also stand in the picture (small, medium, large with the graph; in a corner). The latency is the
/// TCP round trip the computer measures - to the server, or to Matgate when the session goes über Matgate.
/// </summary>
public sealed partial class SessionView
{
    private const int StatusSeconds = 60;

    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<StatusGraph.StatusPoint> _samples = [];
    private readonly Border _statusBar = new();
    private readonly Button _status = new() { Padding = new Thickness(8, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(999), MinHeight = 24 };
    private readonly Avalonia.Controls.Shapes.Ellipse _statusDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly StatusGraph _spark = new() { Width = 56, Height = 16, IsSpark = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly Chip _framesChip = new();
    private readonly Chip _latencyChip = new();
    private readonly Chip _rateChip = new();
    private readonly StatusGraph _graph = new() { Height = 72 };
    private readonly Grid _details = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 4, ColumnSpacing = 14 };
    private readonly ToggleSwitch _overlaySwitch = new() { OnContent = "", OffContent = "" };
    private readonly StackPanel _overlayOptions = new() { Spacing = 8 };
    private readonly Border _perf = new() { IsHitTestVisible = false, CornerRadius = new CornerRadius(6), Margin = new Thickness(8), IsVisible = false };
    private readonly TextBlock _perfText = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"), FontSize = 11, LineHeight = 15 };
    private readonly StatusGraph _perfGraph = new() { Width = 240, Height = 64, Margin = new Thickness(0, 4, 0, 0) };
    private int _frames;
    private long _lastBytes;
    private long _lastFrames;
    private bool _statusBuilt;

    /// <summary>A second was measured (the bar in full screen shows the same numbers).</summary>
    private event EventHandler? StatusMeasured;

    /// <summary>The latest second (tests).</summary>
    internal (double? Latency, double Frames, double Kilobits)? LatestStatus => _samples.Count == 0 ? null : (_samples[^1].Latency, _samples[^1].Frames, _samples[^1].Kilobits);

    /// <summary>The chips of the status bar as text (tests).</summary>
    internal string StatusLine => string.Join(" · ", new[] { _framesChip.Text, _latencyChip.Text, _rateChip.Text }.Where(text => text.Length > 0));

    /// <summary>The status bar below the session (tests).</summary>
    internal Border StatusBar => _statusBar;

    /// <summary>A small rounded label with a number, coloured by how good it is.</summary>
    private sealed class Chip : Border
    {
        private readonly TextBlock _text = new() { FontSize = 11.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };

        public Chip()
        {
            CornerRadius = new CornerRadius(999);
            Padding = new Thickness(8, 1);
            BorderThickness = new Thickness(1);
            VerticalAlignment = VerticalAlignment.Center;
            Child = _text;
            this.Use(BackgroundProperty, "Mat.Bg");
            this.Use(BorderBrushProperty, "Mat.Border");
            Tone(null);
            IsVisible = false;
        }

        public string Text
        {
            get => _text.Text ?? "";
            set
            {
                _text.Text = value;
                IsVisible = value.Length > 0;
            }
        }

        /// <summary>"Mat.SuccessText", "Mat.WarningText", "Mat.DangerText" or null (the normal text).</summary>
        public void Tone(string? key) => _text.Use(TextBlock.ForegroundProperty, key ?? "Mat.Text");
    }

    private Border BuildStatusBar()
    {
        _status.Themed("MatButton.Ghost");
        _status.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _statusDot, _spark, _framesChip, _latencyChip, _rateChip } };
        AutomationProperties.SetName(_status, "Verbindungsstatus");
        ToolTip.SetTip(_status, "Bilder pro Sekunde, Latenz und Datenrate – klicken für die letzte Minute");
        _statusDot.Use(Avalonia.Controls.Shapes.Shape.FillProperty, "Mat.TextFaint");
        foreach (var graph in new[] { _spark, _graph, _perfGraph })
        {
            graph.Use(StatusGraph.FramesBrushProperty, "Mat.Success");
            graph.Use(StatusGraph.LatencyBrushProperty, "Mat.Warning");
            graph.Use(StatusGraph.RateBrushProperty, "Mat.Accent");
            graph.Use(StatusGraph.GridBrushProperty, "Mat.Border");
        }

        _status.Flyout = new Flyout { Content = BuildStatusPanel(), Placement = PlacementMode.TopEdgeAlignedRight };
        _status.Flyout.Opened += (_, _) => ShowDetails();
        _statusTimer.Tick += (_, _) => Measure();

        _state.FontSize = 12.5;
        _statusBar.Child = new DockPanel
        {
            Margin = new Thickness(12, 2, 8, 2),
            Children = { Ui.Docked(_status, Dock.Right), _state },
        };
        _statusBar.MinHeight = 30;
        _statusBar.BorderThickness = new Thickness(0, 1, 0, 0);
        _statusBar.Use(Border.BackgroundProperty, "Mat.Surface");
        _statusBar.Use(Border.BorderBrushProperty, "Mat.Border");
        _statusBar.IsVisible = SettingsService.Get().ShowSessionStatus;
        _status.IsVisible = false;

        _perf.Child = new StackPanel { Children = { _perfText, _perfGraph } };
        _perf.Padding = new Thickness(7, 4);
        _perf.Use(Border.BackgroundProperty, "Mat.Surface");
        _perfText.Use(TextBlock.ForegroundProperty, "Mat.Text");
        _perf.Opacity = 0.88;
        _statusBuilt = true;
        ApplyOverlaySettings();
        return _statusBar;
    }

    private Control BuildStatusPanel()
    {
        var legend = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Children = { Legend("Mat.Success", "Bilder/s"), Legend("Mat.Warning", "Latenz"), Legend("Mat.Accent", "Datenrate") },
        };
        var graphBox = new Border { Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = new StackPanel { Spacing = 6, Children = { _graph, legend } } }
            .Use(Border.BackgroundProperty, "Mat.Bg").Use(Border.BorderBrushProperty, "Mat.Border");

        AutomationProperties.SetName(_overlaySwitch, "Leistungsanzeige im Bild");
        _overlaySwitch.IsCheckedChanged += (_, _) =>
        {
            if (SettingsService.Get().PerformanceOverlay != (_overlaySwitch.IsChecked == true))
            {
                SaveOverlay(settings => settings with { PerformanceOverlay = _overlaySwitch.IsChecked == true });
            }
        };
        var sizes = Segments(["small", "medium", "large"], ["Klein", "Mittel", "Groß"], () => SettingsService.Get().PerformanceSize,
            value => SaveOverlay(settings => settings with { PerformanceSize = value }));
        var corners = Segments(["top-left", "top-right", "bottom-left", "bottom-right"], ["Oben links", "Oben rechts", "Unten links", "Unten rechts"],
            () => SettingsService.Get().PerformanceCorner, value => SaveOverlay(settings => settings with { PerformanceCorner = value }));
        _overlayOptions.Children.AddRange([sizes, corners]);

        return new StackPanel
        {
            Spacing = 10,
            Width = 360,
            Children =
            {
                Ui.Text("Verbindung", null, 14, FontWeight.SemiBold),
                graphBox,
                _details,
                Ui.Line(new Thickness(0, 2)),
                new DockPanel { Children = { Ui.Docked(_overlaySwitch, Dock.Right), Ui.Text("Leistungsanzeige im Bild") } },
                _overlayOptions,
                Ui.Colored("Die Statusleiste ausblenden: Einstellungen › Sitzungen.", "Mat.TextMuted", 11.5),
            },
        };
    }

    /// <summary>A row of buttons of which one is chosen (sizes, corners).</summary>
    private static WrapPanel Segments(string[] values, string[] labels, Func<string> current, Action<string> choose)
    {
        var panel = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        var buttons = new List<ToggleButton>();
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            var button = new ToggleButton { Content = labels[index], Padding = new Thickness(10, 3), FontSize = 12, CornerRadius = new CornerRadius(999) };
            button.Click += (_, _) =>
            {
                choose(value);
                foreach (var other in buttons)
                {
                    other.IsChecked = ReferenceEquals(other, button);
                }
            };
            buttons.Add(button);
            panel.Children.Add(button);
        }

        panel.AttachedToVisualTree += (_, _) =>
        {
            var chosen = current();
            for (var index = 0; index < values.Length; index++)
            {
                buttons[index].IsChecked = values[index] == chosen;
            }
        };
        return panel;
    }

    private static StackPanel Legend(string colour, string text)
    {
        var mark = new Border { Width = 12, Height = 3, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center }.Use(Border.BackgroundProperty, colour);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { mark, Ui.Colored(text, "Mat.TextMuted", 11.5) } };
    }

    private void SaveOverlay(Func<AppSettings, AppSettings> change)
    {
        SettingsService.Update(change);
        ApplyOverlaySettings();
    }

    /// <summary>Where and how big the numbers in the picture are (the settings of this computer).</summary>
    private void ApplyOverlaySettings()
    {
        if (!_statusBuilt)
        {
            return;
        }

        var settings = SettingsService.Get();
        _overlaySwitch.IsChecked = settings.PerformanceOverlay;
        _overlayOptions.IsVisible = settings.PerformanceOverlay;
        _perf.IsVisible = settings.PerformanceOverlay && IsScreen && _isConnected;
        _perf.HorizontalAlignment = settings.PerformanceCorner.EndsWith("right", StringComparison.Ordinal) ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _perf.VerticalAlignment = settings.PerformanceCorner.StartsWith("bottom", StringComparison.Ordinal) ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        // In full screen the bar floats at the top: numbers in a top corner stand below it.
        _perf.Margin = new Thickness(8, _isFullScreen && _perf.VerticalAlignment == VerticalAlignment.Top ? 68 : 8, 8, 8);
        _perfText.FontSize = settings.PerformanceSize switch { "small" => 10, "large" => 12, _ => 11 };
        _perfGraph.IsVisible = settings.PerformanceSize == "large";
        _perfText.TextAlignment = _perf.HorizontalAlignment == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left;
        _statusBar.IsVisible = settings.ShowSessionStatus && !_isFullScreen;
    }

    /// <summary>The settings changed (here or on the settings page): the bars and the numbers in the picture follow.</summary>
    private void SettingsChanged(object? sender, AppSettings settings) => Ui.Post(ApplyOverlaySettings);

    /// <summary>The session is connected: measure every second (remote desktops only).</summary>
    private void StartStatus()
    {
        _samples.Clear();
        _frames = 0;
        _lastFrames = 0;
        _lastBytes = (_backend as IScreenMeter)?.BytesReceived ?? 0;
        _status.IsVisible = IsScreen;
        _framesChip.Text = "";
        _latencyChip.Text = "Messe …";
        _latencyChip.Tone(null);
        _rateChip.Text = "";
        ApplyOverlaySettings();
        _statusTimer.Start();
    }

    private void StopStatus()
    {
        _statusTimer.Stop();
        _status.IsVisible = false;
        _perf.IsVisible = false;
    }

    /// <summary>One measurement now, as the timer takes one every second (tests: headless has no timers running).</summary>
    internal void MeasureStatus() => Measure();

    private void Measure()
    {
        if (!_isConnected || _backend is not { } backend)
        {
            return;
        }

        var meter = backend as IScreenMeter;
        var bytes = meter?.BytesReceived ?? 0;
        var frames = Volatile.Read(ref _frames);
        var kilobits = Math.Max(0, bytes - _lastBytes) * 8 / 1000.0;
        var shown = Math.Max(0, frames - _lastFrames);
        _lastBytes = bytes;
        _lastFrames = frames;

        // Über Matgate the engine connects to 127.0.0.1: the latency that counts is the one to Matgate.
        var roundTrip = _connection.Via == ConnectVia.Matgate && !_connection.SessionOnMatgate ? MatgateRelay.RoundTripFor(_connection) : meter?.RoundTrip;
        var latency = roundTrip is { } value ? Math.Max(0.1, value.TotalMilliseconds) : (double?)null;

        _samples.Add(new StatusGraph.StatusPoint(shown, latency, kilobits));
        if (_samples.Count > StatusSeconds)
        {
            _samples.RemoveAt(0);
        }

        _framesChip.Text = $"{shown} fps";
        _latencyChip.Text = latency is { } ms ? Milliseconds(ms) : "";
        _latencyChip.Tone(LatencyTone(latency));
        _rateChip.Text = Rate(kilobits);
        _statusDot.Use(Avalonia.Controls.Shapes.Shape.FillProperty, latency switch
        {
            null or < 60 => "Mat.Success",
            < 150 => "Mat.Warning",
            _ => "Mat.Danger",
        });
        _spark.Show([.. _samples]);
        PaintOverlay(shown, latency, kilobits);
        StatusMeasured?.Invoke(this, EventArgs.Empty);

        if (_status.Flyout is Flyout { IsOpen: true })
        {
            ShowDetails();
        }
    }

    private static string? LatencyTone(double? latency) => latency switch
    {
        null => null,
        < 60 => "Mat.SuccessText",
        < 150 => "Mat.WarningText",
        _ => "Mat.DangerText",
    };

    private void PaintOverlay(double shown, double? latency, double kilobits)
    {
        if (!_perf.IsVisible)
        {
            return;
        }

        var size = SettingsService.Get().PerformanceSize;
        var first = $"{shown} fps  {(latency is { } ms ? Milliseconds(ms) : "–")}";
        if (size == "small")
        {
            _perfText.Text = first;
            return;
        }

        var depth = _backend?.ColorDepth is > 0 and < 24 and var bits ? $"  {bits} Bit" : "";
        _perfText.Text = $"{first}  {Rate(kilobits)}\n{PictureSize.Width}×{PictureSize.Height}{depth}  {RouteText()}";
        if (size == "large")
        {
            _perfGraph.Show([.. _samples]);
        }
    }

    private void ShowDetails()
    {
        var latencies = _samples.Where(sample => sample.Latency is not null).Select(sample => sample.Latency!.Value).ToList();
        StatusGraph.StatusPoint? latest = _samples.Count > 0 ? _samples[^1] : null;
        var fps = latest?.Frames ?? 0;
        var rows = new List<(string Label, string Value, string? Tone)>
        {
            ("Zustand", _pill.Text, null),
            ("Verbindung", Protocols.Address(_connection), null),
            ("Weg", RouteText(), null),
            ("Bilder/s", $"{fps} gezeigt" + (_samples.Count > 1 ? $" · Mittel {_samples.Average(sample => sample.Frames):0.#}" : ""), null),
            ("Bildzeit", fps > 0 ? $"{1000 / fps:0} ms" : "–", null),
            ("Latenz (hin und zurück)", latencies.Count == 0
                ? "nicht messbar"
                : $"{Milliseconds(latencies[^1])} · Mittel {Milliseconds(latencies.Average())} · {Milliseconds(latencies.Min())} – {Milliseconds(latencies.Max())}",
                LatencyTone(latencies.Count == 0 ? null : latencies[^1])),
            ("Datenrate", $"{Rate(latest?.Kilobits ?? 0)} · Mittel {Rate(_samples.Count == 0 ? 0 : _samples.Average(sample => sample.Kilobits))}", null),
            ("Empfangen", Bytes((_backend as IScreenMeter)?.BytesReceived ?? 0), null),
            ("Bild", $"{PictureSize.Width} × {PictureSize.Height}" + (_backend?.ColorDepth is > 0 and var depth ? $" · {depth} Bit" : ""), null),
            ("Bildqualität", QualityTitle(_quality), null),
        };

        _details.Children.Clear();
        _details.RowDefinitions.Clear();
        for (var row = 0; row < rows.Count; row++)
        {
            _details.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _details.Children.Add(Ui.Cell(Ui.Colored(rows[row].Label, "Mat.TextMuted", 12), row, 0));
            var value = Ui.Colored(rows[row].Value, rows[row].Tone ?? "Mat.Text", 12);
            value.TextWrapping = TextWrapping.Wrap;
            _details.Children.Add(Ui.Cell(value, row, 1));
        }

        _graph.Show([.. _samples]);
    }

    private static string Milliseconds(double ms) =>
        ms < 10 ? ms.ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + " ms" : Math.Round(ms).ToString(CultureInfo.InvariantCulture) + " ms";

    private static string Rate(double kilobits) => kilobits >= 1000
        ? (kilobits / 1000).ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + " Mbit/s"
        : Math.Round(kilobits).ToString(CultureInfo.InvariantCulture) + " kbit/s";

    private static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + " MB",
        _ => Math.Round(bytes / 1024.0).ToString(CultureInfo.InvariantCulture) + " KB",
    };
}
