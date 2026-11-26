using System.Text.Json;
using ThixGate.Core.Infrastructure;

namespace ThixGate.Core.Business;

/// <summary>
/// The last queries of each database connection (as Matgate's history: the newest first, at most 100, the same one only once), kept per
/// computer in query-history.json - never synced, a query may contain data.
/// </summary>
public static class QueryHistory
{
    public const int MaxEntries = 100;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static string FilePath => Path.Combine(AppPaths.DataDirectory, "query-history.json");

    public static IReadOnlyList<string> Of(Guid connectionId)
    {
        lock (Gate)
        {
            return Read().GetValueOrDefault(connectionId) ?? [];
        }
    }

    public static void Add(Guid connectionId, string sql)
    {
        var text = sql.Trim();
        if (text.Length == 0 || text.Length > 100_000)
        {
            return;
        }

        lock (Gate)
        {
            var all = Read();
            var list = all.GetValueOrDefault(connectionId) ?? [];
            list.RemoveAll(entry => entry == text);
            list.Insert(0, text);
            if (list.Count > MaxEntries)
            {
                list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            }

            all[connectionId] = list;
            try
            {
                LibraryStore.WriteAtomically(FilePath, JsonSerializer.Serialize(all, Options));
            }
            catch (IOException exception)
            {
                Log.Warning($"The query history could not be written: {exception.Message}");
            }
        }
    }

    private static Dictionary<Guid, List<string>> Read()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<Guid, List<string>>>(File.ReadAllText(FilePath), Options) ?? [] : [];
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            Log.Warning($"The query history could not be read: {exception.Message}");
            return [];
        }
    }
}
