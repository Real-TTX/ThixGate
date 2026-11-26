using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>What ThixGate knows about the key an SSH server showed.</summary>
public enum HostKeyCheck
{
    /// <summary>The same key as last time: connect.</summary>
    Known,

    /// <summary>The first connection to this server: the user decides (and the key is kept).</summary>
    New,

    /// <summary>Another key than last time: maybe reinstalled, maybe someone in between - the user decides, warned.</summary>
    Changed,
}

/// <summary>
/// The host keys of SSH servers, as OpenSSH's known_hosts but per computer in known_hosts.json: the first connection asks, the same key
/// connects, another key warns. A host behind a Matgate relay is kept under its own name and port, not 127.0.0.1.
/// </summary>
public static class SshHostKeys
{
    public static string KeyOf(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    public static HostKeyCheck Check(string host, int port, string algorithm, string fingerprint, out KnownHost? known)
    {
        known = KnownHostsStore.Read().GetValueOrDefault(KeyOf(host, port));
        if (known is null)
        {
            return HostKeyCheck.New;
        }

        return known.Algorithm == algorithm && known.Fingerprint == fingerprint ? HostKeyCheck.Known : HostKeyCheck.Changed;
    }

    /// <summary>The user trusts this key (from now on it connects without asking).</summary>
    public static void Trust(string host, int port, string algorithm, string fingerprint)
    {
        var hosts = KnownHostsStore.Read();
        hosts[KeyOf(host, port)] = new KnownHost(algorithm, fingerprint, AppClock.UtcNow);
        KnownHostsStore.Write(hosts);
    }

    public static void Forget(string host, int port)
    {
        var hosts = KnownHostsStore.Read();
        if (hosts.Remove(KeyOf(host, port)))
        {
            KnownHostsStore.Write(hosts);
        }
    }
}
