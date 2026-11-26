using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>
/// The permission checks of the business layer. Every service calls them itself before it changes something, whatever
/// the UI shows or hides.
///
/// An administrator can restrict ThixGate with machine policies (value 1 / true):
///   Windows: HKLM\SOFTWARE\Policies\ThixGate (DWORD)
///   macOS:   a configuration profile for the domain info.schmoldt.thixgate (/Library/Managed Preferences)
/// Policies:
///   LockConfiguration     settings, folders, connections and credentials are read-only (connecting still works)
///   DisableCloud          no connection to ThixCloud (the library is not synced)
///   DisableUpdates        no update check, no update installation
///   DisableMatgate        no Matgate accounts, no connection "über Matgate"
///   DisableSavedPasswords passwords are never stored: ThixGate asks for them at every connect
/// </summary>
public static class AccessPolicy
{
    public const string MacPolicyDomain = "info.schmoldt.thixgate";

    private const string PolicyKeyPath = @"SOFTWARE\Policies\ThixGate";

    private static readonly string MacPolicyFile = $"/Library/Managed Preferences/{MacPolicyDomain}.plist";

    private static Func<string, bool> _readPolicy = ReadMachinePolicy;

    private static (DateTime Written, Dictionary<string, bool> Values) _macPolicies = (DateTime.MinValue, []);

    public static bool IsConfigurationLocked => _readPolicy("LockConfiguration");

    public static bool IsCloudDisabled => _readPolicy("DisableCloud");

    public static bool AreUpdatesDisabled => _readPolicy("DisableUpdates");

    public static bool IsMatgateDisabled => _readPolicy("DisableMatgate");

    public static bool ArePasswordsDisabled => _readPolicy("DisableSavedPasswords");

    public static void EnsureMatgateAllowed()
    {
        if (IsMatgateDisabled)
        {
            throw new BusinessException("Matgate wurde von einem Administrator deaktiviert (Richtlinie DisableMatgate).");
        }
    }

    public static void EnsurePasswordsAllowed()
    {
        if (ArePasswordsDisabled)
        {
            throw new BusinessException("Gespeicherte Passwörter wurden von einem Administrator deaktiviert (Richtlinie DisableSavedPasswords).", "Password");
        }
    }

    public static void EnsureConfigurationEditable()
    {
        if (IsConfigurationLocked)
        {
            throw new BusinessException("Die Einstellungen wurden von einem Administrator gesperrt (Richtlinie LockConfiguration).");
        }
    }

    public static void EnsureCloudAllowed()
    {
        if (IsCloudDisabled)
        {
            throw new BusinessException("Der Abgleich mit der ThixCloud wurde von einem Administrator deaktiviert (Richtlinie DisableCloud).");
        }
    }

    public static void EnsureUpdatesAllowed()
    {
        if (AreUpdatesDisabled)
        {
            throw new BusinessException("Updates wurden von einem Administrator deaktiviert (Richtlinie DisableUpdates).");
        }
    }

    /// <summary>Tests replace the registry with their own answers.</summary>
    internal static void UsePolicyReaderForTests(Func<string, bool>? reader) => _readPolicy = reader ?? ReadMachinePolicy;

    private static bool ReadMachinePolicy(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return ReadWindowsPolicy(name);
        }

        return OperatingSystem.IsMacOS() && ReadMacPolicies().GetValueOrDefault(name);
    }

    [SupportedOSPlatform("windows")]
    private static bool ReadWindowsPolicy(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(PolicyKeyPath);
        return key?.GetValue(name) is int value && value == 1;
    }

    /// <summary>The managed preferences are a (binary) plist; plutil turns it into JSON. Read again only after a change.</summary>
    private static Dictionary<string, bool> ReadMacPolicies()
    {
        var written = File.Exists(MacPolicyFile) ? File.GetLastWriteTimeUtc(MacPolicyFile) : DateTime.MinValue;
        if (written == _macPolicies.Written)
        {
            return _macPolicies.Values;
        }

        var values = new Dictionary<string, bool>();
        if (written != DateTime.MinValue)
        {
            try
            {
                var startInfo = new ProcessStartInfo("/usr/bin/plutil") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "-convert", "json", "-o", "-", MacPolicyFile })
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = Process.Start(startInfo)!;
                var json = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                using var document = JsonDocument.Parse(json);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    values[property.Name] = property.Value.ValueKind == JsonValueKind.True ||
                                            (property.Value.ValueKind == JsonValueKind.Number && property.Value.GetInt32() == 1);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log.Warning($"Could not read the policies in {MacPolicyFile}: {exception.Message}");
            }
        }

        _macPolicies = (written, values);
        return values;
    }
}
