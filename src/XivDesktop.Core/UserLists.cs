namespace XivDesktop.Core;

/// <summary>
/// Favourites and recents as immutable-style list operations: each returns a new list so the plugin can
/// swap its configuration lists wholesale (readers on other threads never see a half-updated list).
/// </summary>
public static class UserLists
{
    public const int MaxRecents = 12;

    public static bool Contains(IReadOnlyList<string> list, string id)
    {
        foreach (var s in list)
        {
            if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Adds the id to favourites, or removes it when present.</summary>
    public static List<string> ToggleFavourite(IReadOnlyList<string> favourites, string id)
        => Contains(favourites, id) ? Without(favourites, id) : [.. favourites, id];

    /// <summary>Moves the id to the front of recents, keeping at most <paramref name="max"/> entries.</summary>
    public static List<string> PushRecent(IReadOnlyList<string> recents, string id, int max = MaxRecents)
    {
        var next = new List<string>(Math.Min(max, recents.Count + 1)) { id };
        foreach (var s in recents)
        {
            if (next.Count >= max)
                break;
            if (!string.Equals(s, id, StringComparison.OrdinalIgnoreCase))
                next.Add(s);
        }

        return next;
    }

    public static List<string> Without(IReadOnlyList<string> list, string id)
        => list.Where(s => !string.Equals(s, id, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The ids that still exist in the catalog, in list order.</summary>
    public static List<AppInfo> Resolve(IReadOnlyList<string> ids, AppCatalog catalog)
    {
        var result = new List<AppInfo>(ids.Count);
        foreach (var id in ids)
        {
            if (catalog.Find(id) is { } app)
                result.Add(app);
        }

        return result;
    }
}
