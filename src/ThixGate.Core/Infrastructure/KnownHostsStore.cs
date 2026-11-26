using System.Text.Json;
using ThixGate.Core.Models;

namespace ThixGate.Core.Infrastructure;

/// <summary>The host keys of the SSH servers this computer trusts (known_hosts.json, per computer, never synced). Only <see cref="Business.SshHostKeys"/> uses it.</summary>
internal static class KnownHostsStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Dictionary<string, KnownHost> Read()
    {
        lock (Gate)
        {
            try
            {
                return File.Exists(AppPaths.KnownHostsFile)
                    ? JsonSerializer.Deserialize<Dictionary<string, KnownHost>>(File.ReadAllText(AppPaths.KnownHostsFile), SerializerOptions) ?? []
                    : [];
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                Log.Warning($"The known SSH hosts could not be read: {exception.Message}");
                return [];
            }
        }
    }

    public static void Write(Dictionary<string, KnownHost> hosts)
    {
        lock (Gate)
        {
            LibraryStore.WriteAtomically(AppPaths.KnownHostsFile, JsonSerializer.Serialize(hosts, SerializerOptions));
        }
    }
}
