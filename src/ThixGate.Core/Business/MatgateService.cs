using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>What a Matgate answered at <c>/health</c>.</summary>
public sealed record MatgateCheck(bool IsMatgate, string Version, string Message);

/// <summary>
/// Matgate as an optional addition (docs/thixgate-plan.md, stage 2): the Matgate servers this computer knows. Today an account is
/// checked (is it a Matgate, which version), can be opened in the browser and chosen per connection as "Verbinden über"; the app
/// sign-in, the library from Matgate and the relay through it come with Matgate's app API.
/// </summary>
public static class MatgateService
{
    internal static Func<HttpMessageHandler>? HandlerForTests { get; set; }

    public static IReadOnlyList<MatgateAccount> Accounts() => SettingsService.Get().MatgateAccounts;

    public static MatgateAccount? Find(Guid id) => Accounts().FirstOrDefault(account => account.Id == id);

    /// <summary>Adds or updates an account after the checks (address, name, policy).</summary>
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
            LibraryService.SaveConnection(connection with { Via = ConnectVia.Direct, MatgateAccountId = null });
        }

        TokenStore.Delete(id.ToString("D"), SecretKind.Matgate);
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
    public static string BrowserAddress(Guid id) =>
        (Find(id) ?? throw new BusinessException("Dieses Matgate gibt es nicht mehr.")).ServerUrl + "/";

    private static HttpClient CreateHttp()
    {
        var handler = HandlerForTests?.Invoke();
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(CloudClient.UserAgent);
        return http;
    }

    private sealed record HealthAnswer(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("app")] string? App,
        [property: JsonPropertyName("version")] string? Version);
}
