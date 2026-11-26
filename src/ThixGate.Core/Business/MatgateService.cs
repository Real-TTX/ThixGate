using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>What a Matgate answered at <c>/health</c>.</summary>
public sealed record MatgateCheck(bool IsMatgate, string Version, string Message);

/// <summary>
/// Matgate as an optional addition (Matgate's docs/thixgate-plan.md, stage 2). An account is a Matgate this computer knows: checked
/// (/health), signed in through the browser (Matgate's app API: /app/authorize with PKCE, back to 127.0.0.1, a device token for this
/// computer), its library read (/app/api/library, without passwords) and kept as a copy, single connections taken over into the own
/// library; connecting "über Matgate" goes through its relay (<see cref="MatgateRelay"/>).
/// </summary>
public static class MatgateService
{
    public const string ClientId = "thixgate";

    internal static Func<HttpMessageHandler>? HandlerForTests { get; set; }

    public static IReadOnlyList<MatgateAccount> Accounts() => SettingsService.Get().MatgateAccounts;

    public static MatgateAccount? Find(Guid id) => Accounts().FirstOrDefault(account => account.Id == id);

    /// <summary>Adds or updates an account after the checks (address, name, policy). Another address ends the sign-in.</summary>
    public static MatgateAccount Save(MatgateAccount account)
    {
        AccessPolicy.EnsureConfigurationEditable();
        AccessPolicy.EnsureMatgateAllowed();
        var name = account.Name.Trim();
        if (name.Length == 0)
        {
            throw new BusinessException("Bitte geben Sie dem Matgate einen Namen.", nameof(MatgateAccount.Name));
        }

        var url = CloudSyncService.NormalizeServerUrl(account.ServerUrl);
        var accounts = Accounts();
        if (accounts.Any(item => item.Id != account.Id && string.Equals(item.ServerUrl, url, StringComparison.OrdinalIgnoreCase)))
        {
            throw new BusinessException("Dieses Matgate ist schon eingerichtet.", nameof(MatgateAccount.ServerUrl));
        }

        var saved = account with { Name = name, ServerUrl = url, UserName = account.UserName.Trim() };
        if (Find(account.Id) is { } before && !string.Equals(before.ServerUrl, url, StringComparison.OrdinalIgnoreCase) && before.IsSignedIn)
        {
            // The token belongs to the old address.
            ForgetSignIn(before.Id);
            saved = saved with { DeviceId = null, ProtectedToken = "", DisplayName = "", LibraryFetched = null };
        }

        SettingsService.UpdateMatgateAccounts(list => [.. list.Where(item => item.Id != saved.Id), saved]);
        return saved;
    }

    /// <summary>Removes an account. Connections "über" it go direct again.</summary>
    public static void Delete(Guid id)
    {
        AccessPolicy.EnsureConfigurationEditable();
        if (Find(id) is null)
        {
            throw new BusinessException("Dieses Matgate gibt es nicht mehr.");
        }

        foreach (var connection in LibraryService.Connections().Where(connection => connection.MatgateAccountId == id))
        {
            LibraryService.SaveConnection(connection with { Via = ConnectVia.Direct, MatgateAccountId = null, MatgateConnectionId = null });
        }

        ForgetSignIn(id);
        SettingsService.UpdateMatgateAccounts(list => [.. list.Where(item => item.Id != id)]);
    }

    /// <summary>Asks the server whether it is a Matgate (and which version); remembers the answer with a saved account.</summary>
    public static async Task<MatgateCheck> CheckAsync(string serverUrl, Guid? accountId = null, CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureMatgateAllowed();
        var url = CloudSyncService.NormalizeServerUrl(serverUrl);
        MatgateCheck result;
        try
        {
            using var http = CreateHttp();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var health = await http.GetFromJsonAsync<HealthAnswer>(url + "/health", timeout.Token);
            result = health switch
            {
                { App: "matgate" } => new MatgateCheck(true, health.Version ?? "", $"Matgate {health.Version} ist erreichbar."),
                { Status: "ok" } => new MatgateCheck(true, "", "Matgate ist erreichbar (eine ältere Version ohne Versionsangabe)."),
                _ => new MatgateCheck(false, "", "Unter dieser Adresse antwortet kein Matgate."),
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            result = new MatgateCheck(false, "", $"Matgate ist unter {url} nicht erreichbar.");
        }

        if (accountId is { } id && Find(id) is not null && result.IsMatgate)
        {
            SettingsService.UpdateMatgateAccounts(list => [.. list.Select(item => item.Id == id
                ? item with { LastChecked = AppClock.UtcNow, LastVersion = result.Version }
                : item)]);
        }

        return result;
    }

    /// <summary>The address that opens the account's Matgate in the browser.</summary>
    public static string BrowserAddress(Guid id) => Require(id).ServerUrl + "/";

    // ---- Sign-in ----

    /// <summary>
    /// Signs this computer in: opens Matgate's question "ThixGate auf … zulassen?" in the browser (<paramref name="openBrowser"/>), where the
    /// user signs in as always (password, second factor, MatIdentity, Entra); Matgate sends a one-time code back to 127.0.0.1, which is
    /// traded - with the PKCE verifier only this process knows - for the device token. Waits at most five minutes.
    /// </summary>
    public static async Task<MatgateAccount> SignInAsync(Guid id, Action<string> openBrowser, CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureMatgateAllowed();
        var account = Require(id);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var callback = new LoopbackCallback();
        var authorize = $"{account.ServerUrl}/app/authorize?client_id={ClientId}"
            + $"&redirect_uri={Uri.EscapeDataString(callback.RedirectUri)}"
            + $"&code_challenge={challenge}&code_challenge_method=S256"
            + $"&state={state}&device={Uri.EscapeDataString(AppInfo.MachineName)}";
        openBrowser(authorize);

        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waiting.CancelAfter(TimeSpan.FromMinutes(5));
        IReadOnlyDictionary<string, string> answer;
        try
        {
            answer = await callback.WaitAsync("ThixGate", "Fertig – du kannst dieses Fenster schließen und zu ThixGate zurückkehren.", waiting.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BusinessException("Die Anmeldung im Browser wurde nicht abgeschlossen (nach fünf Minuten abgebrochen).");
        }

        if (!answer.TryGetValue("state", out var returned) || returned != state)
        {
            throw new BusinessException("Die Antwort aus dem Browser passt nicht zu dieser Anmeldung.");
        }

        if (answer.TryGetValue("error", out var error))
        {
            throw new BusinessException(error == "access_denied" ? "Die Anmeldung wurde in Matgate abgebrochen." : $"Matgate hat die Anmeldung abgelehnt ({error}).");
        }

        if (!answer.TryGetValue("code", out var code) || code.Length == 0)
        {
            throw new BusinessException("Matgate hat keinen Anmeldecode geschickt.");
        }

        TokenAnswer token;
        try
        {
            using var http = CreateHttp();
            using var response = await http.PostAsJsonAsync(account.ServerUrl + "/app/api/token",
                new { code, codeVerifier = verifier, redirectUri = callback.RedirectUri }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new BusinessException(response.StatusCode == HttpStatusCode.TooManyRequests
                    ? "Zu viele Anmeldungen in kurzer Zeit – bitte in ein paar Minuten noch einmal."
                    : "Matgate hat den Anmeldecode nicht angenommen. Bitte noch einmal anmelden.");
            }

            token = await response.Content.ReadFromJsonAsync<TokenAnswer>(cancellationToken) ?? throw new BusinessException("Matgate hat nicht geantwortet.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw new BusinessException($"Matgate ist unter {account.ServerUrl} nicht erreichbar.");
        }

        if (string.IsNullOrEmpty(token.Token) || token.User is null)
        {
            throw new BusinessException("Matgate hat keinen Geräteschlüssel geschickt.");
        }

        var protectedToken = TokenStore.Save(id.ToString("D"), token.Token, SecretKind.Matgate);
        SettingsService.UpdateMatgateAccounts(list => [.. list.Select(item => item.Id == id
            ? item with
            {
                DeviceId = token.DeviceId,
                ProtectedToken = protectedToken,
                UserName = token.User.UserName ?? "",
                DisplayName = token.User.DisplayName ?? "",
            }
            : item)]);
        Log.Info($"Matgate {id}: signed in as device {token.DeviceId}.");
        return Require(id);
    }

    /// <summary>Signs this computer out: tells Matgate (its device goes there too) and forgets token and library here.</summary>
    public static async Task SignOutAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var account = Require(id);
        if (ReadToken(account) is { } token)
        {
            try
            {
                using var http = CreateHttp(token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var _ = await http.PostAsync(account.ServerUrl + "/app/api/logout", content: null, timeout.Token);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Not reachable: the device stays listed in Matgate until it is removed there; here it is gone.
                Log.Warning($"Matgate {id}: sign-out not delivered ({exception.GetType().Name}).");
            }
        }

        ForgetSignIn(id);
        SettingsService.UpdateMatgateAccounts(list => [.. list.Select(item => item.Id == id
            ? item with { DeviceId = null, ProtectedToken = "", DisplayName = "", LibraryFetched = null }
            : item)]);
    }

    // ---- Library ----

    /// <summary>The copy of the account's library on this computer; null before the first read.</summary>
    public static MatgateLibrary? Library(Guid id) => MatgateStore.Read(id);

    /// <summary>Reads the library from Matgate (only what changed travels: If-None-Match). An answer 401 means the device was signed out.</summary>
    public static async Task<MatgateLibrary> RefreshLibraryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureMatgateAllowed();
        var account = Require(id);
        var token = ReadToken(account) ?? throw new BusinessException($"Bitte zuerst bei „{account.Name}“ anmelden.");

        // Its version along with it: an older Matgate lists no files, websites and databases (the Matgate page says so).
        await CheckAsync(account.ServerUrl, id, cancellationToken);
        var cached = MatgateStore.Read(id);
        MatgateLibrary library;
        try
        {
            using var http = CreateHttp(token);
            using var request = new HttpRequestMessage(HttpMethod.Get, account.ServerUrl + "/app/api/library");
            if (cached is { Revision.Length: > 0 })
            {
                request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"" + cached.Revision + "\""));
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                ForgetSignIn(id);
                SettingsService.UpdateMatgateAccounts(list => [.. list.Select(item => item.Id == id
                    ? item with { DeviceId = null, ProtectedToken = "", LibraryFetched = null }
                    : item)]);
                throw new BusinessException($"Dieser Computer ist bei „{account.Name}“ nicht mehr angemeldet – bitte neu anmelden.");
            }

            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                library = cached;
            }
            else if (response.IsSuccessStatusCode)
            {
                library = MatgateStore.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                MatgateStore.Write(id, library);
            }
            else
            {
                throw new BusinessException($"Matgate hat die Bibliothek nicht geliefert ({(int)response.StatusCode}).");
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw new BusinessException($"Matgate ist unter {account.ServerUrl} nicht erreichbar.");
        }

        SettingsService.UpdateMatgateAccounts(list => [.. list.Select(item => item.Id == id
            ? item with { LibraryFetched = AppClock.UtcNow, DisplayName = library.User.DisplayName, UserName = library.User.UserName }
            : item)]);
        return library;
    }

    /// <summary>Reads the libraries of all signed-in accounts (start-up, every few minutes). Problems go to the log only.</summary>
    public static async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        if (AccessPolicy.IsMatgateDisabled)
        {
            return;
        }

        foreach (var account in Accounts().Where(account => account.IsSignedIn))
        {
            try
            {
                await RefreshLibraryAsync(account.Id, cancellationToken);
            }
            catch (BusinessException exception)
            {
                Log.Warning($"Matgate {account.Id}: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// A connection of Matgate as ThixGate opens it: over Matgate's relay, with the login ThixGate has (the user name from Matgate,
    /// the password asked at connect) - or, for a remote desktop whose login only Matgate has or that may only be watched, as a session
    /// on Matgate. Not saved; <see cref="TakeOver"/> saves one.
    /// </summary>
    public static RemoteConnection AsConnection(Guid accountId, MatgateConnectionItem item)
    {
        var protocol = item.ProtocolKind ?? throw new BusinessException("Diese Art von Verbindung öffnet ThixGate nicht.");
        if (item.IsWatchOnly && !protocol.IsScreen())
        {
            throw new BusinessException("Diese Verbindung darfst du in Matgate nur ansehen – das geht nur in Matgate selbst.");
        }

        return new RemoteConnection
        {
            Id = item.Id,
            Name = item.Name,
            Protocol = protocol,
            Host = item.Host,
            Port = item.Port == RemoteConnection.DefaultPort(protocol) ? 0 : item.Port,
            UserName = item.UserName,
            Domain = protocol.HasDomain() ? item.Domain : "",
            Notes = item.Notes,
            IgnoreCertificate = item.IgnoreCertificate,
            Url = item.Url,
            RootPath = item.RootPath,
            Database = item.Database,
            KeyboardLayout = KeyboardLayoutId(item.KeyboardLayout),
            Via = ConnectVia.Matgate,
            MatgateAccountId = accountId,
            MatgateConnectionId = item.Id,
            SessionOnMatgate = protocol.IsScreen() && (item.HasStoredLogin || item.IsWatchOnly),
        };
    }

    /// <summary>The own connection taken over from this Matgate connection, if there is one.</summary>
    public static RemoteConnection? TakenOver(Guid accountId, Guid matgateConnectionId) =>
        LibraryService.Connections().FirstOrDefault(connection => connection.MatgateAccountId == accountId && connection.MatgateConnectionId == matgateConnectionId);

    /// <summary>
    /// Takes a Matgate connection over into the own library: same name, folder (created where missing), user and options, connecting
    /// over this Matgate. Taken over before: that one is returned.
    /// </summary>
    public static RemoteConnection TakeOver(Guid accountId, Guid matgateConnectionId)
    {
        AccessPolicy.EnsureConfigurationEditable();
        AccessPolicy.EnsureMatgateAllowed();
        Require(accountId);
        if (TakenOver(accountId, matgateConnectionId) is { } existing)
        {
            return existing;
        }

        var item = Library(accountId)?.Connections.FirstOrDefault(connection => connection.Id == matgateConnectionId)
            ?? throw new BusinessException("Diese Verbindung gibt es in Matgate nicht (mehr).");
        var connection = AsConnection(accountId, item) with { Id = Guid.NewGuid(), FolderId = EnsureFolder(item.Folder) };
        return LibraryService.SaveConnection(connection);
    }

    // ---- Writing back (Matgate's POST/PUT/DELETE /app/api/connections) ----

    /// <summary>
    /// Creates (<paramref name="matgateConnectionId"/> null) or changes a connection in the account's Matgate, then reads the library
    /// again. Matgate decides who may: creating needs "Verbindungen anlegen" (own) or "verwalten" (shared), changing "Bearbeiten".
    /// Only what a connection is goes there - never a password.
    /// </summary>
    public static async Task<MatgateConnectionItem> SaveConnectionAsync(Guid accountId, Guid? matgateConnectionId, MatgateConnectionDraft draft, CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureMatgateAllowed();
        var checkedDraft = CheckDraft(draft);
        var account = Require(accountId);
        var token = ReadToken(account) ?? throw new BusinessException($"Bitte zuerst bei „{account.Name}“ anmelden.");
        var body = new
        {
            name = checkedDraft.Name,
            protocol = checkedDraft.Protocol.MatgateName(),
            host = checkedDraft.Host,
            port = checkedDraft.Port,
            userName = checkedDraft.UserName,
            domain = checkedDraft.Protocol.HasDomain() ? checkedDraft.Domain : "",
            folder = checkedDraft.Folder,
            notes = checkedDraft.Notes,
            keyboardLayout = checkedDraft.KeyboardLayout,
            ignoreCertificate = checkedDraft.IgnoreCertificate,
            shared = matgateConnectionId is null ? checkedDraft.Shared : (bool?)null,
            url = checkedDraft.Protocol == ConnectionProtocol.Web ? checkedDraft.Url : null,
            rootPath = checkedDraft.Protocol.KindOf() == ConnectionKind.Files ? checkedDraft.RootPath : null,
            database = checkedDraft.Protocol.KindOf() == ConnectionKind.Database ? checkedDraft.Database : null,
        };

        MatgateConnectionItem saved;
        try
        {
            using var http = CreateHttp(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = matgateConnectionId is { } id
                ? await http.PutAsJsonAsync($"{account.ServerUrl}/app/api/connections/{id:D}", body, timeout.Token)
                : await http.PostAsJsonAsync($"{account.ServerUrl}/app/api/connections", body, timeout.Token);
            await ThrowForAsync(response, account, timeout.Token);
            saved = MatgateStore.ParseConnection(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw new BusinessException($"Matgate ist unter {account.ServerUrl} nicht erreichbar.");
        }

        Log.Info($"Matgate {accountId}: connection {saved.Id} {(matgateConnectionId is null ? "created" : "changed")}.");
        await RefreshQuietlyAsync(accountId, cancellationToken);
        return saved;
    }

    /// <summary>Deletes a connection in the account's Matgate ("Verwalten" on it), then reads the library again.</summary>
    public static async Task DeleteConnectionAsync(Guid accountId, Guid matgateConnectionId, CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureMatgateAllowed();
        var account = Require(accountId);
        var token = ReadToken(account) ?? throw new BusinessException($"Bitte zuerst bei „{account.Name}“ anmelden.");
        try
        {
            using var http = CreateHttp(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await http.DeleteAsync($"{account.ServerUrl}/app/api/connections/{matgateConnectionId:D}", timeout.Token);
            await ThrowForAsync(response, account, timeout.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw new BusinessException($"Matgate ist unter {account.ServerUrl} nicht erreichbar.");
        }

        Log.Info($"Matgate {accountId}: connection {matgateConnectionId} deleted.");
        await RefreshQuietlyAsync(accountId, cancellationToken);
    }

    /// <summary>The checks of the form, before anything goes to Matgate (Matgate checks again).</summary>
    public static MatgateConnectionDraft CheckDraft(MatgateConnectionDraft draft)
    {
        var cleaned = draft with
        {
            Name = draft.Name.Trim(),
            Host = draft.Host.Trim(),
            UserName = draft.UserName.Trim(),
            Domain = draft.Protocol.HasDomain() ? draft.Domain.Trim() : "",
            Url = draft.Protocol == ConnectionProtocol.Web ? LibraryService.WebAddress(draft.Url) : "",
            RootPath = draft.Protocol.KindOf() == ConnectionKind.Files ? draft.RootPath.Trim() : "",
            Database = draft.Protocol.KindOf() == ConnectionKind.Database ? draft.Database.Trim() : "",
            Folder = string.Join('/', draft.Folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            Notes = draft.Notes.Trim(),
        };
        if (!Enum.IsDefined(cleaned.Protocol))
        {
            throw new BusinessException("Dieses Protokoll gibt es nicht.", nameof(MatgateConnectionDraft.Protocol));
        }

        if (cleaned.Name.Length == 0)
        {
            throw new BusinessException("Bitte geben Sie der Verbindung einen Namen.", nameof(MatgateConnectionDraft.Name));
        }

        if (cleaned.Name.Length > 200)
        {
            throw new BusinessException("Der Name ist zu lang (höchstens 200 Zeichen).", nameof(MatgateConnectionDraft.Name));
        }

        if (cleaned.Protocol == ConnectionProtocol.Web)
        {
            if (!Uri.TryCreate(cleaned.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            {
                throw new BusinessException("Bitte geben Sie die Adresse der Website an (https://…).", nameof(MatgateConnectionDraft.Url));
            }

            cleaned = cleaned with { Host = url.Host, Port = 0 };
        }

        if (cleaned.Host.Length == 0)
        {
            throw new BusinessException("Bitte geben Sie die Adresse an.", nameof(MatgateConnectionDraft.Host));
        }

        if (cleaned.Host.Length > 253 || cleaned.Host.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new BusinessException("Diese Adresse ist ungültig.", nameof(MatgateConnectionDraft.Host));
        }

        if (cleaned.Port is < 0 or > 65535)
        {
            throw new BusinessException("Der Port liegt zwischen 1 und 65535 (leer: Standard).", nameof(MatgateConnectionDraft.Port));
        }

        return cleaned;
    }

    // Matgate's answer as a message at the right field.
    private static async Task ThrowForAsync(HttpResponseMessage response, MatgateAccount account, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new BusinessException($"Dieser Computer ist bei „{account.Name}“ nicht mehr angemeldet – bitte neu anmelden.");
        }

        ProblemAnswer? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync<ProblemAnswer>(cancellationToken);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException)
        {
        }

        var field = problem?.Field switch
        {
            "name" => nameof(MatgateConnectionDraft.Name),
            "host" => nameof(MatgateConnectionDraft.Host),
            "port" => nameof(MatgateConnectionDraft.Port),
            "protocol" => nameof(MatgateConnectionDraft.Protocol),
            "userName" => nameof(MatgateConnectionDraft.UserName),
            "folder" => nameof(MatgateConnectionDraft.Folder),
            "notes" => nameof(MatgateConnectionDraft.Notes),
            _ => null,
        };
        var message = (response.StatusCode, problem?.Error) switch
        {
            (HttpStatusCode.Forbidden, _) => $"Das darfst du in „{account.Name}“ nicht (dafür braucht es dort die passenden Rechte).",
            (HttpStatusCode.NotFound, _) => $"Diese Verbindung gibt es in „{account.Name}“ nicht (mehr).",
            (_, "required") => "Bitte ausfüllen.",
            (_, "too_long") => "Das ist zu lang.",
            (_, "invalid") => "Das ist ungültig.",
            (_, "protocol_fixed") => "Das Protokoll einer Verbindung lässt sich in Matgate nicht ändern.",
            (_, "protocol") => "Matgate nimmt hier nur Remote Desktop, VNC und SSH an.",
            _ => $"Matgate hat das nicht angenommen ({(int)response.StatusCode}).",
        };
        throw new BusinessException(message, field);
    }

    private static async Task RefreshQuietlyAsync(Guid accountId, CancellationToken cancellationToken)
    {
        try
        {
            await RefreshLibraryAsync(accountId, cancellationToken);
        }
        catch (BusinessException exception)
        {
            Log.Warning($"Matgate {accountId}: {exception.Message}");
        }
    }

    /// <summary>The device token of a signed-in account (for the relay).</summary>
    internal static string? ReadToken(MatgateAccount account) =>
        account.IsSignedIn ? TokenStore.Read(account.Id.ToString("D"), account.ProtectedToken, SecretKind.Matgate) : null;

    /// <summary>Windows' keyboard layout id (KLID) of Matgate's layout names (Matgate's DisplayBridge.KeyboardLayoutId).</summary>
    public static uint KeyboardLayoutId(string? layout) => (layout ?? "").Trim().ToLowerInvariant() switch
    {
        "de-ch-qwertz" => 0x00000807,
        "en-us-qwerty" => 0x00000409,
        "en-gb-qwerty" => 0x00000809,
        "fr-fr-azerty" => 0x0000040C,
        "fr-be-azerty" => 0x0000080C,
        "fr-ch-qwertz" => 0x0000100C,
        "it-it-qwerty" => 0x00000410,
        "es-es-qwerty" => 0x0000040A,
        "es-latam-qwerty" => 0x0000080A,
        "pt-br-qwerty" => 0x00000416,
        "sv-se-qwerty" => 0x0000041D,
        "da-dk-qwerty" => 0x00000406,
        "nb-no-qwerty" => 0x00000414,
        "hu-hu-qwertz" => 0x0000040E,
        "tr-tr-qwerty" => 0x0000041F,
        "ja-jp-qwerty" => 0x00000411,
        _ => 0x00000407,
    };

    internal static HttpClient CreateHttp(string? token = null)
    {
        var handler = HandlerForTests?.Invoke();
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(CloudClient.UserAgent);
        if (token is not null)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return http;
    }

    private static MatgateAccount Require(Guid id) => Find(id) ?? throw new BusinessException("Dieses Matgate gibt es nicht mehr.");

    private static void ForgetSignIn(Guid id)
    {
        TokenStore.Delete(id.ToString("D"), SecretKind.Matgate);
        MatgateStore.Delete(id);
    }

    // "Labor/Linux" → the folder Linux in Labor, created where missing; "" → no folder.
    private static Guid? EnsureFolder(string path)
    {
        Guid? parent = null;
        foreach (var name in path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var existing = LibraryService.Folders().FirstOrDefault(folder => folder.ParentId == parent
                && string.Equals(folder.Name, name, StringComparison.CurrentCultureIgnoreCase));
            parent = (existing ?? LibraryService.SaveFolder(new RemoteFolder { Name = name, ParentId = parent })).Id;
        }

        return parent;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record HealthAnswer(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("app")] string? App,
        [property: JsonPropertyName("version")] string? Version);

    private sealed record TokenAnswer(
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("deviceId")] Guid DeviceId,
        [property: JsonPropertyName("user")] TokenUser? User);

    private sealed record ProblemAnswer(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("field")] string? Field);

    private sealed record TokenUser(
        [property: JsonPropertyName("userName")] string? UserName,
        [property: JsonPropertyName("displayName")] string? DisplayName);
}
