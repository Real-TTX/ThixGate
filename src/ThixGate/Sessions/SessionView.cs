using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Matgate.Display;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;
using ThixGate.Dialogs;

namespace ThixGate.Sessions;

/// <summary>
/// One RDP or VNC session, as a tab right of the menu of the main window or in a <see cref="SessionWindow"/> of its own (<see cref="SessionHub"/>
/// puts it there and moves it between them; the session goes on). The engine is Matgate's (Mat.Remote, <see cref="ScreenBackends"/>); this
/// view is its <see cref="IScreenHost"/>: it draws the changed picture straight into a bitmap (no video detour as in the browser), shows the
/// remote pointer, asks for credentials, and sends mouse, wheel and keys in the same messages as Matgate's page. The resolution follows the
/// view ("auto"). A bar at the top: state, Strg+Alt+Entf, Vollbild, Abdocken / Andocken, Trennen / Neu verbinden.
/// </summary>
public sealed class SessionView : UserControl, IScreenHost
{
    private readonly RemoteConnection _connection;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, Focusable = true };
    private readonly Border _surface = new() { ClipToBounds = true, Focusable = true };
    private readonly TextBlock _state = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StatePill _pill = new();
    private readonly TextBlock _overlay = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, TextAlignment = TextAlignment.Center };
    private readonly Button _reconnect = Ui.Button("refresh", "Neu verbinden", "MatButton.Primary");
    private readonly Button _secureAttention = Ui.Button("keyboard", "Strg+Alt+Entf");
    private readonly Button _fullScreen = Ui.Button("zoom-in", "Vollbild");
    private readonly Button _dock = new();
    private readonly Button _disconnect = Ui.Button("power", "Trennen", "MatButton.Danger");
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly HashSet<PhysicalKey> _keysDown = [];
    private WriteableBitmap? _bitmap;
    private IScreenBackend? _backend;
    private CancellationTokenSource? _run;
    private volatile bool _dirty;
    private int _drawPosted;
    private volatile int _wantedWidth;
    private volatile int _wantedHeight;
    private bool _isConnected;
    private bool _started;
    private bool _isDocked;

    public SessionView(RemoteConnection connection)
    {
        _connection = connection;
        this.Use(BackgroundProperty, "Mat.Surface");

        _state.Text = $"{connection.Name} · {Protocols.Address(connection)}";
        _state.Themed("MatText.Muted");
        _overlay.Themed("MatText.Muted");
        _reconnect.IsVisible = false;
        _reconnect.Click += (_, _) => Start();
        _secureAttention.Click += (_, _) =>
        {
            _backend?.SecureAttention();
            _surface.Focus();
        };
        _secureAttention.IsVisible = connection.Protocol == ConnectionProtocol.Rdp;
        _fullScreen.Click += (_, _) => ToggleFullScreen();
        _dock.Click += (_, _) => DockRequested?.Invoke(this, EventArgs.Empty);
        _disconnect.Click += (_, _) => Stop("Getrennt.");
        IsDocked = false;
        AutomationProperties.SetName(_surface, $"Bildschirm von {connection.Name}");

        var bar = new DockPanel { Margin = new Thickness(12, 8), LastChildFill = true };
        bar.Children.Add(Ui.Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _reconnect, _secureAttention, _fullScreen, _dock, _disconnect } }, Dock.Right));
        bar.Children.Add(Ui.Docked(new Border { Margin = new Thickness(0, 0, 12, 0), Child = _pill }, Dock.Left));
        bar.Children.Add(_state);

        _surface.Background = Brushes.Black;
        _surface.Child = new Panel { Children = { _image, _overlay } };
        var root = new DockPanel();
        root.Children.Add(Ui.Docked(new Border { Child = bar }.Use(Border.BackgroundProperty, "Mat.SurfaceRaised"), Dock.Top));
        root.Children.Add(_surface);
        Content = root;

        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            if (_isConnected && _connection.Resolution == "auto")
            {
                _backend?.Resize(_wantedWidth, _wantedHeight);
            }
        };
        _surface.SizeChanged += (_, _) => RememberSize();

        _surface.PointerMoved += (_, e) => Pointer(e);
        _surface.PointerPressed += (_, e) =>
        {
            _surface.Focus();
            Pointer(e);
            Button(e.GetCurrentPoint(_surface).Properties.PointerUpdateKind, true);
            e.Handled = true;
        };
        _surface.PointerReleased += (_, e) =>
        {
            Pointer(e);
            Button(e.GetCurrentPoint(_surface).Properties.PointerUpdateKind, false);
            e.Handled = true;
        };
        _surface.PointerWheelChanged += (_, e) =>
        {
            Pointer(e);
            Send(new JsonObject { ["t"] = "w", ["dy"] = (int)Math.Round(e.Delta.Y * 120), ["dx"] = (int)Math.Round(-e.Delta.X * 120) });
            e.Handled = true;
        };
        AddHandler(KeyDownEvent, (_, e) => Key(e, down: true), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => Key(e, down: false), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _surface.LostFocus += (_, _) => ReleaseAll();
        AttachedToVisualTree += (_, _) =>
        {
            // The first time it is shown the session starts; moving between a tab and a window only shows it elsewhere.
            RememberSize();
            if (!_started)
            {
                _started = true;
                Start();
            }
        };
    }

    /// <summary>"Vollbild" or its key: the host shows the session in full screen (a tab goes into a window of its own for it).</summary>
    public event EventHandler? FullScreenRequested;

    /// <summary>"Abdocken" (in a tab) or "Andocken" (in a window): the hub moves the session.</summary>
    public event EventHandler? DockRequested;

    /// <summary>The state changed (connecting, connected, ended): tabs and the title bar follow.</summary>
    public event EventHandler? StateChanged;

    public RemoteConnection Connection => _connection;

    /// <summary>Shown as a tab of the main window (else in a window of its own): the button says "Abdocken" or "Andocken".</summary>
    public bool IsDocked
    {
        get => _isDocked;
        set
        {
            _isDocked = value;
            _dock.Content = new IconLabel { Icon = value ? "external" : "sidebar", Text = value ? "Abdocken" : "Andocken" };
            ToolTip.SetTip(_dock, value ? "In einem eigenen Fenster zeigen" : "Als Tab ins Hauptfenster");
        }
    }

    /// <summary>The button "Abdocken" / "Andocken" (tests).</summary>
    public Button DockButton => _dock;

    public bool IsConnected => _isConnected;

    public string StateText => _pill.Text;

    /// <summary>The engine has drawn a picture into the window (tests).</summary>
    public bool HasPicture => _bitmap is not null;

    /// <summary>The size of the picture in pixels (tests).</summary>
    public PixelSize PictureSize => _bitmap?.PixelSize ?? default;

    /// <summary>Asks (when the setting says so) before a connected session is closed; true: close it.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!_isConnected || !SettingsService.Get().ConfirmCloseSession)
        {
            return true;
        }

        return await ConfirmDialog.AskAsync(Ui.WindowOf(this), "Sitzung trennen?", $"Die Verbindung zu „{_connection.Name}“ wird getrennt. Auf dem Server laufen die Programme weiter.", "Trennen");
    }

    /// <summary>Ends the session for good (its tab or window closes).</summary>
    public void Shutdown() => Stop(null);

    /// <summary>Puts the keyboard into the session.</summary>
    public void FocusScreen() => _surface.Focus();

    // ================================================================= life cycle

    private void Start()
    {
        _run?.Cancel();
        _run = new CancellationTokenSource();
        var token = _run.Token;
        ShowState("Verbinde …", "refresh", Tone.Accent, $"Verbinde mit {Protocols.Address(_connection)} …");
        _reconnect.IsVisible = false;
        _ = Task.Run(() => RunAsync(token), token);
    }

    private async Task RunAsync(CancellationToken token)
    {
        var login = SessionTargets.LoginOf(_connection);
        IScreenBackend? backend = null;
        try
        {
            backend = await SessionTargets.BackendAsync(_connection, login, _wantedWidth, _wantedHeight, this, token);
            _backend = backend;
            await backend.ConnectAsync(token);
            LibraryService.MarkUsed(_connection.Id);
            _isConnected = true;

            // The first picture is whatever the server drew while signing in: shown at once, not only after the next change.
            RequestDraw();
            Ui.Post(() =>
            {
                ShowState("Verbunden", "check", Tone.Success, null);
                _surface.Focus();
            });
            var reason = await backend.RunAsync(token);
            Ui.Post(() => Ended(reason is { Length: > 0 } ? $"Die Sitzung wurde beendet: {reason}" : "Die Sitzung wurde beendet."));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is ScreenException or BusinessException or IOException or System.Net.Sockets.SocketException
                                              or System.Security.Authentication.AuthenticationException or InvalidOperationException)
        {
            Log.Warning($"Session {_connection.Id}: {exception.GetType().Name}: {exception.Message}");
            Ui.Post(() => Ended(exception is System.Net.Sockets.SocketException
                ? $"{Protocols.Address(_connection)} ist nicht erreichbar ({exception.Message})."
                : exception.Message));
        }
        finally
        {
            _isConnected = false;
            if (backend is not null)
            {
                try
                {
                    await backend.DisposeAsync();
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    Log.Warning($"Session {_connection.Id}: closing: {exception.Message}");
                }
            }

            if (ReferenceEquals(_backend, backend))
            {
                _backend = null;
            }
        }
    }

    private void Stop(string? message)
    {
        ReleaseAll();
        _run?.Cancel();
        _isConnected = false;
        if (message is not null)
        {
            Ended(message);
        }
    }

    private void Ended(string message)
    {
        _isConnected = false;
        ShowState("Getrennt", "x-circle", Tone.Muted, message);
        _reconnect.IsVisible = true;
    }

    private void ShowState(string word, string icon, Tone tone, string? overlay)
    {
        _pill.Show(word, icon, tone);
        StateChanged?.Invoke(this, EventArgs.Empty);
        _overlay.Text = overlay ?? "";
        _overlay.IsVisible = overlay is not null;
        _image.Opacity = overlay is null ? 1 : 0.35;
    }

    private void ToggleFullScreen()
    {
        FullScreenRequested?.Invoke(this, EventArgs.Empty);
        _surface.Focus();
    }

    // ================================================================= picture

    private void RememberSize()
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        _wantedWidth = Math.Max(640, (int)(_surface.Bounds.Width * scale)) & ~1;
        _wantedHeight = Math.Max(400, (int)(_surface.Bounds.Height * scale)) & ~1;
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    /// <summary>
    /// A part of the picture changed (any thread): one draw is posted to the UI thread at render priority; changes that arrive before it
    /// runs are drawn with it (no timer, no drawing while nothing changes).
    /// </summary>
    private void RequestDraw()
    {
        _dirty = true;
        if (Interlocked.Exchange(ref _drawPosted, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _drawPosted, 0);
                DrawIfDirty();
            }, DispatcherPriority.Render);
        }
    }

    private void DrawIfDirty()
    {
        Draws++;
        if (!_dirty || _backend is not { } backend)
        {
            return;
        }

        _dirty = false;
        try
        {
            // No picture yet (the desktop is still coming up): try again at the next tick - a quiet desktop sends no further change.
            if (!backend.WithFrame(CopyFrame))
            {
                Misses++;
                DispatcherTimer.RunOnce(RequestDraw, TimeSpan.FromMilliseconds(30));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            LastDrawError = $"{exception.GetType().Name}: {exception.Message}";
            Log.Warning($"Session {_connection.Id}: the picture could not be drawn: {LastDrawError}");
        }
    }

    /// <summary>How often the picture was drawn (tests).</summary>
    public int Draws { get; private set; }

    /// <summary>How often a picture was wanted but the engine had none yet (tests).</summary>
    public int Misses { get; private set; }

    /// <summary>Why the last picture could not be drawn (tests, the log).</summary>
    public string? LastDrawError { get; private set; }

    /// <summary>Copies the engine's picture (BGRX, top-down) into the bitmap; a new size makes a new bitmap.</summary>
    private void CopyFrame(ReadOnlySpan<byte> bgrx, int stride, int width, int height)
    {
        if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _image.Source = _bitmap;
        }

        using (var target = _bitmap.Lock())
        {
            var rowBytes = width * 4;
            for (var row = 0; row < height; row++)
            {
                var source = bgrx.Slice(row * stride, rowBytes);
                unsafe
                {
                    fixed (byte* from = source)
                    {
                        Buffer.MemoryCopy(from, (byte*)target.Address + (long)row * target.RowBytes, target.RowBytes, rowBytes);
                    }
                }
            }
        }

        _image.InvalidateVisual();
    }

    void IScreenHost.Dirty(int x, int y, int width, int height) => RequestDraw();

    void IScreenHost.Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
        {
            return;
        }

        var pixels = rgba[..(width * height * 4)].ToArray();
        Ui.Post(() =>
        {
            try
            {
                var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
                using (var target = bitmap.Lock())
                {
                    for (var row = 0; row < height; row++)
                    {
                        Marshal.Copy(pixels, row * width * 4, target.Address + row * target.RowBytes, width * 4);
                    }
                }

                _surface.Cursor = new Cursor(bitmap, new PixelPoint(Math.Clamp(hotX, 0, width - 1), Math.Clamp(hotY, 0, height - 1)));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                _surface.Cursor = Avalonia.Input.Cursor.Default;
            }
        });
    }

    void IScreenHost.CursorHidden(bool hidden) =>
        Ui.Post(() => _surface.Cursor = hidden ? new Cursor(StandardCursorType.None) : Avalonia.Input.Cursor.Default);

    void IScreenHost.CursorDefault() => Ui.Post(() => _surface.Cursor = Avalonia.Input.Cursor.Default);

    void IScreenHost.Event(string op, JsonObject data, ReadOnlyMemory<byte> payload)
    {
        // The clipboard of the session (screen.clip …) comes with the next step; nothing else needs an answer here.
    }

    void IScreenHost.Status(string state, string? message)
    {
        if (message is { Length: > 0 })
        {
            Ui.Post(() => _state.Text = $"{_connection.Name} · {message}");
        }
    }

    async Task<ScreenCredentials?> IScreenHost.AskAsync(string[] need, string userName, string domain, bool failed, CancellationToken cancellationToken)
    {
        // A session on Matgate signs in there: what is typed goes to Matgate, nothing is kept here.
        var canRemember = _connection.CredentialId is not null && !AccessPolicy.ArePasswordsDisabled && !_connection.SessionOnMatgate;
        var answer = await Dispatcher.UIThread.InvokeAsync(() => SignInDialog.AskAsync(Ui.WindowOf(this), _connection.Name, need, userName, domain, failed, canRemember));
        if (answer is null)
        {
            return null;
        }

        if (answer.Remember && _connection.CredentialId is { } credentialId && answer.Password.Length > 0)
        {
            try
            {
                CredentialService.RememberPassword(credentialId, answer.Password);
            }
            catch (BusinessException exception)
            {
                Log.Warning($"Password not kept: {exception.Message}");
            }
        }

        return new ScreenCredentials(answer.UserName, answer.Password, answer.Domain);
    }

    void IScreenHost.Audio(ReadOnlySpan<byte> pcm, int rate, int channels)
    {
        // Sound plays with the next step (an audio output of the platform); the engine sends none while it is off.
    }

    (int Width, int Height) IScreenHost.WantedSize() => _connection.Resolution == "auto" ? (_wantedWidth, _wantedHeight) : (0, 0);

    void IScreenHost.Log(string message) => Log.Info($"Session {_connection.Id}: {message}");

    // ================================================================= input

    private void Send(JsonObject message)
    {
        if (_isConnected)
        {
            _backend?.Input(message);
        }
    }

    /// <summary>The pointer in the picture's pixels (the picture is fitted into the window, centred).</summary>
    private void Pointer(PointerEventArgs e)
    {
        if (_bitmap is null)
        {
            return;
        }

        var point = e.GetPosition(_surface);
        var (width, height) = (_bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
        var scale = Math.Min(_surface.Bounds.Width / width, _surface.Bounds.Height / height);
        var offsetX = (_surface.Bounds.Width - width * scale) / 2;
        var offsetY = (_surface.Bounds.Height - height * scale) / 2;
        var x = Math.Clamp((int)((point.X - offsetX) / scale), 0, width - 1);
        var y = Math.Clamp((int)((point.Y - offsetY) / scale), 0, height - 1);
        Send(new JsonObject { ["t"] = "m", ["x"] = x, ["y"] = y });
    }

    private void Button(PointerUpdateKind kind, bool down)
    {
        var button = kind switch
        {
            PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 0,
            PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 1,
            PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 2,
            PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => 3,
            PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => 4,
            _ => -1,
        };
        if (button >= 0)
        {
            Send(new JsonObject { ["t"] = "b", ["b"] = button, ["d"] = down ? 1 : 0 });
        }
    }

    private void Key(KeyEventArgs e, bool down)
    {
        if (!_isConnected || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
        {
            return;
        }

        // Ctrl+Alt+Enter (Windows) / Ctrl+Cmd+F (macOS): leave or enter full screen - the only key ThixGate keeps for itself.
        if (down && ((e.Key == Avalonia.Input.Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                     || (e.Key == Avalonia.Input.Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Meta))))
        {
            e.Handled = true;
            ToggleFullScreen();
            return;
        }

        var (scancode, extended) = KeyMap.ScancodeOf(e.PhysicalKey);
        var keysym = KeyMap.KeysymOf(e.PhysicalKey, e.KeySymbol);
        if (scancode == 0 && keysym == 0)
        {
            return;
        }

        if (down)
        {
            _keysDown.Add(e.PhysicalKey);
        }
        else
        {
            _keysDown.Remove(e.PhysicalKey);
        }

        e.Handled = true;
        Send(new JsonObject
        {
            ["t"] = "k",
            ["s"] = scancode,
            ["e"] = extended ? 1 : 0,
            ["d"] = down ? 1 : 0,
            ["ks"] = keysym,
            ["ag"] = down && e.PhysicalKey == PhysicalKey.AltRight ? 1 : 0,
        });
    }

    /// <summary>Nothing may stay pressed when the session loses the focus (Alt+Tab, a dialog, another tab).</summary>
    private void ReleaseAll()
    {
        _keysDown.Clear();
        Send(new JsonObject { ["t"] = "r" });
    }
}
