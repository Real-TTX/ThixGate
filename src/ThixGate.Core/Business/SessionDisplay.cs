using Matgate.Display;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>
/// What a remote desktop takes from this computer: its monitors (<see cref="RemoteConnection.Monitors"/>, else <see cref="AppSettings.Monitors"/>)
/// and its folders as drives (<see cref="RemoteConnection.Drives"/>). Only RDP - VNC shows one screen and has no drives - and only the engine
/// here: a session on Matgate runs there, where this computer's monitors and disks are not.
/// </summary>
public static class SessionDisplay
{
    /// <summary>The monitors in the order the user numbers them: left to right, then top to bottom.</summary>
    public static IReadOnlyList<ScreenMonitor> Ordered(IEnumerable<ScreenMonitor> monitors) =>
        [.. monitors.OrderBy(monitor => monitor.Left).ThenBy(monitor => monitor.Top)];

    /// <summary>The setting that counts for a connection: its own, or the one of the settings.</summary>
    public static string MonitorsOf(RemoteConnection connection) =>
        connection.Monitors.Length > 0 ? connection.Monitors : SettingsService.Get().Monitors is { Length: > 0 } fallback ? fallback : "one";

    /// <summary>
    /// The monitors a session spans: none for "one" (it shows in its tab or window), every one for "all", or the chosen ones that exist here
    /// ("1,3"). One monitor alone is a full screen on that monitor. Never for VNC or a session on Matgate.
    /// </summary>
    public static IReadOnlyList<ScreenMonitor> MonitorsFor(RemoteConnection connection, IReadOnlyList<ScreenMonitor> available)
    {
        if (connection.Protocol != ConnectionProtocol.Rdp || (connection.Via == ConnectVia.Matgate && connection.SessionOnMatgate))
        {
            return [];
        }

        var ordered = Ordered(available);
        var setting = MonitorsOf(connection);
        if (setting == "all")
        {
            return ordered.Count > 1 ? ordered : [];
        }

        var chosen = Chosen(setting);
        return chosen.Count == 0 ? [] : [.. chosen.Where(number => number <= ordered.Count).Select(number => ordered[number - 1])];
    }

    /// <summary>The numbers of the chosen monitors in a setting like "1,3" (none for "one", "all" or nonsense).</summary>
    public static IReadOnlyList<int> Chosen(string setting) =>
        [.. setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var number) ? number : 0)
            .Where(number => number is > 0 and <= 16)
            .Distinct()
            .Order()];

    /// <summary>The folders of a connection, one a line (none for "*" or an empty setting).</summary>
    public static IReadOnlyList<string> Folders(string drives) =>
        drives.Trim() == "*" ? [] : [.. drives.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// This computer's folders as drives of a session: every drive of the computer ("*"), or the folders that exist here (a folder of another
    /// computer of the library is left out). Never for VNC or a session on Matgate.
    /// </summary>
    public static IReadOnlyList<ScreenDrive> DrivesFor(RemoteConnection connection)
    {
        if (connection.Protocol != ConnectionProtocol.Rdp || connection.Drives.Trim().Length == 0
            || (connection.Via == ConnectVia.Matgate && connection.SessionOnMatgate))
        {
            return [];
        }

        if (connection.Drives.Trim() == "*")
        {
            return ComputerDrives();
        }

        return [.. Folders(connection.Drives).Where(Directory.Exists).Select(folder => new ScreenDrive(NameOf(folder), folder))];
    }

    /// <summary>Every drive of this computer: Windows its drive letters, macOS the home folder and every volume.</summary>
    public static IReadOnlyList<ScreenDrive> ComputerDrives()
    {
        if (OperatingSystem.IsWindows())
        {
            return [.. DriveInfo.GetDrives()
                .Where(drive => drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                .Select(drive => new ScreenDrive(drive.Name.TrimEnd('\\', ':'), drive.RootDirectory.FullName))];
        }

        var drives = new List<ScreenDrive> { new("Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) };
        if (Directory.Exists("/Volumes"))
        {
            drives.AddRange(Directory.EnumerateDirectories("/Volumes").Select(volume => new ScreenDrive(Path.GetFileName(volume), volume)));
        }

        return drives;
    }

    /// <summary>The name a folder shows on the remote desktop: its own name, or the drive letter of a drive.</summary>
    public static string NameOf(string folder)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(folder);
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed.TrimEnd(':');
    }
}
