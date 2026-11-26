using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>
/// The vault: saved credentials, usable by many connections. The password is protected for this user (Windows DPAPI in vault.json,
/// macOS Keychain) and never leaves the computer - the cloud carries name, user and domain only, so on another computer the
/// password is asked once and kept there.
/// </summary>
public static class CredentialService
{
    public static IReadOnlyList<StoredCredential> List(string search = "")
    {
        var text = search.Trim();
        return [.. LibraryStore.Read().Credentials
            .Where(credential => text.Length == 0
                || credential.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || credential.UserName.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || credential.Domain.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(credential => credential.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public static StoredCredential? Find(Guid id) => LibraryStore.Read().Credentials.FirstOrDefault(credential => credential.Id == id);

    /// <summary>How many connections use these credentials.</summary>
    public static int UsageCount(Guid id) => LibraryStore.Read().Connections.Count(connection => connection.CredentialId == id);

    /// <summary>
    /// Creates or updates credentials. <paramref name="newPassword"/>: null keeps the stored password, "" removes it, anything else
    /// replaces it.
    /// </summary>
    public static StoredCredential Save(StoredCredential credential, string? newPassword)
    {
        AccessPolicy.EnsureConfigurationEditable();
        if (!string.IsNullOrEmpty(newPassword))
        {
            AccessPolicy.EnsurePasswordsAllowed();
        }

        var name = credential.Name.Trim();
        if (name.Length == 0)
        {
            throw new BusinessException("Bitte geben Sie den Zugangsdaten einen Namen.", nameof(StoredCredential.Name));
        }

        if (name.Length > LibraryService.MaxNameLength)
        {
            throw new BusinessException($"Der Name darf höchstens {LibraryService.MaxNameLength} Zeichen lang sein.", nameof(StoredCredential.Name));
        }

        if (credential.UserName.Trim().Length == 0)
        {
            throw new BusinessException("Bitte geben Sie den Benutzernamen an.", nameof(StoredCredential.UserName));
        }

        if (newPassword is { Length: > 1024 })
        {
            throw new BusinessException("Das Passwort ist zu lang.", "Password");
        }

        var data = LibraryStore.Read();
        if (data.Credentials.Any(item => item.Id != credential.Id && string.Equals(item.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new BusinessException($"Zugangsdaten mit dem Namen „{name}“ gibt es schon.", nameof(StoredCredential.Name));
        }

        var existing = data.Credentials.FirstOrDefault(item => item.Id == credential.Id);
        var hasPassword = newPassword switch
        {
            null => existing?.HasPassword ?? false,
            "" => false,
            _ => true,
        };

        if (newPassword is not null)
        {
            StorePassword(credential.Id, newPassword);
        }

        var now = AppClock.UtcNow;
        var saved = credential with
        {
            Name = name,
            UserName = credential.UserName.Trim(),
            Domain = credential.Domain.Trim(),
            Notes = credential.Notes.Trim(),
            HasPassword = hasPassword,
            CreateDate = existing?.CreateDate ?? now,
            UpdateDate = LibraryService.NextStamp(existing?.UpdateDate, now),
        };

        LibraryStore.Update(library =>
        {
            library.Credentials.RemoveAll(item => item.Id == saved.Id);
            library.Credentials.Add(saved);
        });
        LibraryService.Raise();
        return saved;
    }

    /// <summary>Deletes credentials; connections that used them keep their user name and ask for the password.</summary>
    public static void Delete(Guid id)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var credential = Find(id) ?? throw new BusinessException("Diese Zugangsdaten gibt es nicht mehr.");
        var now = AppClock.UtcNow;
        LibraryStore.Update(library =>
        {
            library.Credentials.RemoveAll(item => item.Id == id);
            library.Tombstones.Add(new Tombstone(SyncCollections.Credentials, id, now));
            for (var index = 0; index < library.Connections.Count; index++)
            {
                var connection = library.Connections[index];
                if (connection.CredentialId == id)
                {
                    library.Connections[index] = connection with
                    {
                        CredentialId = null,
                        UserName = connection.UserName.Length > 0 ? connection.UserName : credential.UserName,
                        Domain = connection.Domain.Length > 0 ? connection.Domain : credential.Domain,
                        UpdateDate = LibraryService.NextStamp(connection.UpdateDate, now),
                    };
                }
            }
        });
        StorePassword(id, "");
        LibraryService.Raise();
    }

    /// <summary>The stored password (only for connecting; never shown, logged or synced). Null: none on this computer.</summary>
    public static string? ReadPassword(Guid id)
    {
        if (AccessPolicy.ArePasswordsDisabled)
        {
            return null;
        }

        var stored = LibraryStore.ProtectedPassword(id);
        return stored is null ? null : TokenStore.Read(id.ToString("D"), stored, SecretKind.Vault);
    }

    /// <summary>
    /// Keeps the password a person typed at connect for credentials that have none on this computer yet (the "Speichern" box of the
    /// sign-in dialog).
    /// </summary>
    public static void RememberPassword(Guid id, string password)
    {
        AccessPolicy.EnsurePasswordsAllowed();
        var credential = Find(id) ?? throw new BusinessException("Diese Zugangsdaten gibt es nicht mehr.");
        StorePassword(id, password);
        if (!credential.HasPassword)
        {
            // HasPassword is about THIS computer: not a change for the cloud.
            LibraryStore.Update(library =>
            {
                var index = library.Credentials.FindIndex(item => item.Id == id);
                if (index >= 0)
                {
                    library.Credentials[index] = library.Credentials[index] with { HasPassword = password.Length > 0 };
                }
            });
            LibraryService.Raise(syncToo: false);
        }
    }

    private static void StorePassword(Guid id, string password)
    {
        var account = id.ToString("D");
        if (password.Length == 0)
        {
            TokenStore.Delete(account, SecretKind.Vault);
            LibraryStore.SetProtectedPassword(id, null);
            return;
        }

        LibraryStore.SetProtectedPassword(id, TokenStore.Save(account, password, SecretKind.Vault));
    }
}
