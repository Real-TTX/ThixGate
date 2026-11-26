using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using ThixGate.Core.Business;
using ThixGate.Core.Models;

namespace ThixGate.Sessions;

/// <summary>
/// The browser of a website tab on macOS: WKWebView (Safari's engine) as the tab's native view, driven through the Objective-C runtime -
/// no binding library. Address and title are read twice a second; a sign-in cookie (Proxmox) is set by the page's script once it has
/// loaded. HTTP Basic, certificate exceptions and the proxy for "über Matgate" need WebKit delegates this host does not have: WebKit asks
/// the user itself, and a website über Matgate opens on macOS only through "Im Browser öffnen".
/// </summary>
internal sealed class WebKitBrowser : NativeControlHost, IBrowser
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TaskCompletionSource<IntPtr> _view = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr _webView;
    private string _address = "";
    private string _title = "";
    private IReadOnlyList<WebCookie> _cookies = [];
    private bool _cookiesSet;

    public WebKitBrowser()
    {
        _poll.Tick += (_, _) => Poll();
    }

    public event Action<string>? AddressChanged;

    public event Action<string>? TitleChanged;

    public Control Control => this;

    public async Task StartAsync(BrowserOptions options)
    {
        if (options.Proxy is not null)
        {
            throw new BusinessException("Websites über Matgate zeigt ThixGate auf dem Mac noch nicht im Tab – „Im Browser öffnen“ geht.");
        }

        _cookies = options.Cookies;
        await _view.Task;
        Navigate(options.Start);
        _poll.Start();
    }

    public void Navigate(Uri address)
    {
        if (_webView == IntPtr.Zero)
        {
            return;
        }

        var url = Send(Class("NSURL"), Selector("URLWithString:"), NSString(address.ToString()));
        var request = Send(Class("NSURLRequest"), Selector("requestWithURL:"), url);
        Send(_webView, Selector("loadRequest:"), request);
    }

    public void Back() => Call("goBack");

    public void Forward() => Call("goForward");

    public void Reload() => Call("reload");

    public void Close()
    {
        _poll.Stop();
        Call("stopLoading");
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var configuration = Send(Send(Class("WKWebViewConfiguration"), Selector("alloc")), Selector("init"));
        _webView = SendRect(Send(Class("WKWebView"), Selector("alloc")), Selector("initWithFrame:configuration:"), new CGRect(0, 0, 800, 600), configuration);
        _view.TrySetResult(_webView);
        return new PlatformHandle(_webView, "NSView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Close();
        Send(control.Handle, Selector("release"));
        _webView = IntPtr.Zero;
    }

    private void Call(string selector)
    {
        if (_webView != IntPtr.Zero)
        {
            Send(_webView, Selector(selector));
        }
    }

    private void Poll()
    {
        if (_webView == IntPtr.Zero)
        {
            return;
        }

        var url = Send(_webView, Selector("URL"));
        var address = url == IntPtr.Zero ? "" : Text(Send(url, Selector("absoluteString")));
        if (address != _address)
        {
            _address = address;
            AddressChanged?.Invoke(address);
        }

        var title = Text(Send(_webView, Selector("title")));
        if (title != _title)
        {
            _title = title;
            TitleChanged?.Invoke(title);
        }

        // The sign-in cookie: set by the page's own script once it is there, then the page loads again with it.
        if (!_cookiesSet && _cookies.Count > 0 && address.Length > 0 && !IsLoading())
        {
            _cookiesSet = true;
            var script = string.Concat(_cookies.Select(cookie => $"document.cookie={System.Text.Json.JsonSerializer.Serialize($"{cookie.Name}={Uri.EscapeDataString(cookie.Value)}; path={cookie.Path}; secure")};"))
                         + "location.reload();";
            Send(_webView, Selector("evaluateJavaScript:completionHandler:"), NSString(script), IntPtr.Zero);
        }
    }

    private bool IsLoading() => _webView != IntPtr.Zero && SendBool(_webView, Selector("isLoading"));

    private static IntPtr NSString(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text + "\0");
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return Send(Class("NSString"), Selector("stringWithUTF8String:"), pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static string Text(IntPtr nsString) =>
        nsString == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(Send(nsString, Selector("UTF8String"))) ?? "";

    private static IntPtr Class(string name) => objc_getClass(name);

    private static IntPtr Selector(string name) => sel_registerName(name);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGRect(double X, double Y, double Width, double Height);

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendRect(IntPtr receiver, IntPtr selector, CGRect rect, IntPtr argument);
}
