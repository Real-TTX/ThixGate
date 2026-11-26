using System.Text.Json;
using ThixGate.Core.Models;

namespace ThixGate.Core.Infrastructure;

/// <summary>The copies of the Matgate libraries (matgate/&lt;account&gt;.json). Only <see cref="Business.MatgateService"/> uses it.</summary>
internal static class MatgateStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, MatgateLibrary?> Cache = [];
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static MatgateLibrary? Read(Guid accountId)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(accountId, out var cached))
            {
                return cached;
            }

            var path = PathOf(accountId);
            MatgateLibrary? library = null;
            try
            {
                library = File.Exists(path) ? JsonSerializer.Deserialize<MatgateLibrary>(File.ReadAllText(path), SerializerOptions) : null;
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                Log.Warning($"The Matgate library {accountId} could not be read: {exception.Message}");
            }

            Cache[accountId] = library;
            return library;
        }
    }

    public static void Write(Guid accountId, MatgateLibrary library)
    {
        lock (Gate)
        {
            LibraryStore.WriteAtomically(PathOf(accountId), JsonSerializer.Serialize(library, SerializerOptions));
            Cache[accountId] = library;
        }
    }

    public static void Delete(Guid accountId)
    {
        lock (Gate)
        {
            Cache.Remove(accountId);
            var path = PathOf(accountId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Tests start with no copies.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Cache.Clear();
        }
    }

    /// <summary>Parses what Matgate sent (the same shape as the copy).</summary>
    public static MatgateLibrary Parse(string json) =>
        JsonSerializer.Deserialize<MatgateLibrary>(json, SerializerOptions) ?? throw new JsonException("empty library");

    /// <summary>One connection as Matgate answers a create or change.</summary>
    public static MatgateConnectionItem ParseConnection(string json) =>
        JsonSerializer.Deserialize<MatgateConnectionItem>(json, SerializerOptions) ?? throw new JsonException("empty connection");

    private static string PathOf(Guid accountId) => Path.Combine(AppPaths.MatgateDirectory, accountId.ToString("D") + ".json");
}
