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
    /// <summary>A window of its own per session.</summary>
    Window = 0,

    /// <summary>The window of the session in full screen.</summary>
    FullScreen = 1,
}

/// <summary>The settings of this computer (settings.json). Only <see cref="Business.SettingsService"/> writes them.</summary>
public sealed record AppSettings
{
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    // ---- Sessions ----

    public OpenSessionIn OpenSessionIn { get; init; } = OpenSessionIn.Window;

    /// <summary>Ask before a running session's window is closed.</summary>
    public bool ConfirmCloseSession { get; init; } = true;

    /// <summary>The terminal SSH sessions open in (empty: the system's: Windows Terminal / Terminal.app).</summary>
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

    public bool FirstStartDone { get; init; }

    public CloudSettings Cloud { get; init; } = new();
}

/// <summary>A Matgate server known to this computer. The device token of the app sign-in (stage 2) is stored protected, not here.</summary>
public sealed record MatgateAccount
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>A name for people ("Matgate Büro").</summary>
    public string Name { get; init; } = "";

    /// <summary>https://matgate.example.com</summary>
    public string ServerUrl { get; init; } = "";

    public string UserName { get; init; } = "";

    public DateTimeOffset? LastChecked { get; init; }

    /// <summary>The version Matgate answered at the last check.</summary>
    public string LastVersion { get; init; } = "";
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
