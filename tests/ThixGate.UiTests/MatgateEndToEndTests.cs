using System.Net;
using System.Text.RegularExpressions;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ThixGate.Core.Business;
using ThixGate.Core.Models;
using ThixGate.Sessions;

namespace ThixGate.UiTests;

/// <summary>
/// ThixGate against a real Matgate: the app sign-in through Matgate's pages (a browser of HttpClient: sign-in form, "ThixGate
/// zulassen", back to 127.0.0.1), the library, and real RDP / VNC sessions through Matgate's relay into the test desktop of Matgate
/// (Matgate/tests/desktop). Opt-in: THIXGATE_E2E_MATGATE (address), THIXGATE_E2E_MATGATE_USER, THIXGATE_E2E_MATGATE_PASSWORD - a test
/// Matgate whose RDP/VNC connections lead to the test desktop (its test credentials: demo / demo-only-pw, VNC demo-pw1).
/// </summary>
public sealed partial class MatgateEndToEndTests
{
    private static readonly string? Server = Environment.GetEnvironmentVariable("THIXGATE_E2E_MATGATE");
    private static readonly string User = Environment.GetEnvironmentVariable("THIXGATE_E2E_MATGATE_USER") ?? "";
    private static readonly string Password = Environment.GetEnvironmentVariable("THIXGATE_E2E_MATGATE_PASSWORD") ?? "";

    [AvaloniaFact]
    public void Signed_in_ThixGate_opens_the_library_and_sessions_through_the_relay()
    {
        if (Server is null)
        {
            return; // opt-in
        }

        using var data = new TestData();
        SettingsService.Update(settings => settings with { ConfirmCloseSession = false });
        var account = MatgateService.Save(new MatgateAccount { Name = "Matgate Test", ServerUrl = Server });

        var signIn = MatgateService.SignInAsync(account.Id, url => _ = Task.Run(() => AllowInBrowserAsync(url)));
        Wait(signIn, TimeSpan.FromSeconds(60));
        account = signIn.Result;
        Assert.True(account.IsSignedIn);
        Assert.Equal(User, account.UserName);

        var refresh = MatgateService.RefreshLibraryAsync(account.Id);
        Wait(refresh, TimeSpan.FromSeconds(30));
        var library = refresh.Result;
        Assert.All(library.Connections, item => Assert.Contains(item.Protocol, new[] { "rdp", "vnc", "ssh" }));
        var screens = library.Connections.Where(item => item.Protocol is "rdp" or "vnc").ToList();
        Assert.NotEmpty(screens);

        var vncCredential = CredentialService.Save(new StoredCredential { Name = "VNC", UserName = "vnc" }, "demo-pw1");
        var rdpCredential = CredentialService.Save(new StoredCredential { Name = "Demo", UserName = "demo" }, "demo-only-pw");
        foreach (var item in screens)
        {
            var taken = MatgateService.TakeOver(account.Id, item.Id);
            var connection = LibraryService.SaveConnection(taken with
            {
                CredentialId = item.Protocol == "vnc" ? vncCredential.Id : rdpCredential.Id,
                Resolution = "1280x800",
            });
            Assert.Equal(ConnectVia.Matgate, connection.Via);
            Session(connection, item.Name);
        }

        var signOut = MatgateService.SignOutAsync(account.Id);
        Wait(signOut, TimeSpan.FromSeconds(15));
        Assert.False(MatgateService.Find(account.Id)!.IsSignedIn);
    }

    // What a person does in the browser: signs in to Matgate, presses "Zulassen"; the browser follows back to ThixGate.
    private static async Task AllowInBrowserAsync(string authorizeUrl)
    {
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        using var browser = new HttpClient(handler);
        var authorize = new Uri(authorizeUrl);
        var login = await browser.PostAsync(new Uri(authorize, "/login"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = User,
            ["password"] = Password,
            ["returnUrl"] = authorize.PathAndQuery,
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var page = await browser.GetStringAsync(authorize);
        Assert.Contains("data-app-allow", page, StringComparison.Ordinal);
        var fields = HiddenField().Matches(page)
            .GroupBy(match => match.Groups[1].Value)
            .ToDictionary(group => group.Key, group => WebUtility.HtmlDecode(group.First().Groups[2].Value));
        fields["decision"] = "allow";
        var answer = await browser.PostAsync(new Uri(authorize, "/app/authorize"), new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        var back = answer.Headers.Location!;
        Assert.Equal("127.0.0.1", back.Host);
        await browser.GetStringAsync(back);
    }

    private static void Session(RemoteConnection connection, string name)
    {
        var window = SessionWindow.Show(connection);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < until && !(window.IsConnected && window.HasPicture))
            {
                Pump();
            }

            for (var settle = DateTime.UtcNow.AddSeconds(2); DateTime.UtcNow < settle;)
            {
                Pump();
            }

            if (Environment.GetEnvironmentVariable("THIXGATE_SCREENSHOTS") is { } shots)
            {
                Directory.CreateDirectory(shots);
                window.CaptureRenderedFrame()?.Save(Path.Combine(shots, $"relay-{connection.Protocol}-{connection.EffectivePort}.png"));
            }

            Assert.True(window.IsConnected, $"{name}: not connected: {window.StateText}");
            Assert.True(window.HasPicture, $"{name}: no picture ({window.LastDrawError})");
            Assert.True(window.PictureSize.Width >= 640, $"{name}: picture {window.PictureSize}");
        }
        finally
        {
            window.Close();
        }

        for (var round = 0; round < 20; round++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }
    }

    private static void Wait(Task task, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            Pump();
        }

        Assert.True(task.IsCompleted, "timed out");
        task.GetAwaiter().GetResult();
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(50);
    }

    [GeneratedRegex("""<input type="hidden" name="([^"]+)" value="([^"]*)">""")]
    private static partial Regex HiddenField();
}
