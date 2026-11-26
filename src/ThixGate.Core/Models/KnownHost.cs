namespace ThixGate.Core.Models;

/// <summary>The key an SSH server showed and the user trusted (known_hosts.json, per computer).</summary>
public sealed record KnownHost(string Algorithm, string Fingerprint, DateTimeOffset TrustedAt);
