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

    /// <summary>True when a developer or a test pointed ThixGate to its own folder: it is then its own instance.</summary>
    public static bool HasOverride => _dataDirectoryOverride is not null;

    /// <summary>Tests use a temporary folder.</summary>
    internal static void UseDirectoryForTests(string? directory) => _dataDirectoryOverride = directory;

    private static string UserFolder() => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
