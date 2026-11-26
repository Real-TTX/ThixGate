using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>What an import did.</summary>
public sealed record ImportResult(int Folders, int Connections, int Credentials, int Skipped)
{
    public string Summary => $"{Connections} Verbindung(en), {Folders} Ordner und {Credentials} Zugangsdaten übernommen"
        + (Skipped > 0 ? $", {Skipped} schon vorhanden" : "") + ".";
}

/// <summary>
/// Import and export of the library: ThixGate's own file (*.thixgate.json: folders, connections, credentials without passwords)
/// and Remote Desktop files (*.rdp). Every imported item goes through the same checks as saving it in the UI.
/// </summary>
public static class TransferService
{
    public const string FileExtension = ".thixgate.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The library as a file (no passwords, no "last used").</summary>
    public static void Export(string path)
    {
        var data = LibraryStore.Read();
        var file = new ExportFile(
            "thixgate",
            1,
            data.Folders,
            [.. data.Connections.Select(connection => connection with { LastUsed = null })],
            [.. data.Credentials.Select(credential => credential with { HasPassword = false })]);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(file, Json));
        File.Move(temporaryPath, path, overwrite: true);
    }

    /// <summary>Imports a *.thixgate.json or a *.rdp file. Items with an id that is already here are skipped.</summary>
    public static ImportResult Import(string path)
    {
        AccessPolicy.EnsureConfigurationEditable();
        if (!File.Exists(path))
        {
            throw new BusinessException("Die Datei gibt es nicht.");
        }

        return path.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase) ? ImportRdp(path) : ImportOwn(path);
    }

    private static ImportResult ImportOwn(string path)
    {
        ExportFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ExportFile>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            file = null;
        }

        if (file is null || file.Format != "thixgate")
        {
            throw new BusinessException("Das ist keine Datei von ThixGate (*.thixgate.json) und keine RDP-Datei.");
        }

        var data = LibraryStore.Read();
        int folders = 0, connections = 0, credentials = 0, skipped = 0;

        // Parents before children: folders are saved in the order of their depth.
        var pending = (file.Folders ?? []).Where(folder => data.Folders.All(existing => existing.Id != folder.Id)).ToList();
        skipped += (file.Folders?.Count ?? 0) - pending.Count;
        for (var pass = 0; pass < 32 && pending.Count > 0; pass++)
        {
            foreach (var folder in pending.ToList())
            {
                var known = LibraryService.Folders();
                if (folder.ParentId is { } parent && known.All(existing => existing.Id != parent) && pending.Any(other => other.Id == parent))
                {
                    continue;
                }

                LibraryService.SaveFolder(folder with { ParentId = known.Any(existing => existing.Id == folder.ParentId) ? folder.ParentId : null });
                pending.Remove(folder);
                folders++;
            }
        }

        foreach (var credential in file.Credentials ?? [])
        {
            if (data.Credentials.Any(existing => existing.Id == credential.Id))
            {
                skipped++;
                continue;
            }

            CredentialService.Save(credential with { HasPassword = false }, newPassword: null);
            credentials++;
        }

        var knownCredentials = CredentialService.List().Select(credential => credential.Id).ToHashSet();
        var knownFolders = LibraryService.Folders().Select(folder => folder.Id).ToHashSet();
        var accounts = SettingsService.Get().MatgateAccounts.Select(account => account.Id).ToHashSet();
        foreach (var connection in file.Connections ?? [])
        {
            if (data.Connections.Any(existing => existing.Id == connection.Id))
            {
                skipped++;
                continue;
            }

            var viaMatgate = connection.Via == ConnectVia.Matgate && connection.MatgateAccountId is { } account && accounts.Contains(account)
                && connection.MatgateConnectionId is not null;
            LibraryService.SaveConnection(connection with
            {
                FolderId = connection.FolderId is { } folderId && knownFolders.Contains(folderId) ? folderId : null,
                CredentialId = connection.CredentialId is { } credentialId && knownCredentials.Contains(credentialId) ? credentialId : null,
                Via = viaMatgate ? ConnectVia.Matgate : ConnectVia.Direct,
                LastUsed = null,
            });
            connections++;
        }

        return new ImportResult(folders, connections, credentials, skipped);
    }

    /// <summary>A Remote Desktop file: "full address", "username", "domain" and the size become one RDP connection.</summary>
    private static ImportResult ImportRdp(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(path))
        {
            // "full address:s:server.example.com:3390"
            var parts = line.Split(':', 3);
            if (parts.Length == 3)
            {
                values[parts[0].Trim()] = parts[2].Trim();
            }
        }

        var address = values.GetValueOrDefault("full address") ?? "";
        if (address.Length == 0)
        {
            throw new BusinessException("Die RDP-Datei nennt keinen Server („full address“).");
        }

        var (host, port) = SplitAddress(address);
        if (values.TryGetValue("server port", out var serverPort) && port == 0)
        {
            int.TryParse(serverPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
        }

        var user = values.GetValueOrDefault("username") ?? "";
        var domain = values.GetValueOrDefault("domain") ?? "";
        if (domain.Length == 0 && user.Contains('\\'))
        {
            domain = user[..user.IndexOf('\\')];
            user = user[(user.IndexOf('\\') + 1)..];
        }

        var resolution = "auto";
        if (values.TryGetValue("desktopwidth", out var width) && values.TryGetValue("desktopheight", out var height)
            && int.TryParse(width, out var w) && int.TryParse(height, out var h) && w >= 640 && h >= 480
            && values.GetValueOrDefault("screen mode id") == "1")
        {
            resolution = $"{w}x{h}";
        }

        LibraryService.SaveConnection(new RemoteConnection
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Protocol = ConnectionProtocol.Rdp,
            Host = host,
            Port = port == 3389 ? 0 : port,
            UserName = user,
            Domain = domain,
            Resolution = resolution,
        });
        return new ImportResult(0, 1, 0, 0);
    }

    /// <summary>"host", "host:port", "[v6]:port" or a bare IPv6.</summary>
    internal static (string Host, int Port) SplitAddress(string address)
    {
        address = address.Trim();
        if (address.StartsWith('['))
        {
            var end = address.IndexOf(']');
            if (end > 0)
            {
                var portText = address[(end + 1)..].TrimStart(':');
                return (address[1..end], int.TryParse(portText, out var bracketPort) ? bracketPort : 0);
            }
        }

        var colon = address.LastIndexOf(':');
        if (colon > 0 && address.IndexOf(':') == colon && int.TryParse(address[(colon + 1)..], out var port))
        {
            return (address[..colon], port);
        }

        return (address, 0);
    }

    private sealed record ExportFile(string Format, int Version, List<RemoteFolder>? Folders, List<RemoteConnection>? Connections, List<StoredCredential>? Credentials);
}
