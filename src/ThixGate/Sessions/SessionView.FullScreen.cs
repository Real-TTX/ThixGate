using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>
/// Full screen (and a desktop over several monitors): the bars above and below go, and a rounded bar floats at the top of the screen
/// instead - with the state, the name and the way the session goes, the numbers and the last minute, and every button (Anzeige, Ton,
/// Strg+Alt+Entf, Minimieren, Vollbild beenden, Trennen). As the settings say (<see cref="AppSettings.FullScreenBar"/>) it stays, or it waits
/// above the edge: a small handle shows where; the pointer at the top edge or on the handle brings it down, and it goes again a moment
/// after the pointer left it. The pin keeps it.
/// </summary>
public sealed partial class SessionView
{
    private readonly Border _fullBar = new();
    private readonly Border _handle = new();
    private readonly ToggleButton _pin = new() { Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(999) };
    private readonly TextBlock _fullStatus = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly StatusGraph _fullSpark = new() { Width = 56, Height = 16, IsSpark = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _fullSound = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _hideBar = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private bool _isFullScreen;

    /// <summary>Asks the window to go to the task bar / Dock (the bar's "Minimieren").</summary>
    public event EventHandler? MinimizeRequested;

    /// <summary>The session fills the screen (or spans monitors): the floating bar instead of the bars above and below the picture.</summary>
    public bool IsFullScreen
    {
        get => _isFullScreen;
        set
        {
            if (_isFullScreen == value)
            {
                return;
            }

            _isFullScreen = value;
            _barHost.IsVisible = !value;
            ApplyOverlaySettings();
            if (value)
            {
                _pin.IsChecked = SettingsService.Get().FullScreenBar == "always";
                // Shown for a moment, so the user sees where it is; then it waits at the edge (unless it is pinned).
                ShowFullBar(TimeSpan.FromSeconds(2.5));
            }
            else
            {
                _hideBar.Stop();
                _fullBar.IsVisible = false;
                _handle.IsVisible = false;
            }
        }
    }

    /// <summary>The bar at the top is down (tests).</summary>
    internal bool IsFullBarShown => _fullBar.IsVisible;

    /// <summary>The handle at the top edge shows (tests).</summary>
    internal bool IsHandleShown => _handle.IsVisible;

    /// <summary>The bar in full screen (tests).</summary>
    internal Border FullBar => _fullBar;

    private Panel BuildFullBar()
    {
        _fullStatus.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
        foreach (var graph in new[] { _fullSpark })
        {
            graph.Use(StatusGraph.FramesBrushProperty, "Mat.Success");
        }

        StatusMeasured += (_, _) =>
        {
            _fullStatus.Text = StatusLine;
            _fullSpark.Show([.. _samples]);
        };

        _pin.Themed("MatButton.Ghost");
        _pin.Content = new IconLabel { Icon = "pin" };
        AutomationProperties.SetName(_pin, "Leiste immer zeigen");
        ToolTip.SetTip(_pin, "Leiste immer zeigen (sonst kommt sie am oberen Rand)");
        _pin.IsCheckedChanged += (_, _) =>
        {
            var always = _pin.IsChecked == true;
            if ((SettingsService.Get().FullScreenBar == "always") != always)
            {
                SettingsService.Update(settings => settings with { FullScreenBar = always ? "always" : "edge" });
            }

            if (always)
            {
                _hideBar.Stop();
                _fullBar.IsVisible = _isFullScreen;
                _handle.IsVisible = false;
            }
            else if (_isFullScreen && !_fullBar.IsPointerOver)
            {
                _hideBar.Start();
            }
        };

        var title = new TextBlock { Text = _connection.Name, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Use(TextBlock.ForegroundProperty, "Mat.Text");
        var route = new IconLabel { Icon = _connection.Via == ConnectVia.Direct ? "link" : "route", Text = _connection.Via == ConnectVia.Direct ? "Direkt" : _connection.SessionOnMatgate ? "Sitzung auf Matgate" : "Über Matgate" };
        route.Use(TemplatedControl.ForegroundProperty, "Mat.TextMuted");
        ToolTip.SetTip(route, RouteText());
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        dot.Use(Avalonia.Controls.Shapes.Shape.FillProperty, "Mat.Success");
        StateChanged += (_, _) => dot.Use(Avalonia.Controls.Shapes.Shape.FillProperty, _isConnected ? "Mat.Success" : "Mat.TextFaint");

        var display = BarButton("sliders", "Anzeige", () => _display.Flyout?.ShowAt(_fullBar), compact: true);
        display.IsVisible = IsScreenKind;
        _fullSound.Content = new IconLabel { Icon = "bell" };
        ToolTip.SetTip(_fullSound, "Ton");
        _fullSound.IsVisible = _connection.Protocol == ConnectionProtocol.Rdp;
        AutomationProperties.SetName(_fullSound, "Ton");
        _fullSound.IsChecked = _connection.PlaySound;
        _fullSound.IsCheckedChanged += (_, _) =>
        {
            if (_fullSound.IsChecked != _soundOn)
            {
                SetSound(_fullSound.IsChecked == true);
            }
        };
        _sound.IsCheckedChanged += (_, _) => _fullSound.IsChecked = _sound.IsChecked;

        var secureAttention = BarButton("keyboard", "Strg+Alt+Entf", () =>
        {
            _backend?.SecureAttention();
            _surface.Focus();
        }, compact: true);
        secureAttention.IsVisible = _connection.Protocol == ConnectionProtocol.Rdp;
        var minimize = BarButton("minus", "Minimieren", () => MinimizeRequested?.Invoke(this, EventArgs.Empty), compact: true);
        var leave = BarButton("zoom-out", "Vollbild beenden", ToggleFullScreen);
        var disconnect = BarButton("power", "Trennen", () => Stop("Getrennt."));
        disconnect.Themed("MatButton.Danger");

        var separator = new Border { Width = 1, Margin = new Thickness(4, 6) }.Use(Border.BackgroundProperty, "Mat.Border");
        _fullBar.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { _pin, dot, title, route, _fullSpark, _fullStatus, separator, display, _fullSound, secureAttention, minimize, leave, disconnect },
        };
        _fullBar.Padding = new Thickness(8, 6, 8, 6);
        _fullBar.Margin = new Thickness(12, 10, 12, 0);
        _fullBar.HorizontalAlignment = HorizontalAlignment.Center;
        _fullBar.VerticalAlignment = VerticalAlignment.Top;
        _fullBar.CornerRadius = new CornerRadius(14);
        _fullBar.BorderThickness = new Thickness(1);
        _fullBar.Use(Border.BackgroundProperty, "Mat.Surface");
        _fullBar.Use(Border.BorderBrushProperty, "Mat.Border");
        _fullBar.IsVisible = false;
        AutomationProperties.SetName(_fullBar, "Leiste der Sitzung");

        // What happens on the bar stays there: no pointer goes on to the remote desktop.
        _fullBar.PointerPressed += (_, e) => e.Handled = true;
        _fullBar.PointerReleased += (_, e) => e.Handled = true;
        _fullBar.PointerMoved += (_, e) => e.Handled = true;
        _fullBar.PointerEntered += (_, _) => _hideBar.Stop();
        _fullBar.PointerExited += (_, _) => HideSoon();
        _hideBar.Tick += (_, _) =>
        {
            _hideBar.Stop();
            _hideBar.Interval = TimeSpan.FromSeconds(1.2);
            if (_pin.IsChecked != true && !_fullBar.IsPointerOver && !(_display.Flyout?.IsOpen ?? false))
            {
                _fullBar.IsVisible = false;
                _handle.IsVisible = _isFullScreen;
            }
        };

        // The handle: a small grip at the top edge, where the bar waits.
        var grip = new Border { Width = 46, Height = 5, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) }
            .Use(Border.BackgroundProperty, "Mat.TextFaint");
        // The area that takes the pointer is a theme colour at no opacity (no colour of its own), the grip on top of it.
        var area = new Border { Opacity = 0 }.Use(Border.BackgroundProperty, "Mat.Surface");
        _handle.Child = new Panel { Children = { area, grip } };
        _handle.Width = 140;
        _handle.Height = 16;
        _handle.HorizontalAlignment = HorizontalAlignment.Center;
        _handle.VerticalAlignment = VerticalAlignment.Top;
        _handle.Cursor = new Cursor(StandardCursorType.Hand);
        _handle.IsVisible = false;
        ToolTip.SetTip(_handle, "Leiste einblenden");
        AutomationProperties.SetName(_handle, "Leiste einblenden");
        _handle.PointerEntered += (_, _) => ShowFullBar(null);
        _handle.PointerPressed += (_, e) =>
        {
            ShowFullBar(null);
            e.Handled = true;
        };

        return new Panel { Children = { _handle, _fullBar } };
    }

    /// <summary>A remote desktop (also before the view knows its parts: the constructor builds the bar first).</summary>
    private bool IsScreenKind => _connection.Protocol.IsScreen();

    private static Button BarButton(string icon, string text, Action click, bool compact = false)
    {
        // Compact: the icon alone, the words as its tip - the bar stays narrow enough for a laptop screen.
        var button = new Button { Content = compact ? new IconLabel { Icon = icon } : new IconLabel { Icon = icon, Text = text }, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, text);
        ToolTip.SetTip(button, text);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Brings the bar down; with a time it goes again after it (unless pinned or under the pointer).</summary>
    private void ShowFullBar(TimeSpan? stay)
    {
        if (!_isFullScreen)
        {
            return;
        }

        _fullBar.IsVisible = true;
        _handle.IsVisible = false;
        _hideBar.Stop();
        if (stay is { } time && _pin.IsChecked != true)
        {
            _hideBar.Interval = time;
            _hideBar.Start();
        }
    }

    private void HideSoon()
    {
        if (_pin.IsChecked != true && _isFullScreen)
        {
            _hideBar.Interval = TimeSpan.FromSeconds(1.2);
            _hideBar.Start();
        }
    }

    /// <summary>The pointer at the very top of the screen brings the bar down.</summary>
    private void PointerNearTop(PointerEventArgs e)
    {
        if (_isFullScreen && !_fullBar.IsVisible && e.GetPosition(_surface).Y <= 6)
        {
            ShowFullBar(null);
        }
    }
}
