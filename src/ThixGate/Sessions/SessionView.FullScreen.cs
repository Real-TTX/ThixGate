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
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>
/// Full screen (and a desktop over several monitors): the bar at the top goes, and a small bar waits at the top edge of the screen instead,
/// as with Remote Desktop's own client - it comes down when the pointer touches the edge and goes again after a moment, unless it is
/// pinned. It has the name and the latency, Strg+Alt+Entf, Minimieren, Vollbild beenden and Trennen.
/// </summary>
public sealed partial class SessionView
{
    private readonly Border _fullBar = new();
    private readonly ToggleButton _pin = new() { Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _fullName = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _fullStatus = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly DispatcherTimer _hideBar = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _isFullScreen;

    /// <summary>Asks the window to go to the task bar / Dock (the bar's "Minimieren").</summary>
    public event EventHandler? MinimizeRequested;

    /// <summary>The session fills the screen (or spans monitors): the bar at the top edge instead of the bar above the picture.</summary>
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
            if (value)
            {
                // Shown for a moment, so the user sees where it is; then it waits at the edge.
                ShowFullBar();
            }
            else
            {
                _hideBar.Stop();
                _fullBar.IsVisible = false;
            }
        }
    }

    /// <summary>The bar at the top edge is down (tests).</summary>
    internal bool IsFullBarShown => _fullBar.IsVisible;

    /// <summary>The bar at the top edge (tests).</summary>
    internal Border FullBar => _fullBar;

    private Border BuildFullBar()
    {
        _fullName.Text = _connection.Name;
        _fullName.Use(TextBlock.ForegroundProperty, "Mat.Text");
        _fullStatus.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
        _statusText.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty)
            {
                _fullStatus.Text = _statusText.Text;
            }
        };

        _pin.Themed("MatButton.Icon");
        _pin.Content = new IconLabel { Icon = "pin" };
        AutomationProperties.SetName(_pin, "Leiste anheften");
        ToolTip.SetTip(_pin, "Leiste anheften");
        _pin.IsCheckedChanged += (_, _) =>
        {
            if (_pin.IsChecked == true)
            {
                _hideBar.Stop();
            }
            else
            {
                _hideBar.Start();
            }
        };

        var secureAttention = BarButton("keyboard", "Strg+Alt+Entf", () =>
        {
            _backend?.SecureAttention();
            _surface.Focus();
        });
        secureAttention.IsVisible = _connection.Protocol == ConnectionProtocol.Rdp;
        var minimize = BarButton("minus", "Minimieren", () => MinimizeRequested?.Invoke(this, EventArgs.Empty));
        var leave = BarButton("zoom-out", "Vollbild beenden", ToggleFullScreen);
        var disconnect = BarButton("power", "Trennen", () => Stop("Getrennt."));
        disconnect.Themed("MatButton.Danger");

        _fullBar.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                _pin,
                new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center }.Use(Avalonia.Controls.Shapes.Shape.FillProperty, "Mat.Accent"),
                _fullName,
                _fullStatus,
                new Border { Width = 8 },
                secureAttention,
                minimize,
                leave,
                disconnect,
            },
        };
        _fullBar.Padding = new Thickness(10, 6);
        _fullBar.HorizontalAlignment = HorizontalAlignment.Center;
        _fullBar.VerticalAlignment = VerticalAlignment.Top;
        _fullBar.CornerRadius = new CornerRadius(0, 0, 10, 10);
        _fullBar.BorderThickness = new Thickness(1, 0, 1, 1);
        _fullBar.Use(Border.BackgroundProperty, "Mat.SurfaceRaised");
        _fullBar.Use(Border.BorderBrushProperty, "Mat.Border");
        _fullBar.IsVisible = false;
        AutomationProperties.SetName(_fullBar, "Leiste der Sitzung");

        // What happens on the bar stays there: no pointer goes on to the remote desktop.
        _fullBar.PointerPressed += (_, e) => e.Handled = true;
        _fullBar.PointerReleased += (_, e) => e.Handled = true;
        _fullBar.PointerMoved += (_, e) => e.Handled = true;
        _fullBar.PointerEntered += (_, _) => _hideBar.Stop();
        _fullBar.PointerExited += (_, _) =>
        {
            if (_pin.IsChecked != true)
            {
                _hideBar.Start();
            }
        };
        _hideBar.Tick += (_, _) =>
        {
            _hideBar.Stop();
            if (_pin.IsChecked != true && !_fullBar.IsPointerOver)
            {
                _fullBar.IsVisible = false;
            }
        };
        return _fullBar;
    }

    private static Button BarButton(string icon, string text, Action click)
    {
        var button = new Button { Content = new IconLabel { Icon = icon, Text = text }, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => click();
        return button;
    }

    private void ShowFullBar()
    {
        _fullBar.IsVisible = true;
        _hideBar.Stop();
        if (_pin.IsChecked != true)
        {
            _hideBar.Interval = TimeSpan.FromSeconds(2.5);
            _hideBar.Start();
            _hideBar.Tick += ResetInterval;
        }
    }

    private void ResetInterval(object? sender, EventArgs e)
    {
        _hideBar.Interval = TimeSpan.FromSeconds(1.5);
        _hideBar.Tick -= ResetInterval;
    }

    /// <summary>The pointer at the very top of the screen brings the bar down.</summary>
    private void PointerNearTop(PointerEventArgs e)
    {
        if (_isFullScreen && !_fullBar.IsVisible && e.GetPosition(_surface).Y <= 2)
        {
            ShowFullBar();
        }
    }
}
