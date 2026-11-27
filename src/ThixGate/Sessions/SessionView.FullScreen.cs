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
/// Full screen (and a desktop over several monitors): the bars above and below go, and a connection bar sits at the top edge instead -
/// as in Microsoft's Windows App: flush with the edge and rounded below, a menu on the left (Verbindungsinformationen, Anzeige, Ton,
/// Strg+Alt+Entf, Leiste immer zeigen), the state, name and way of the session, and on the right Minimieren, Vollbild beenden, Trennen.
/// It stays hidden until the pointer rests at the top edge for a moment (or touches the grip there) and goes again a moment after
/// the pointer moved away from it; entering full screen does not show it. The pointer is followed in the whole window (also where the
/// picture or a panel takes it), and where it was last counts: on macOS the system's menu bar comes down at the top edge and takes the
/// pointer - the bar then waits below it instead of going. As the settings say (<see cref="AppSettings.FullScreenBar"/>) it can instead
/// always show ("Leiste immer zeigen" in its menu).
/// </summary>
public sealed partial class SessionView
{
    /// <summary>How long the pointer rests at the top edge before the bar comes (so a quick move to the top does not bring it).</summary>
    private static readonly TimeSpan RevealDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>How close to the top edge the pointer brings the bar.</summary>
    private const double RevealZone = 6;

    /// <summary>How far below the bar the pointer may go before it hides (room for the macOS menu bar over it).</summary>
    private const double StayZone = 48;

    /// <summary>Where the pointer was last seen in the window, from the top of the session (NaN: not yet).</summary>
    private double _pointerTop = double.NaN;

    private readonly Border _fullBar = new();
    private readonly Border _handle = new();
    private readonly Button _barMenu = new() { VerticalAlignment = VerticalAlignment.Stretch, Padding = new Thickness(10, 0) };
    private readonly MenuItem _alwaysItem = new() { Header = "Leiste immer zeigen", ToggleType = MenuItemToggleType.CheckBox };
    private readonly MenuItem _soundItem = new() { Header = "Ton", ToggleType = MenuItemToggleType.CheckBox };
    private readonly DispatcherTimer _hideBar = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _revealBar = new() { Interval = RevealDelay };
    private bool _isFullScreen;

    /// <summary>Asks the window to go to the task bar / Dock (the bar's "Minimieren").</summary>
    public event EventHandler? MinimizeRequested;

    /// <summary>The session fills the screen (or spans monitors): the connection bar instead of the bars above and below the picture.</summary>
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
            _hideBar.Stop();
            _revealBar.Stop();
            // Pinned it shows at once; otherwise it waits at the edge until the pointer asks for it.
            var always = value && IsBarPinned;
            _fullBar.IsVisible = always;
            _handle.IsVisible = value && !always;
        }
    }

    /// <summary>The bar is down (tests).</summary>
    internal bool IsFullBarShown => _fullBar.IsVisible;

    /// <summary>The grip at the top edge shows (tests).</summary>
    internal bool IsHandleShown => _handle.IsVisible;

    /// <summary>The bar in full screen (tests).</summary>
    internal Border FullBar => _fullBar;

    /// <summary>The pointer rests at the top edge (tests: as if it had rested long enough).</summary>
    internal void RevealBarNow() => ShowFullBar();

    /// <summary>The pointer is at the top edge and the bar comes when it rests there a moment longer (tests).</summary>
    internal bool IsRevealPending => _revealBar.IsEnabled;

    private static bool IsBarPinned => SettingsService.Get().FullScreenBar == "always";

    private Panel BuildFullBar()
    {
        // Left: the menu, then the state, name and way; right of a line the window's buttons.
        _barMenu.Themed("MatButton.Ghost");
        _barMenu.Content = new IconLabel { Icon = "menu" };
        AutomationProperties.SetName(_barMenu, "Menü der Sitzung");
        ToolTip.SetTip(_barMenu, "Menü");
        _barMenu.Flyout = BuildBarMenu();

        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        dot.Use(Avalonia.Controls.Shapes.Shape.FillProperty, "Mat.Success");
        StateChanged += (_, _) => dot.Use(Avalonia.Controls.Shapes.Shape.FillProperty, _isConnected ? "Mat.Success" : "Mat.TextFaint");
        var title = new TextBlock { Text = _connection.Name, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Use(TextBlock.ForegroundProperty, "Mat.Text");
        var route = new TextBlock { Text = _connection.Via == ConnectVia.Direct ? "Direkt" : _connection.SessionOnMatgate ? "Sitzung auf Matgate" : "Über Matgate", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        route.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
        ToolTip.SetTip(route, RouteText());

        var minimize = WindowButton("minus", "Minimieren", () => MinimizeRequested?.Invoke(this, EventArgs.Empty));
        var leave = WindowButton("zoom-out", "Vollbild beenden", ToggleFullScreen);
        var disconnect = WindowButton("x", "Trennen", () => Stop("Getrennt."));

        var separator = new Border { Width = 1, Margin = new Thickness(10, 8, 4, 8) }.Use(Border.BackgroundProperty, "Mat.Border");
        _fullBar.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _barMenu, dot, title, route, separator, minimize, leave, disconnect },
        };
        _fullBar.MinWidth = 420;
        _fullBar.Height = 40;
        _fullBar.Padding = new Thickness(4, 0, 4, 0);
        _fullBar.HorizontalAlignment = HorizontalAlignment.Center;
        _fullBar.VerticalAlignment = VerticalAlignment.Top;
        // Flush with the top edge, rounded below (Windows App).
        _fullBar.CornerRadius = new CornerRadius(0, 0, 10, 10);
        _fullBar.BorderThickness = new Thickness(1, 0, 1, 1);
        _fullBar.Use(Border.BackgroundProperty, "Mat.Surface");
        _fullBar.Use(Border.BorderBrushProperty, "Mat.Border");
        _fullBar.IsVisible = false;
        AutomationProperties.SetName(_fullBar, "Leiste der Sitzung");

        // What happens on the bar stays there: no pointer goes on to the remote desktop.
        _fullBar.PointerPressed += (_, e) => e.Handled = true;
        _fullBar.PointerReleased += (_, e) => e.Handled = true;
        _fullBar.PointerMoved += (_, e) => e.Handled = true;
        _hideBar.Tick += (_, _) =>
        {
            _hideBar.Stop();
            HideBarIfAway();
        };
        _revealBar.Tick += (_, _) =>
        {
            _revealBar.Stop();
            ShowFullBar();
        };

        // The grip: a small tab at the top edge where the bar waits (seen on a dark and a light picture alike); touching it brings the bar.
        var line = new Border { Width = 28, Height = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            .Use(Border.BackgroundProperty, "Mat.TextMuted");
        var tab = new Border
        {
            Width = 64,
            Height = 10,
            CornerRadius = new CornerRadius(0, 0, 5, 5),
            BorderThickness = new Thickness(1, 0, 1, 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0.9,
            Child = line,
        }.Use(Border.BackgroundProperty, "Mat.Surface").Use(Border.BorderBrushProperty, "Mat.Border");
        // The area that takes the pointer is wider and taller than the tab: a theme colour at no opacity (no colour of its own).
        var area = new Border { Opacity = 0 }.Use(Border.BackgroundProperty, "Mat.Surface");
        _handle.Child = new Panel { Children = { area, tab } };
        _handle.Width = 180;
        _handle.Height = 16;
        _handle.HorizontalAlignment = HorizontalAlignment.Center;
        _handle.VerticalAlignment = VerticalAlignment.Top;
        _handle.IsVisible = false;
        ToolTip.SetTip(_handle, "Leiste einblenden");
        AutomationProperties.SetName(_handle, "Leiste einblenden");
        _handle.PointerEntered += (_, _) => ShowFullBar();
        _handle.PointerPressed += (_, e) =>
        {
            ShowFullBar();
            e.Handled = true;
        };

        return new Panel { Children = { _handle, _fullBar } };
    }

    /// <summary>The bar's menu (Windows App: Connection information, Monitor configuration, Toolbar options).</summary>
    private MenuFlyout BuildBarMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var information = new MenuItem { Header = "Verbindungsinformationen", Icon = new IconLabel { Icon = "activity" } };
        information.Click += (_, _) => ShowFromBar(_status.Flyout);
        menu.Items.Add(information);

        if (IsScreenKind)
        {
            var display = new MenuItem { Header = "Anzeige …", Icon = new IconLabel { Icon = "sliders" } };
            display.Click += (_, _) => ShowFromBar(_display.Flyout);
            menu.Items.Add(display);
        }

        if (_connection.Protocol == ConnectionProtocol.Rdp)
        {
            _soundItem.Icon = new IconLabel { Icon = "bell" };
            _soundItem.Click += (_, _) => SetSound(!_soundOn);
            menu.Items.Add(_soundItem);
            var secureAttention = new MenuItem { Header = "Strg+Alt+Entf senden", Icon = new IconLabel { Icon = "keyboard" } };
            secureAttention.Click += (_, _) =>
            {
                _backend?.SecureAttention();
                _surface.Focus();
            };
            menu.Items.Add(secureAttention);
        }

        menu.Items.Add(new Separator());
        _alwaysItem.Icon = new IconLabel { Icon = "pin" };
        _alwaysItem.Click += (_, _) =>
        {
            var always = !IsBarPinned;
            SettingsService.Update(settings => settings with { FullScreenBar = always ? "always" : "edge" });
            ApplyBarSetting();
        };
        menu.Items.Add(_alwaysItem);

        menu.Opening += (_, _) =>
        {
            _soundItem.IsChecked = _soundOn;
            _alwaysItem.IsChecked = IsBarPinned;
        };
        menu.Closed += (_, _) => HideSoon();
        return menu;
    }

    /// <summary>A panel of the session (details, Anzeige) below the bar; it goes back to its own place when it closes.</summary>
    private void ShowFromBar(FlyoutBase? flyout)
    {
        if (flyout is not PopupFlyoutBase popup)
        {
            return;
        }

        var placement = popup.Placement;
        popup.Placement = PlacementMode.BottomEdgeAlignedLeft;
        void Restore(object? sender, EventArgs e)
        {
            popup.Closed -= Restore;
            popup.Placement = placement;
            HideSoon();
        }

        popup.Closed += Restore;
        popup.ShowAt(_fullBar);
    }

    /// <summary>A menu or panel of the bar is open: the bar stays.</summary>
    private bool IsBarBusy => (_barMenu.Flyout?.IsOpen ?? false) || (_display.Flyout?.IsOpen ?? false) || (_status.Flyout?.IsOpen ?? false);

    /// <summary>A remote desktop (also before the view knows its parts: the constructor builds the bar first).</summary>
    private bool IsScreenKind => _connection.Protocol.IsScreen();

    private static Button WindowButton(string icon, string text, Action click)
    {
        // Like a window's caption buttons: the icon alone, the words as its tip.
        var button = new Button { Content = new IconLabel { Icon = icon }, VerticalAlignment = VerticalAlignment.Stretch, Padding = new Thickness(14, 0), MinWidth = 44 };
        button.Themed("MatButton.Ghost");
        AutomationProperties.SetName(button, text);
        ToolTip.SetTip(button, text);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Brings the bar down; it goes again when the pointer leaves it (unless pinned).</summary>
    private void ShowFullBar()
    {
        if (!_isFullScreen)
        {
            return;
        }

        _revealBar.Stop();
        _hideBar.Stop();
        _fullBar.IsVisible = true;
        _handle.IsVisible = false;
        HideSoon();
    }

    /// <summary>The hide timer ran out: the bar goes unless pinned, busy (a menu or panel of it is open) or the pointer is still near it.</summary>
    internal void HideBarIfAway()
    {
        if (IsBarPinned || !_isFullScreen || !_fullBar.IsVisible)
        {
            return;
        }

        if (IsBarBusy || IsPointerNearBar)
        {
            // Look again a moment later.
            _hideBar.Start();
            return;
        }

        _fullBar.IsVisible = false;
        _handle.IsVisible = true;
    }

    /// <summary>The pointer was last seen on the bar or just below it (on macOS also: last seen there before the menu bar took it).</summary>
    private bool IsPointerNearBar => !double.IsNaN(_pointerTop) && _pointerTop <= BarBottom + StayZone;

    private double BarBottom => _fullBar.Bounds.Height > 0 ? _fullBar.Bounds.Bottom : _fullBar.Height;

    /// <summary>"Leiste immer zeigen" changed (menu or settings): pinned it shows at once, else it goes when the pointer is not on it.</summary>
    private void ApplyBarSetting()
    {
        if (!_isFullScreen)
        {
            return;
        }

        if (IsBarPinned)
        {
            _hideBar.Stop();
            _fullBar.IsVisible = true;
            _handle.IsVisible = false;
        }
        else
        {
            HideSoon();
        }
    }

    private void HideSoon()
    {
        if (!IsBarPinned && _isFullScreen && _fullBar.IsVisible && !_hideBar.IsEnabled)
        {
            _hideBar.Start();
        }
    }

    /// <summary>A pointer move anywhere in the window (tunnelled, also when something below handles it).</summary>
    private void WindowPointerMoved(object? sender, PointerEventArgs e) => TrackPointer(e.GetPosition(this).Y);

    /// <summary>
    /// The pointer is at <paramref name="top"/> (from the top of the session): resting at the top edge brings the bar (moving away before
    /// it came does not); moving away from the shown bar lets it go a moment later.
    /// </summary>
    internal void TrackPointer(double top)
    {
        _pointerTop = top;
        if (!_isFullScreen)
        {
            return;
        }

        if (_fullBar.IsVisible)
        {
            if (!IsPointerNearBar)
            {
                HideSoon();
            }

            return;
        }

        if (top <= RevealZone)
        {
            if (!_revealBar.IsEnabled)
            {
                _revealBar.Start();
            }
        }
        else
        {
            _revealBar.Stop();
        }
    }
}
