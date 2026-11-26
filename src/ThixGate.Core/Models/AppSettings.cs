namespace ThixGate.Core.Models;

public enum ThemeChoice
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>What a double click on a connection does.</summary>
public enum OpenSessionIn
{
    /// <summary>A tab right of the menu of the main window (like Royal TS).</summary>
    Tab = 0,

    /// <summary>A window of its own per session.</summary>
    Window = 1,

    /// <summary>A window of its own in full screen.</summary>
    FullScreen = 2,
}

/// <summary>The settings of this computer (settings.json). Only <see cref="Business.SettingsService"/> writes them.</summary>
public sealed record AppSettings
{
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    // ---- Sessions ----

    public OpenSessionIn OpenSessionIn { get; init; } = OpenSessionIn.Tab;

    /// <summary>Ask before a running session's window is closed.</summary>
    public bool ConfirmCloseSession { get; init; } = true;

    /// <summary>SSH opens in the terminal of the system (<see cref="SshTerminal"/>) instead of ThixGate's own terminal (a tab or window like RDP/VNC).</summary>
    public bool SshInSystemTerminal { get; init; }

    /// <summary>The system's terminal for SSH when <see cref="SshInSystemTerminal"/> (empty: Windows Terminal / Terminal.app).</summary>
    public string SshTerminal { get; init; } = "";

    // ---- Matgate ----

    /// <summary>Matgate servers this computer may connect through (gateway) and later take the library from.</summary>
    public IReadOnlyList<MatgateAccount> MatgateAccounts { get; init; } = [];

    // ---- Updates ----

    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Full auto update: install new versions without asking (on by default, the setup offers it).</summary>
    public bool InstallUpdatesAutomatically { get; init; } = true;

    // ---- Remembered by the app ----

    /// <summary>Size of the main window (device-independent pixels).</summary>
    public double WindowWidth { get; init; } = 1280;

    public double WindowHeight { get; init; } = 820;

    /// <summary>The menu at the left of the main window is folded to its icons.</summary>
    public bool IsSidebarCollapsed { get; init; }

    /// <summary>Width of the open menu with the library as a tree (device-independent pixels).</summary>
    public double TreeWidth { get; init; } = 270;

    /// <summary>The folders open in the tree of the menu.</summary>
    public IReadOnlyList<Guid> ExpandedFolders { get; init; } = [];

    public bool FirstStartDone { get; init; }

    public CloudSettings Cloud { get; init; } = new();
}

/// <summary>
/// A Matgate server known to this computer. After the app sign-in (Matgate's /app/authorize in the browser) it has a device there; the
/// device token is stored protected (Windows DPAPI value in <see cref="ProtectedToken"/>, macOS Keychain), never in clear.
/// </summary>
public sealed record MatgateAccount
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>A name for people ("Matgate Büro").</summary>
    public string Name { get; init; } = "";

    /// <summary>https://matgate.example.com</summary>
    public string ServerUrl { get; init; } = "";

    /// <summary>The user in Matgate (filled in by the sign-in).</summary>
    public string UserName { get; init; } = "";

    public DateTimeOffset? LastChecked { get; init; }

    /// <summary>The version Matgate answered at the last check.</summary>
    public string LastVersion { get; init; } = "";

    /// <summary>This computer's device in Matgate (Konto › Sicherheit › Geräte); null: not signed in.</summary>
    public Guid? DeviceId { get; init; }

    /// <summary>The name Matgate shows for the signed-in user.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>The device token, protected for this user (Windows DPAPI); on macOS it lives in the Keychain.</summary>
    public string ProtectedToken { get; init; } = "";

    /// <summary>When the library was last read from Matgate.</summary>
    public DateTimeOffset? LibraryFetched { get; init; }

    public bool IsSignedIn => DeviceId is not null;
}

/// <summary>The connection to ThixCloud (MatCloud). The device token is stored protected (DPAPI / Keychain), not in here.</summary>
public sealed record CloudSettings
{
    /// <summary>The address of the cloud, e.g. https://cloud.example.com. Empty = not connected.</summary>
    public string ServerUrl { get; init; } = "";

    public string UserName { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public string DeviceName { get; init; } = "";

    public long DeviceId { get; init; }

    /// <summary>The highest revision of the account this computer has applied.</summary>
    public long LastRevision { get; init; }

    /// <summary>The device token, protected for this user (Windows DPAPI); on macOS it lives in the Keychain.</summary>
    public string ProtectedToken { get; init; } = "";

    public bool IsConnected => ServerUrl.Length > 0 && DeviceId > 0;
}
