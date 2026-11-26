using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace ThixGate.Core.Infrastructure;

/// <summary>What a secret is: it decides the Keychain service (macOS) and the DPAPI entropy (Windows).</summary>
internal enum SecretKind
{
    /// <summary>The device token of ThixCloud.</summary>
    Cloud,

    /// <summary>A password of the vault (saved credentials).</summary>
    Vault,

    /// <summary>The app token of a Matgate account.</summary>
    Matgate,
}

/// <summary>
/// Keeps secrets where only this user can read them: Windows DPAPI (the protected value lives in our files), macOS Keychain
/// (generic password "info.schmoldt.thixgate.&lt;kind&gt;", the value in our files stays empty). No secret ever goes into a log
/// or to the cloud.
/// </summary>
internal static class TokenStore
{
    private static readonly Dictionary<string, string> TestTokens = [];
    private static bool _useTestStore;

    /// <summary>Stores the secret; returns the value to keep in our files (Windows) or an empty string (Keychain).</summary>
    public static string Save(string account, string secret, SecretKind kind = SecretKind.Cloud)
    {
        if (_useTestStore)
        {
            TestTokens[$"{kind}/{account}"] = secret;
            return "test";
        }

        if (OperatingSystem.IsWindows())
        {
            return Convert.ToBase64String(Protect(secret, kind));
        }

        if (OperatingSystem.IsMacOS())
        {
            RunSecurity("add-generic-password", "-U", "-a", account, "-s", ServiceOf(kind), "-w", secret);
            return "";
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
    }

    public static string? Read(string account, string protectedValue, SecretKind kind = SecretKind.Cloud)
    {
        if (_useTestStore)
        {
            return TestTokens.GetValueOrDefault($"{kind}/{account}");
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return protectedValue.Length == 0 ? null : Unprotect(Convert.FromBase64String(protectedValue), kind);
            }

            if (OperatingSystem.IsMacOS())
            {
                var secret = RunSecurity("find-generic-password", "-a", account, "-s", ServiceOf(kind), "-w").TrimEnd('\n');
                return secret.Length == 0 ? null : secret;
            }

            return protectedValue.Length == 0 ? null : Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or InvalidOperationException or IOException)
        {
            Log.Warning($"A secret ({kind}) could not be read: {exception.GetType().Name}");
            return null;
        }
    }

    public static void Delete(string account, SecretKind kind = SecretKind.Cloud)
    {
        if (_useTestStore)
        {
            TestTokens.Remove($"{kind}/{account}");
            return;
        }

        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            RunSecurity("delete-generic-password", "-a", account, "-s", ServiceOf(kind));
        }
        catch (InvalidOperationException)
        {
            // not there
        }
    }

    internal static void UseMemoryForTests(bool enabled)
    {
        _useTestStore = enabled;
        TestTokens.Clear();
    }

    private static string ServiceOf(SecretKind kind) => $"info.schmoldt.thixgate.{kind.ToString().ToLowerInvariant()}";

    private static byte[] EntropyOf(SecretKind kind) => Encoding.UTF8.GetBytes($"ThixGate.{kind}");

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(string secret, SecretKind kind) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), EntropyOf(kind), DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static string Unprotect(byte[] data, SecretKind kind) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(data, EntropyOf(kind), DataProtectionScope.CurrentUser));

    private static string RunSecurity(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/security")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("security could not be started");
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"security {arguments[0]} failed ({process.ExitCode})");
        }

        return output;
    }
}
