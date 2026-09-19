namespace XivDesktop.Core.Claude;

/// <summary>A directory the session could run in: its Linux path and how it is shown.</summary>
public sealed record WorkingDirectoryChoice(string Path, string Display, bool IsRepo)
{
    public static WorkingDirectoryChoice Of(string home, string path, bool isRepo)
        => new(path, Shorten(home, path), isRepo);

    /// <summary>"~/code/thing" rather than the whole path.</summary>
    public static string Shorten(string home, string path)
        => home.Length > 0 && path.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + path[home.Length..]
        : home.Length > 0 && path == home ? "~"
        : path;
}

/// <summary>
/// Lists the directories a Claude session could work in: the home directory itself, then the repositories
/// under it (the directories holding a <c>.git</c>). Directory access is injected, so this is tested on a
/// temp tree and works the same under Wine, where the Linux paths are mapped by <see cref="HostPaths"/>.
/// </summary>
public static class RepoPicker
{
    /// <summary>How deep under the home directory a repository is looked for.</summary>
    public const int MaxDepth = 3;

    /// <summary>Directories never descended into: build output, caches, and anything hidden.</summary>
    private static readonly HashSet<string> Skip = new(StringComparer.Ordinal)
    {
        "node_modules", "target", "build", "dist", "obj", "bin", "vendor", "venv", "__pycache__",
        "Steam", "SteamLibrary", ".local", ".cache", ".config", ".var",
    };

    /// <summary>
    /// The home directory, then every repository under it, alphabetically.
    /// <paramref name="children"/> lists a directory's subdirectories as full Linux paths;
    /// <paramref name="exists"/> answers whether a path is there. Both are given Linux paths and may
    /// map them however the caller needs.
    /// </summary>
    public static List<WorkingDirectoryChoice> Scan(
        string home,
        Func<string, IEnumerable<string>> children,
        Func<string, bool> exists,
        int maxDepth = MaxDepth,
        int limit = 200)
    {
        var results = new List<WorkingDirectoryChoice>();
        if (home.Length == 0)
            return results;
        results.Add(WorkingDirectoryChoice.Of(home, home, IsRepo(home, exists)));

        var repos = new List<string>();
        Walk(home, 0);
        repos.Sort(StringComparer.Ordinal);
        foreach (var repo in repos)
        {
            if (results.Count >= limit)
                break;
            results.Add(WorkingDirectoryChoice.Of(home, repo, true));
        }

        return results;

        void Walk(string directory, int depth)
        {
            if (depth >= maxDepth || repos.Count >= limit)
                return;
            IEnumerable<string> subdirectories;
            try
            {
                subdirectories = children(directory);
            }
            catch (Exception)
            {
                return;
            }

            foreach (var child in subdirectories)
            {
                var name = Name(child);
                if (name.Length == 0 || name[0] == '.' || Skip.Contains(name))
                    continue;
                if (IsRepo(child, exists))
                {
                    repos.Add(child);
                    continue; // a repository's own subdirectories are not separate choices
                }

                Walk(child, depth + 1);
            }
        }
    }

    public static bool IsRepo(string directory, Func<string, bool> exists)
    {
        try
        {
            return exists(directory + "/.git");
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The directory a session should start in: the configured one when it is set and usable, else home.
    /// </summary>
    public static string Resolve(string configured, string home, Func<string, bool> directoryExists)
    {
        var wanted = (configured ?? "").Trim().TrimEnd('/');
        if (wanted.StartsWith("~/", StringComparison.Ordinal) && home.Length > 0)
            wanted = home + wanted[1..];
        if (wanted == "~")
            wanted = home;
        if (wanted.Length == 0)
            return home;
        try
        {
            return directoryExists(wanted) ? wanted : home;
        }
        catch (Exception)
        {
            return home;
        }
    }

    private static string Name(string path)
    {
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx < 0 ? trimmed : trimmed[(idx + 1)..];
    }
}
