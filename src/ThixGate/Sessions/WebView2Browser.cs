#if WINDOWS
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using ThixGate.Core.Business;
using ThixGate.Core.Models;
using ThixGate.Core.Infrastructure;

namespace ThixGate.Sessions;

/// <summary>
/// The browser of a website tab on Windows: WebView2 (Edge's engine, on every Windows 10/11) in a child window of the tab. Its own data
/// folder per connection; über Matgate with the proxy of <see cref="WebRelayProxy"/>; HTTP Basic answered once with the connection's login
/// (a second request: Edge asks); a certificate error accepted when the connection says so; a new window opens in the same tab.
/// </summary>
internal sealed class WebView2Browser : NativeControlHost, IBrowser
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;

    private readonly TaskCompletionSource<IntPtr> _window = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr _hwnd;
    private CoreWebView2Controller? _controller;
    private BrowserOptions? _options;
    private bool _answeredBasic;

    public WebView2Browser()
    {
        SizeChanged += (_, _) => Fit();
    }

    public event Action<string>? AddressChanged;

    public event Action<string>? TitleChanged;

    public Control Control => this;

    /// <summary>The first page finished loading (true) or failed (false) - the self test waits for it.</summary>
    public Task<bool> FirstPage => _loaded.Task;

    /// <summary>The version of the WebView2 runtime (the self test reports it).</summary>
    public string EngineVersion { get; private set; } = "";

    public async Task StartAsync(BrowserOptions options)
    {
        _options = options;
        var hwnd = await _window.Task;
        try
        {
            Directory.CreateDirectory(options.DataFolder);
            var arguments = options.Proxy is null ? null : $"--proxy-server={options.Proxy} --proxy-bypass-list=<-loopback>";
            var environment = await CoreWebView2Environment.CreateAsync(null, options.DataFolder, new CoreWebView2EnvironmentOptions(arguments));
            EngineVersion = environment.BrowserVersionString;
            _controller = await environment.CreateCoreWebView2ControllerAsync(hwnd);
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or COMException or ArgumentException or InvalidOperationException)
        {
            Log.Warning($"WebView2: {exception.Message}");
            throw new BusinessException("Der Browser von Windows (WebView2) fehlt oder startet nicht – „Im Browser öffnen“ zeigt die Website.");
        }

        var core = _controller.CoreWebView2;
        core.Settings.IsStatusBarEnabled = true;
        core.SourceChanged += (_, _) => AddressChanged?.Invoke(core.Source);
        core.DocumentTitleChanged += (_, _) => TitleChanged?.Invoke(core.DocumentTitle);
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };
        core.ServerCertificateErrorDetected += (_, e) =>
        {
            if (options.IgnoreCertificate)
            {
                e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
            }
        };
        core.BasicAuthenticationRequested += (_, e) =>
        {
            // Matgate's way: the stored login answers the first request; refused, Edge asks the user.
            if (!_answeredBasic && options.BasicCredentials() is { } login)
            {
                _answeredBasic = true;
                e.Response.UserName = login.UserName;
                e.Response.Password = login.Password;
            }
        };
        core.NavigationCompleted += (_, e) =>
        {
            _loaded.TrySetResult(e.IsSuccess);
            if (e.IsSuccess)
            {
                _answeredBasic = false;
            }
        };
        foreach (var cookie in options.Cookies)
        {
            var made = core.CookieManager.CreateCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
            made.IsSecure = options.Start.Scheme == "https";
            core.CookieManager.AddOrUpdateCookie(made);
        }

        Fit();
        _controller.IsVisible = true;
        core.Navigate(options.Start.ToString());
    }

    public void Navigate(Uri address) => _controller?.CoreWebView2.Navigate(address.ToString());

    public void Back()
    {
        if (_controller?.CoreWebView2 is { CanGoBack: true } core)
        {
            core.GoBack();
        }
    }

    public void Forward()
    {
        if (_controller?.CoreWebView2 is { CanGoForward: true } core)
        {
            core.GoForward();
        }
    }

    public void Reload() => _controller?.CoreWebView2.Reload();

    public void Close()
    {
        _controller?.Close();
        _controller = null;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _hwnd = CreateWindowEx(0, "Static", "", WsChild | WsVisible | WsClipChildren, 0, 0, 100, 100, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _window.TrySetResult(_hwnd);
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Close();
        DestroyWindow(control.Handle);
        _hwnd = IntPtr.Zero;
    }

    private void Fit()
    {
        if (_controller is null || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, (int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
#endif
