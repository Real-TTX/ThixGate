using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using ThixGate.Core.Business;
using ThixGate.Core.Models;
using ThixGate.Sessions;

namespace ThixGate.UiTests;

/// <summary>
/// What a Remote Desktop brings from this computer, against xrdp of Matgate's test desktop (THIXGATE_E2E_DESKTOP, THIXGATE_E2E_PORTS;
/// the container needs FUSE for the drive: --device /dev/fuse --cap-add SYS_ADMIN). The test types into the xterm of the session:
/// a folder of this computer as a drive (xrdp mounts it at ~/thinclient_drives), read and written there; the clipboard both ways (xclip);
/// and the status (latency, data rate) of the session.
/// </summary>
public sealed class SessionLocalEndToEndTests
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("THIXGATE_E2E_DESKTOP");
    private static readonly int[] Ports = (Environment.GetEnvironmentVariable("THIXGATE_E2E_PORTS") ?? "3389,3390,5901").Split(',').Select(int.Parse).ToArray();

    [AvaloniaFact]
    public void Rdp_session_brings_a_drive_the_clipboard_and_its_status()
    {
        if (Host is null)
        {
            return; // opt-in
        }

        using var data = new TestData();
        var folder = Path.Combine(Path.GetTempPath(), "thixgate-drive-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "hallo.txt"), "Hallo vom Computer");
        SettingsService.Update(settings => settings with { ConfirmCloseSession = false });
        var credential = CredentialService.Save(new StoredCredential { Name = "Test", UserName = "demo" }, "demo-only-pw");
        var connection = LibraryService.SaveConnection(new RemoteConnection
        {
            Name = "Testdesktop mit Laufwerk",
            Host = Host,
            Port = Ports[0],
            CredentialId = credential.Id,
            Resolution = "1280x800",
            KeyboardLayout = 0x409,
            Drives = folder,
        });

        var window = SessionWindow.Show(connection);
        var view = window.View;
        try
        {
            Pump(40, () => view.IsConnected && view.HasPicture);
            Assert.True(view.IsConnected, $"not connected: {view.StateText}");
            Pump(4, () => false); // the desktop and the drive come up

            // The status: a sample a second, with latency and data.
            for (var second = 0; second < 10 && view.LatestStatus is not { Latency: not null, Kilobits: > 0 }; second++)
            {
                Pump(1, () => false);
                view.MeasureStatus();
            }

            Assert.True(view.LatestStatus?.Latency is not null, $"status: {view.LatestStatus} / {view.StatusLine}");
            Assert.True(view.StatusLine.Contains("ms"), $"status line: {view.StatusLine} / {view.LatestStatus}");

            // Into the xterm: what is copied there arrives on this computer's clipboard.
            Click(view, 300, 200);
            Type(view, "echo -n Kopiert | xclip -selection clipboard\n");
            Pump(2, () => false);
            if (Environment.GetEnvironmentVariable("THIXGATE_SCREENSHOTS") is { } shots)
            {
                Directory.CreateDirectory(shots);
                window.CaptureRenderedFrame()?.Save(Path.Combine(shots, "session-typed.png"));
            }

            var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
            string? text = null;
            Pump(15, () => (text = Run(clipboard.TryGetTextAsync)) == "Kopiert");
            Run(() => clipboard.SetTextAsync("probe"));
            var probe = Run(clipboard.TryGetTextAsync);
            var log = Directory.Exists(ThixGate.Core.Infrastructure.AppPaths.LogDirectory) ? string.Join(Environment.NewLine, Directory.GetFiles(ThixGate.Core.Infrastructure.AppPaths.LogDirectory).SelectMany(File.ReadAllLines).TakeLast(30)) : "";
            Assert.True(text == "Kopiert", $"clipboard: {text}, note: {view.ClipboardNote}, headless clipboard works: {probe == "probe"}{Environment.NewLine}{log}");

            // This computer's clipboard goes there: the session turns it into capitals and copies it back.
            Run(() => clipboard.SetTextAsync("von thixgate"));
            Run(view.SyncClipboardAsync);
            Pump(1, () => false);
            Type(view, "xclip -o -selection clipboard | tr a-z A-Z | xclip -selection clipboard\n");
            Pump(15, () => (text = Run(clipboard.TryGetTextAsync)) == "VON THIXGATE");
            Assert.Equal("VON THIXGATE", text);

            // The drive (THIXGATE_E2E_DRIVE: the server has to mount it - xrdp with FUSE): a file of this computer read there and one written back.
            if (Environment.GetEnvironmentVariable("THIXGATE_E2E_DRIVE") is not null)
            {
                Type(view, "cd ~/thinclient_drives/* && cp hallo.txt zurueck.txt\n");
                var back = Path.Combine(folder, "zurueck.txt");
                Pump(20, () => File.Exists(back) && new FileInfo(back).Length > 0);
                Assert.True(File.Exists(back), "the session did not write into the drive: " + string.Join(", ", Directory.GetFiles(folder).Select(Path.GetFileName)));
                Assert.Equal("Hallo vom Computer", File.ReadAllText(back));
            }

            // Full screen: the bar at the top edge instead of the bar above the picture.
            window.ToggleFullScreen();
            Pump(1, () => false);
            Assert.True(view.IsFullScreen);
            Assert.True(view.IsFullBarShown);
            window.ToggleFullScreen();
            Pump(1, () => false);
            Assert.False(view.IsFullScreen);
        }
        finally
        {
            window.Close();
            Pump(1, () => false);
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Pump(double seconds, Func<bool> done)
    {
        for (var until = DateTime.UtcNow.AddSeconds(seconds); DateTime.UtcNow < until && !done();)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(50);
        }
    }

    private static T Run<T>(Func<Task<T>> work)
    {
        var task = work();
        Pump(5, () => task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }

    private static void Run(Func<Task> work)
    {
        var task = work();
        Pump(5, () => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void Click(SessionView view, int x, int y)
    {
        view.SendInput(new JsonObject { ["t"] = "m", ["x"] = x, ["y"] = y });
        view.SendInput(new JsonObject { ["t"] = "b", ["b"] = 0, ["d"] = 1 });
        view.SendInput(new JsonObject { ["t"] = "b", ["b"] = 0, ["d"] = 0 });
        Pump(0.5, () => false);
    }

    /// <summary>Types as a US keyboard would (the connection says 0x409): each character as its scancode, with Shift where it needs one.</summary>
    private static void Type(SessionView view, string text)
    {
        foreach (var character in text)
        {
            var (scancode, shift) = UsKey(character);
            if (shift)
            {
                Key(view, 0x2A, true);
            }

            Key(view, scancode, true);
            Key(view, scancode, false);
            if (shift)
            {
                Key(view, 0x2A, false);
            }

            Pump(0.02, () => false);
        }
    }

    private static void Key(SessionView view, int scancode, bool down) =>
        view.SendInput(new JsonObject { ["t"] = "k", ["s"] = scancode, ["e"] = 0, ["d"] = down ? 1 : 0, ["ks"] = 0, ["ag"] = 0 });

    private static (int Scancode, bool Shift) UsKey(char character)
    {
        const string Top = "qwertyuiop", Middle = "asdfghjkl", Bottom = "zxcvbnm";
        var lower = char.ToLowerInvariant(character);
        var shift = char.IsUpper(character);
        if (Top.Contains(lower))
        {
            return (0x10 + Top.IndexOf(lower), shift);
        }

        if (Middle.Contains(lower))
        {
            return (0x1E + Middle.IndexOf(lower), shift);
        }

        if (Bottom.Contains(lower))
        {
            return (0x2C + Bottom.IndexOf(lower), shift);
        }

        if (char.IsAsciiDigit(character))
        {
            return (character == '0' ? 0x0B : 0x02 + (character - '1'), false);
        }

        return character switch
        {
            ' ' => (0x39, false),
            '\n' => (0x1C, false),
            '~' => (0x29, true),
            '/' => (0x35, false),
            '*' => (0x09, true),
            '&' => (0x08, true),
            '.' => (0x34, false),
            '-' => (0x0C, false),
            '_' => (0x0C, true),
            '|' => (0x2B, true),
            '>' => (0x34, true),
            '<' => (0x33, true),
            _ => throw new ArgumentException("No US key for " + character),
        };
    }
}
