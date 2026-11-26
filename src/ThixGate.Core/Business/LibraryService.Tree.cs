using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

public static partial class LibraryService
{
    /// <summary>
    /// The library as the rows of the tree of the menu (folders with their sub-folders and connections, then the connections without a folder),
    /// each level sorted by name; then every signed-in Matgate with the folders and connections of its library (straight from the copy on this
    /// computer, nothing taken over; not when a policy turns Matgate off). Only the rows that show: the children of a folder only when it is in <paramref name="expanded"/>. With a
    /// <paramref name="search"/> (name, address, user, notes, tags) only the connections that match and the folders above them, all open.
    /// </summary>
    public static IReadOnlyList<LibraryTreeRow> Tree(string search, IReadOnlySet<Guid> expanded)
    {
        var data = LibraryStore.Read();
        var term = search.Trim();
        var folderIds = data.Folders.Select(folder => folder.Id).ToHashSet();
        var connections = term.Length == 0 ? data.Connections : [.. data.Connections.Where(connection => Matches(connection, term))];
        var byFolder = connections.ToLookup(connection => connection.FolderId is { } id && folderIds.Contains(id) ? id : (Guid?)null);
        var children = data.Folders.ToLookup(folder => folder.ParentId is { } parent && folderIds.Contains(parent) ? parent : (Guid?)null);

        // How many connections a folder holds with everything below it (the number beside its name; a search keeps the folders with one).
        var counts = new Dictionary<Guid, int>();
        int Count(Guid folderId)
        {
            if (counts.TryGetValue(folderId, out var known))
            {
                return known;
            }

            counts[folderId] = 0; // a loop of parents counts once
            var count = byFolder[folderId].Count() + children[folderId].Sum(child => Count(child.Id));
            counts[folderId] = count;
            return count;
        }

        var rows = new List<LibraryTreeRow>();
        var visited = new HashSet<Guid>();
        void Add(Guid? parentId, int depth)
        {
            foreach (var folder in children[parentId].OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var count = Count(folder.Id);
                if ((term.Length > 0 && count == 0) || !visited.Add(folder.Id))
                {
                    continue;
                }

                var hasChildren = children[folder.Id].Any() || byFolder[folder.Id].Any();
                var isExpanded = hasChildren && (term.Length > 0 || expanded.Contains(folder.Id));
                rows.Add(new LibraryTreeRow { Folder = folder, Depth = depth, Count = count, HasChildren = hasChildren, IsExpanded = isExpanded });
                if (isExpanded)
                {
                    Add(folder.Id, depth + 1);
                }
            }

            foreach (var connection in byFolder[parentId].OrderBy(connection => connection.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                rows.Add(new LibraryTreeRow { Connection = connection, Depth = depth });
            }
        }

        Add(null, 0);
        if (!AccessPolicy.IsMatgateDisabled)
        {
            foreach (var account in MatgateService.Accounts().Where(account => account.IsSignedIn).OrderBy(account => account.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                AddMatgate(rows, account, MatgateService.Library(account.Id), term, expanded);
            }
        }

        return rows;
    }

    /// <summary>A Matgate and (open) its folders - the paths of its folders and of its connections - and connections.</summary>
    private static void AddMatgate(List<LibraryTreeRow> rows, MatgateAccount account, MatgateLibrary? library, string term, IReadOnlySet<Guid> expanded)
    {
        var items = (library?.Connections ?? []).Where(item => item.ProtocolKind is not null && (term.Length == 0 || Matches(item, term))).ToList();
        if (term.Length > 0 && items.Count == 0)
        {
            return;
        }

        static string Clean(string path) => string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        static string ParentOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

        // Every folder with the folders above it; with a search only the ones on the way to a match.
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = (term.Length == 0 ? (library?.Folders ?? []).Select(folder => folder.Path) : []).Concat(items.Select(item => item.Folder));
        foreach (var source in sources.Select(Clean).Where(path => path.Length > 0))
        {
            for (var path = source; path.Length > 0 && paths.Add(path); path = ParentOf(path))
            {
            }
        }

        var byFolder = items.ToLookup(item => paths.Contains(Clean(item.Folder)) ? Clean(item.Folder) : "", StringComparer.OrdinalIgnoreCase);
        var children = paths.ToLookup(ParentOf, StringComparer.OrdinalIgnoreCase);
        int Count(string path) => byFolder[path].Count() + children[path].Sum(Count);

        var open = term.Length > 0 || expanded.Contains(account.Id);
        rows.Add(new LibraryTreeRow { Matgate = account, Count = items.Count, HasChildren = items.Count > 0 || paths.Count > 0, IsExpanded = open && (items.Count > 0 || paths.Count > 0) });
        if (!open)
        {
            return;
        }

        void Add(string parent, int depth)
        {
            foreach (var path in children[parent].OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
            {
                var hasChildren = children[path].Any() || byFolder[path].Any();
                var isExpanded = hasChildren && (term.Length > 0 || expanded.Contains(LibraryTreeRow.MatgateFolderId(account.Id, path)));
                rows.Add(new LibraryTreeRow { MatgateAccountId = account.Id, MatgatePath = path, Depth = depth, Count = Count(path), HasChildren = hasChildren, IsExpanded = isExpanded });
                if (isExpanded)
                {
                    Add(path, depth + 1);
                }
            }

            foreach (var item in byFolder[parent].OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                rows.Add(new LibraryTreeRow { MatgateAccountId = account.Id, MatgateItem = item, Depth = depth });
            }
        }

        Add("", 1);
    }

    private static bool Matches(MatgateConnectionItem item, string search) =>
        item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || item.Host.Contains(search, StringComparison.OrdinalIgnoreCase)
        || item.UserName.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || item.Notes.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>The folders above a connection or folder (to open the tree down to it), the outermost first.</summary>
    public static IReadOnlyList<Guid> FolderChain(Guid? folderId)
    {
        var folders = LibraryStore.Read().Folders.ToDictionary(folder => folder.Id);
        var chain = new List<Guid>();
        var current = folderId;
        while (current is { } id && folders.TryGetValue(id, out var folder) && !chain.Contains(id))
        {
            chain.Insert(0, id);
            current = folder.ParentId;
        }

        return chain;
    }
}
