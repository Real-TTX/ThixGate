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
using ThixGate.Core.Business;
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>
/// The status of a remote desktop: latency, frames a second and data rate as a few words in the bar (the latency coloured), and on a click
/// the details with the last minute as a graph. The latency is the TCP round trip the computer measures - to the server, or to Matgate when
/// the session goes über Matgate (the way on from there is Matgate's).
/// </summary>
public sealed partial class SessionView
{
    private const int StatusSeconds = 60;

    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<StatusSample> _samples = [];
    private readonly Button _status = new() { Padding = new Thickness(8, 4), VerticalAlignment = VerticalAlignment.Center };
    private readonly Avalonia.Controls.Shapes.Ellipse _statusDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _statusText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly StatusGraph _graph = new() { Height = 90, MinWidth = 300 };
    private readonly TextBlock _details = new() { FontSize = 12, LineHeight = 20 };
    private int _frames;
    private long _lastBytes;
    private long _lastFrames;
    private string _quality = "";

    /// <summary>One second of a session: latency in ms, frames, kilobits a second (null: not known).</summary>
    private sealed record StatusSample(double? Latency, double Frames, double Kilobits);

    /// <summary>The latest second (tests).</summary>
    internal (double? Latency, double Frames, double Kilobits)? LatestStatus => _samples.Count == 0 ? null : (_samples[^1].Latency, _samples[^1].Frames, _samples[^1].Kilobits);

    /// <summary>The few words of the status in the bar (tests).</summary>
    internal string StatusLine => _statusText.Text ?? "";

    private void BuildStatus()
    {
        _status.Themed("MatButton.Ghost");
        _status.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _statusDot, _statusText } };
        _status.IsVisible = false;
        AutomationProperties.SetName(_status, "Verbindungsstatus");
        ToolTip.SetTip(_status, "Latenz, Bilder pro Sekunde und Datenrate – klicken für Details");
        _statusDot.Use(Avalonia.Controls.Shapes.Shape.FillProperty, "Mat.TextFaint");
        _statusText.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
        _graph.Use(StatusGraph.LineBrushProperty, "Mat.Accent");
        _graph.Use(StatusGraph.SecondBrushProperty, "Mat.Info");
        _graph.Use(StatusGraph.GridBrushProperty, "Mat.Border");
        _details.Use(TextBlock.ForegroundProperty, "Mat.Text");

        var legend = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children = { Legend("Mat.Accent", "Latenz"), Legend("Mat.Info", "Datenrate") },
        };
        var panel = new StackPanel
        {
            Spacing = 10,
            Width = 340,
            Children = { Ui.Text("Verbindung", null, 14, FontWeight.SemiBold), _details, Ui.Colored("Letzte Minute", "Mat.TextMuted", 12), _graph, legend },
        };
        _status.Flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedRight };
        _status.Flyout.Opened += (_, _) => ShowDetails();
        _statusTimer.Tick += (_, _) => Measure();
    }

    private static StackPanel Legend(string colour, string text)
    {
        var mark = new Border { Width = 14, Height = 3, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center }.Use(Border.BackgroundProperty, colour);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { mark, Ui.Colored(text, "Mat.TextMuted", 12) } };
    }

    /// <summary>The session is connected: measure every second (remote desktops only).</summary>
    private void StartStatus(string quality)
    {
        _samples.Clear();
        _frames = 0;
        _lastFrames = 0;
        _lastBytes = (_backend as IScreenMeter)?.BytesReceived ?? 0;
        _quality = quality;
        _status.IsVisible = IsScreen && SettingsService.Get().ShowSessionStatus;
        _statusText.Text = "Messe …";
        _statusTimer.Start();
    }

    private void StopStatus()
    {
        _statusTimer.Stop();
        _status.IsVisible = false;
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

        _samples.Add(new StatusSample(latency, shown, kilobits));
        if (_samples.Count > StatusSeconds)
        {
            _samples.RemoveAt(0);
        }

        var parts = new List<string>();
        if (latency is { } ms)
        {
            parts.Add(Milliseconds(ms));
        }

        parts.Add($"{shown} Bilder/s");
        parts.Add(Rate(kilobits));
        _statusText.Text = string.Join(" · ", parts);
        _statusDot.Use(Avalonia.Controls.Shapes.Shape.FillProperty, latency switch
        {
            null => "Mat.TextFaint",
            < 60 => "Mat.Success",
            < 150 => "Mat.Warning",
            _ => "Mat.Danger",
        });

        if (_status.Flyout is Flyout { IsOpen: true })
        {
            ShowDetails();
        }
    }

    private void ShowDetails()
    {
        var latencies = _samples.Where(sample => sample.Latency is not null).Select(sample => sample.Latency!.Value).ToList();
        var latest = _samples.Count > 0 ? _samples[^1] : null;
        var way = _connection.Via == ConnectVia.Direct
            ? "direkt"
            : _connection.SessionOnMatgate ? "Sitzung auf Matgate (Latenz bis Matgate)" : "über Matgate (Latenz bis Matgate)";
        var lines = new List<string>
        {
            "Latenz: " + (latencies.Count == 0
                ? "nicht messbar"
                : $"{Milliseconds(latencies[^1])} (Mittel {Milliseconds(latencies.Average())}, {Milliseconds(latencies.Min())} – {Milliseconds(latencies.Max())})"),
            $"Bilder pro Sekunde: {latest?.Frames ?? 0}",
            $"Datenrate: {Rate(latest?.Kilobits ?? 0)} (Mittel {Rate(_samples.Count == 0 ? 0 : _samples.Average(sample => sample.Kilobits))})",
            $"Empfangen: {Bytes((_backend as IScreenMeter)?.BytesReceived ?? 0)}",
            $"Bild: {PictureSize.Width} × {PictureSize.Height}" + (_backend?.ColorDepth is > 0 and var depth ? $", {depth} Bit" : ""),
            $"Weg: {way}",
            $"Qualität: {_quality}",
        };
        _details.Text = string.Join("\n", lines);
        _graph.Show([.. _samples.Select(sample => sample.Latency)], [.. _samples.Select(sample => (double?)sample.Kilobits)], StatusSeconds);
    }

    private static string Milliseconds(double ms) =>
        ms < 10 ? ms.ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + " ms" : Math.Round(ms).ToString(CultureInfo.InvariantCulture) + " ms";

    private static string Rate(double kilobits) => kilobits >= 1000
        ? (kilobits / 1000).ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + " MBit/s"
        : Math.Round(kilobits).ToString(CultureInfo.InvariantCulture) + " kBit/s";

    private static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + " MB",
        _ => Math.Round(bytes / 1024.0).ToString(CultureInfo.InvariantCulture) + " KB",
    };
}
