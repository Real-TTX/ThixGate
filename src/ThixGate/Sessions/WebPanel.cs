using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using ThixGate.Controls;
using ThixGate.Core.Business;
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>What the browser of a website tab is given: its data folder, a proxy (über Matgate), certificates, the sign-ins.</summary>
internal sealed record BrowserOptions(
    Uri Start,
    string DataFolder,
    string? Proxy,
    bool IgnoreCertificate,
    Func<(string UserName, string Password)?> BasicCredentials,
    IReadOnlyList<WebCookie> Cookies);

/// <summary>The browser engine of the platform in a control: WebView2 (Windows), WKWebView (macOS).</summary>
internal interface IBrowser
{
    Control Control { get; }

    /// <summary>The address shown changed.</summary>
    event Action<string>? AddressChanged;

    /// <summary>The page's title changed.</summary>
    event Action<string>? TitleChanged;

    /// <summary>Starts the engine and opens the start address; throws a BusinessException when the engine is missing.</summary>
    Task StartAsync(BrowserOptions options);

    void Navigate(Uri address);

    void Back();

    void Forward();

    void Reload();

    void Close();
}

/// <summary>
/// A website in a tab (as Matgate's website tab): ThixGate's browser - the system's engine - with Zurück, Vor, Neu laden, the address field
/// (Enter opens it; without a scheme https://) and "Im Browser öffnen". Matgate's sign-ins: HTTP Basic with the connection's login,
/// Proxmox VE / Backup Server with a ticket; a self-signed certificate is accepted when the connection says so. Über Matgate the site goes
/// through Matgate's relay (a proxy on this computer). Cookies and sign-ins stay per connection.
/// </summary>
public sealed class WebPanel : UserControl, ISessionPanel
{
    private readonly RemoteConnection _connection;
    private readonly TextBox _address = new();
    private readonly Button _back = Ui.Button("chevron-left", "");
    private readonly Button _forward = Ui.Button("chevron-right", "");
    private readonly Button _reload = Ui.Button("refresh", "");
    private readonly Button _external = Ui.Button("external", "Im Browser öffnen");
    private readonly Border _host = new();
    private IBrowser? _browser;
    private WebRelayProxy? _proxy;

    public WebPanel(RemoteConnection connection)
    {
        _connection = connection;
        this.Use(BackgroundProperty, "Mat.Surface");
        AutomationProperties.SetName(_address, "Adresse");
        foreach (var (button, tip) in new[] { (_back, "Zurück"), (_forward, "Vor"), (_reload, "Neu laden") })
        {
            ToolTip.SetTip(button, tip);
            AutomationProperties.SetName(button, tip);
        }

        _back.Click += (_, _) => _browser?.Back();
        _forward.Click += (_, _) => _browser?.Forward();
        _reload.Click += (_, _) => _browser?.Reload();
        _external.Click += (_, _) => Ui.OpenExternal(_address.Text is { Length: > 0 } text ? text : _connection.Url);
        _address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Uri.TryCreate(LibraryService.WebAddress(_address.Text ?? ""), UriKind.Absolute, out var address))
            {
                e.Handled = true;
                _browser?.Navigate(address);
            }
        };

        var bar = new DockPanel { Margin = new Thickness(12, 8) };
        bar.Children.Add(Ui.Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _back, _forward, _reload } }, Dock.Left));
        bar.Children.Add(Ui.Docked(new Border { Margin = new Thickness(8, 0, 0, 0), Child = _external }, Dock.Right));
        bar.Children.Add(new Border { Margin = new Thickness(8, 0, 0, 0), Child = _address });
        var root = new DockPanel();
        root.Children.Add(Ui.Docked(bar, Dock.Top));
        root.Children.Add(_host);
        Content = root;
        _address.Text = connection.Url;
        IsEnabled = false;
    }

    /// <summary>Makes the browser of this platform (tests give their own).</summary>
    internal static Func<IBrowser?> BrowserFactory { get; set; } = DefaultBrowser;

    public Control View => this;

    /// <summary>The address shown (tests).</summary>
    public string Address => _address.Text ?? "";

    /// <summary>The page's title, shown in the bar of the session.</summary>
    public event Action<string>? TitleChanged;

    private static IBrowser? DefaultBrowser()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return new WebView2Browser();
        }
#endif
        return OperatingSystem.IsMacOS() ? new WebKitBrowser() : null;
    }

    public async Task ConnectAsync(AskSignIn ask, CancellationToken cancellationToken)
    {
        var start = WebSites.StartAddress(_connection);
        var proxy = await WebSites.ProxyAsync(_connection, cancellationToken);
        var cookies = new List<WebCookie>();
        if (await WebSites.ProxmoxSignInAsync(_connection, proxy is null ? null : ProxyHandler(proxy), cancellationToken) is { } ticket)
        {
            cookies.Add(ticket);
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            _proxy = proxy;
            var browser = BrowserFactory() ?? throw new BusinessException("Auf diesem System gibt es keinen eingebauten Browser – „Im Browser öffnen“ zeigt die Website.");
            browser.AddressChanged += address => Ui.Post(() => _address.Text = address);
            browser.TitleChanged += title => Ui.Post(() => TitleChanged?.Invoke(title));
            _host.Child = browser.Control;
            _browser = browser;
            IsEnabled = true;
            await browser.StartAsync(new BrowserOptions(start, WebSites.DataFolder(_connection), proxy?.Address, _connection.IgnoreCertificate,
                () => WebSites.BasicCredentials(_connection), cookies));
            LibraryService.MarkUsed(_connection.Id);
        });
    }

    private static HttpMessageHandler ProxyHandler(WebRelayProxy proxy) => new HttpClientHandler
    {
        Proxy = new System.Net.WebProxy(proxy.Address),
        UseProxy = true,
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    };

    public void Disconnect()
    {
        _browser?.Close();
        _browser = null;
        _host.Child = null;
        _proxy?.Dispose();
        _proxy = null;
        IsEnabled = false;
    }

    public void FocusPanel() => _browser?.Control.Focus();
}
