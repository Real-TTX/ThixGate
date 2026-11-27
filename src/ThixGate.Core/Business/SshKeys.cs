using System.Security.Cryptography;
using System.Text;
using Matgate.Services;
using Renci.SshNet;
using Renci.SshNet.Common;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>
/// The private key of an SSH / SFTP connection: a key file of this computer (<see cref="RemoteConnection.KeyFile"/>) or a key pasted into
/// the form, as in Matgate. A pasted key is a secret like a password: it lives only in the vault (Windows DPAPI in vault.json, macOS
/// Keychain), never in library.json, an export, the cloud, a log or a command line - on another computer the connection has no key and
/// asks for the password. An encrypted key opens with the password as its passphrase. The pasted key is read the way Matgate reads it
/// (<see cref="SshLogin"/> of Mat.Remote).
/// </summary>
public static class SshKeys
{
    /// <summary>The field of the form errors name.</summary>
    public const string Field = "PrivateKey";

    private const int MaxLength = 32 * 1024;

    /// <summary>A key is stored on this computer for the connection.</summary>
    public static bool IsStored(Guid connectionId) =>
        !AccessPolicy.ArePasswordsDisabled && LibraryStore.ProtectedPassword(Slot(connectionId)) is not null;

    /// <summary>The connection signs in with a key (a file or a stored one).</summary>
    public static bool Has(RemoteConnection connection) =>
        connection.Protocol.HasKeyFile() && (connection.KeyFile.Length > 0 || IsStored(connection.Id));

    /// <summary>The key's text for connecting ("" none): the stored one, else the key file.</summary>
    public static string Text(RemoteConnection connection)
    {
        if (!connection.Protocol.HasKeyFile())
        {
            return "";
        }

        if (Stored(connection.Id) is { } stored)
        {
            return stored;
        }

        if (connection.KeyFile.Length == 0)
        {
            return "";
        }

        if (!File.Exists(connection.KeyFile))
        {
            throw new BusinessException($"Die Schlüsseldatei {connection.KeyFile} gibt es nicht.", nameof(RemoteConnection.KeyFile));
        }

        return File.ReadAllText(connection.KeyFile);
    }

    /// <summary>The key as SSH.NET takes it; an encrypted one with its passphrase.</summary>
    public static PrivateKeyFile Open(string key, string passphrase) => SshLogin.ReadKey(key, passphrase);

    /// <summary>Whether the key is encrypted (it then needs its passphrase).</summary>
    public static bool NeedsPassphrase(string key, string field = Field)
    {
        if (key.Length == 0)
        {
            return false;
        }

        try
        {
            using var _ = SshLogin.ReadKey(key, "");
            return false;
        }
        catch (SshAuthenticationException)
        {
            return true;
        }
        catch (Exception exception) when (exception is SshException or IOException or ArgumentException or InvalidOperationException or FormatException)
        {
            throw new BusinessException($"Der private Schlüssel lässt sich nicht lesen: {exception.Message}", field);
        }
    }

    /// <summary>A pasted key, checked: it must be a private key ThixGate can read (OpenSSH, PEM, PKCS#8 or PuTTY).</summary>
    public static string Check(string key)
    {
        var text = key.Replace("\r\n", "\n").Trim();
        if (text.Length > MaxLength)
        {
            throw new BusinessException("Der Schlüssel ist zu lang.", Field);
        }

        if (!SshLogin.LooksLikeKey(text))
        {
            throw new BusinessException("Das ist kein privater Schlüssel. Bitte den ganzen Schlüssel einfügen, mit „-----BEGIN … PRIVATE KEY-----“ (OpenSSH, PEM) oder als PuTTY-Schlüssel (.ppk) – nicht den öffentlichen (.pub).", Field);
        }

        NeedsPassphrase(text);
        return text + "\n";
    }

    /// <summary>Keeps (<paramref name="key"/> null), replaces or removes ("") the stored key of a connection.</summary>
    internal static void Store(Guid connectionId, string? key)
    {
        if (key is null)
        {
            return;
        }

        var slot = Slot(connectionId);
        var account = slot.ToString("D");
        if (key.Length == 0)
        {
            if (LibraryStore.ProtectedPassword(slot) is not null)
            {
                TokenStore.Delete(account, SecretKind.Vault);
                LibraryStore.SetProtectedPassword(slot, null);
            }

            return;
        }

        AccessPolicy.EnsurePasswordsAllowed();
        LibraryStore.SetProtectedPassword(slot, TokenStore.Save(account, key, SecretKind.Vault));
    }

    /// <summary>The stored key (null: none here).</summary>
    internal static string? Stored(Guid connectionId)
    {
        if (AccessPolicy.ArePasswordsDisabled)
        {
            return null;
        }

        var slot = Slot(connectionId);
        var stored = LibraryStore.ProtectedPassword(slot);
        return stored is null ? null : TokenStore.Read(slot.ToString("D"), stored, SecretKind.Vault);
    }

    // The vault holds secrets by id: a connection's key has an id of its own, made from the connection's (never one of a credential).
    private static Guid Slot(Guid connectionId) => new(MD5.HashData(Encoding.UTF8.GetBytes("ssh-key/" + connectionId.ToString("D"))));
}
