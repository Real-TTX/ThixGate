using System.Diagnostics;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>
/// SSH connections open in a terminal of the system with the system's OpenSSH (Windows Terminal or a console window on Windows,
/// Terminal.app on macOS) - or in the terminal named in the settings ("{ssh}" stands for the command). The password is typed in
/// the terminal: ThixGate never puts it on a command line.
/// </summary>
public static class SshLauncher
{
    /// <summary>The ssh arguments of a connection (user@host, port, key file).</summary>
    public static IReadOnlyList<string> Arguments(RemoteConnection connection)
    {
        if (connection.Protocol != ConnectionProtocol.Ssh)
        {
            throw new BusinessException("Nur SSH-Verbindungen öffnen ein Terminal.");
        }

        var login = SessionTargets.LoginOf(connection);
        var arguments = new List<string>();
        if (connection.EffectivePort != 22)
        {
            arguments.Add("-p");
            arguments.Add(connection.EffectivePort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (connection.KeyFile.Length > 0)
        {
            arguments.Add("-i");
            arguments.Add(connection.KeyFile);
        }

        arguments.Add(login.UserName.Length > 0 ? $"{login.UserName}@{connection.Host}" : connection.Host);
        return arguments;
    }

    /// <summary>The command as a person would type it (shown in the UI, used in terminals that take one line).</summary>
    public static string CommandLine(RemoteConnection connection) =>
        "ssh " + string.Join(' ', Arguments(connection).Select(Quote));

    /// <summary>Opens the terminal.</summary>
    public static void Open(RemoteConnection connection)
    {
        if (connection.Via == ConnectVia.Matgate)
        {
            throw new BusinessException("Verbinden über Matgate kommt mit der App-Schnittstelle von Matgate – bis dahin bitte „Direkt“ wählen.");
        }

        if (connection.KeyFile.Length > 0 && !File.Exists(connection.KeyFile))
        {
            throw new BusinessException($"Die Schlüsseldatei {connection.KeyFile} gibt es nicht.", nameof(RemoteConnection.KeyFile));
        }

        var command = CommandLine(connection);
        var custom = SettingsService.Get().SshTerminal.Trim();
        try
        {
            if (custom.Length > 0)
            {
                StartShell(custom.Replace("{ssh}", command, StringComparison.Ordinal));
            }
            else if (OperatingSystem.IsMacOS())
            {
                var script = $"tell application \"Terminal\" to do script \"{command.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
                Start("/usr/bin/osascript", ["-e", script, "-e", "tell application \"Terminal\" to activate"]);
            }
            else if (OperatingSystem.IsWindows())
            {
                var windowsTerminal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
                if (File.Exists(windowsTerminal))
                {
                    Start(windowsTerminal, ["new-tab", "--title", connection.Name, "ssh", .. Arguments(connection)]);
                }
                else
                {
                    var startInfo = new ProcessStartInfo("ssh.exe") { UseShellExecute = true };
                    foreach (var argument in Arguments(connection))
                    {
                        startInfo.ArgumentList.Add(argument);
                    }

                    Process.Start(startInfo)?.Dispose();
                }
            }
            else
            {
                Start("x-terminal-emulator", ["-e", "ssh", .. Arguments(connection)]);
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Log.Warning($"SSH terminal could not be started: {exception.Message}");
            throw new BusinessException("Das Terminal konnte nicht gestartet werden. Ist OpenSSH installiert? (Einstellungen › Sitzungen › Terminal)");
        }

        LibraryService.MarkUsed(connection.Id);
    }

    private static void Start(string program, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process.Start(startInfo)?.Dispose();
    }

    private static void StartShell(string commandLine)
    {
        if (OperatingSystem.IsWindows())
        {
            Start("cmd.exe", ["/c", "start", "\"ThixGate\"", commandLine]);
        }
        else
        {
            Start("/bin/sh", ["-c", commandLine]);
        }
    }

    private static string Quote(string argument) =>
        argument.Length > 0 && argument.All(character => char.IsLetterOrDigit(character) || character is '@' or '.' or '-' or '_' or ':' or '/' or '\\')
            ? argument
            : $"\"{argument.Replace("\"", "\\\"")}\"";
}
