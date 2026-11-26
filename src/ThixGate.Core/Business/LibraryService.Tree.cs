using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

public static partial class LibraryService
{
    /// <summary>
    /// The library as the rows of the tree column (folders with their sub-folders and connections, then the connections without a folder),
    /// each level sorted by name. Only the rows that show: the children of a folder only when it is in <paramref name="expanded"/>. With a
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
        return rows;
    }

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
