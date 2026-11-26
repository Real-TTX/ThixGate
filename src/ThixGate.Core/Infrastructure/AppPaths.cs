namespace ThixGate.Core.Infrastructure;

/// <summary>
/// Where ThixGate keeps its files. Program files and user data are separate: updates and uninstalling never touch the
/// user data unless the user decides so.
///   Windows: %LocalAppData%\ThixGate (library, vault, settings, updates, logs)
///   macOS:   ~/Library/Application Support/ThixGate
/// </summary>
public static class AppPaths
{
    /// <summary>Developers can point ThixGate to a separate folder (sample data, screenshots) with this variable.</summary>
    public const string DataDirectoryVariable = "THIXGATE_DATA_DIR";

    private static string? _dataDirectoryOverride = NullIfEmpty(Environment.GetEnvironmentVariable(DataDirectoryVariable));

    public static string DataDirectory => _dataDirectoryOverride ?? Path.Combine(UserFolder(), AppInfo.ProductName);

    /// <summary>Folders, connections, credentials (without passwords), tombstones and the sync marks.</summary>
    public static string LibraryFile => Path.Combine(DataDirectory, "library.json");

    /// <summary>The protected passwords of the vault (Windows: DPAPI values; macOS: empty, the Keychain has them). Never synced.</summary>
    public static string VaultFile => Path.Combine(DataDirectory, "vault.json");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string UpdateDirectory => Path.Combine(DataDirectory, "updates");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>The libraries of the Matgate accounts, one file each (read-only copies, per computer).</summary>
    public static string MatgateDirectory => Path.Combine(DataDirectory, "matgate");

    /// <summary>Files opened from a file connection (to view or edit them with their program); emptied when ThixGate starts.</summary>
    public static string TemporaryDirectory => Path.Combine(Path.GetTempPath(), AppInfo.ProductName + (HasOverride ? "-" + Path.GetFileName(DataDirectory) : ""));

    /// <summary>The host keys of the SSH servers this computer trusts (per computer).</summary>
    public static string KnownHostsFile => Path.Combine(DataDirectory, "known_hosts.json");

    /// <summary>True when a developer or a test pointed ThixGate to its own folder: it is then its own instance.</summary>
    public static bool HasOverride => _dataDirectoryOverride is not null;

    /// <summary>Tests use a temporary folder.</summary>
    internal static void UseDirectoryForTests(string? directory) => _dataDirectoryOverride = directory;

    private static string UserFolder() => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
