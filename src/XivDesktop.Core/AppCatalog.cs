namespace XivDesktop.Core;

/// <summary>Counts from one scan, for status lines and the scan tool.</summary>
public sealed record CatalogStats
{
    public int FilesRead { get; init; }

    public int Overridden { get; init; }

    public int NotApplications { get; init; }

    public int NoDisplay { get; init; }

    public int HiddenKey { get; init; }

    public int ShowInFiltered { get; init; }

    public int TryExecMissing { get; init; }

    public int BadExec { get; init; }

    public int Terminal { get; init; }

    public int IconsPng { get; init; }

    public int IconsSvgOnly { get; init; }

    public int IconsMissing { get; init; }
}

/// <summary>
/// Immutable result of scanning the application directories: visible apps sorted by name.
/// </summary>
public sealed class AppCatalog
{
    /// <summary>The desktop name XivDesktop matches OnlyShowIn/NotShowIn against.</summary>
    public const string DesktopName = "XivDesktop";

    public static readonly AppCatalog Empty = new([], new CatalogStats(), [], DateTimeOffset.MinValue);

    private readonly Dictionary<string, AppInfo> byId;

    public AppCatalog(IReadOnlyList<AppInfo> apps, CatalogStats stats, IReadOnlyList<string> errors, DateTimeOffset scannedAt)
    {
        Apps = apps;
        Stats = stats;
        Errors = errors;
        ScannedAt = scannedAt;
        byId = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
            byId.TryAdd(app.Id, app);
    }

    public IReadOnlyList<AppInfo> Apps { get; }

    public CatalogStats Stats { get; }

    /// <summary>Unreadable directories/files (the scan continues past them).</summary>
    public IReadOnlyList<string> Errors { get; }

    public DateTimeOffset ScannedAt { get; }

    /// <summary>Looks up by desktop-file id; "org.foo.Bar" and "org.foo.Bar.desktop" both work.</summary>
    public AppInfo? Find(string id)
    {
        if (byId.TryGetValue(id, out var app))
            return app;
        return !id.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) && byId.TryGetValue(id + ".desktop", out app) ? app : null;
    }

    /// <summary>An id, an exact (case-insensitive) name, or the best search match; null when nothing matches.</summary>
    public AppInfo? Resolve(string idOrQuery, IReadOnlyCollection<string>? favourites = null, IReadOnlyList<string>? recents = null)
    {
        var q = idOrQuery.Trim();
        if (q.Length == 0)
            return null;
        if (Find(q) is { } byIdHit)
            return byIdHit;
        foreach (var app in Apps)
        {
            if (string.Equals(app.Name, q, StringComparison.OrdinalIgnoreCase))
                return app;
        }

        var results = AppSearch.Search(Apps, q, favourites, recents);
        return results.Count > 0 ? results[0] : null;
    }

    /// <summary>
    /// Scans <see cref="HostPaths.ApplicationDirs"/> (recursively; later directories override earlier ones by
    /// desktop-file id), applies the visibility rules and resolves icons. Blocking; call it off the UI thread.
    /// </summary>
    public static AppCatalog Scan(HostPaths paths, CancellationToken cancel = default)
    {
        var errors = new List<string>();
        var entries = new Dictionary<string, DesktopEntry>(StringComparer.Ordinal);
        var filesRead = 0;
        var overridden = 0;

        foreach (var linuxDir in paths.ApplicationDirs)
        {
            cancel.ThrowIfCancellationRequested();
            var localDir = paths.ToLocal(linuxDir);
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(localDir))
                    continue;
                files = Directory.EnumerateFiles(localDir, "*.desktop", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MatchCasing = MatchCasing.CaseSensitive,
                }).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{linuxDir}: {e.Message}");
                continue;
            }

            // Within one directory the order is deterministic; across directories later wins.
            foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
            {
                var id = DesktopFileId(localDir, file);
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{file}: {e.Message}");
                    continue;
                }

                filesRead++;
                var entry = DesktopEntryParser.Parse(text, id, file);
                if (entry == null)
                    continue;
                if (entries.ContainsKey(id))
                    overridden++;
                entries[id] = entry;
            }
        }

        return Build(entries.Values, paths, filesRead, overridden, errors);
    }

    /// <summary>Applies visibility rules and icon lookup to parsed entries (the second half of <see cref="Scan"/>).</summary>
    public static AppCatalog Build(IEnumerable<DesktopEntry> entries, HostPaths paths, int filesRead = 0, int overridden = 0, List<string>? errors = null)
    {
        var icons = new IconResolver(paths);
        var apps = new List<AppInfo>();
        int notApp = 0, noDisplay = 0, hidden = 0, showIn = 0, tryExec = 0, badExec = 0, terminal = 0, png = 0, svg = 0, missing = 0;

        foreach (var e in entries)
        {
            if (e.Hidden)
            {
                hidden++;
                continue;
            }

            if (!string.Equals(e.Type, "Application", StringComparison.Ordinal) || e.Name.Length == 0)
            {
                notApp++;
                continue;
            }

            if (e.NoDisplay)
            {
                noDisplay++;
                continue;
            }

            if (!ShownIn(e, DesktopName))
            {
                showIn++;
                continue;
            }

            if (e.TryExec.Length > 0 && !ExecutableExists(e.TryExec, paths))
            {
                tryExec++;
                continue;
            }

            var command = ExecLine.ToShellCommand(e.Exec);
            if (command == null)
            {
                badExec++;
                continue;
            }

            if (e.Terminal)
                terminal++;

            var (kind, iconPath) = icons.Resolve(e.Icon);
            switch (kind)
            {
                case IconKind.Png: png++; break;
                case IconKind.SvgOnly: svg++; break;
                default: missing++; break;
            }

            apps.Add(new AppInfo
            {
                Id = e.Id,
                Name = e.Name,
                GenericName = e.GenericName,
                Comment = e.Comment,
                Keywords = e.Keywords,
                Categories = e.Categories,
                Exec = e.Exec,
                Command = command,
                Terminal = e.Terminal,
                Icon = e.Icon,
                IconKind = kind,
                IconPath = iconPath,
                SourcePath = e.SourcePath,
            });
        }

        apps.Sort((a, b) =>
        {
            var c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });

        var stats = new CatalogStats
        {
            FilesRead = filesRead,
            Overridden = overridden,
            NotApplications = notApp,
            NoDisplay = noDisplay,
            HiddenKey = hidden,
            ShowInFiltered = showIn,
            TryExecMissing = tryExec,
            BadExec = badExec,
            Terminal = terminal,
            IconsPng = png,
            IconsSvgOnly = svg,
            IconsMissing = missing,
        };
        return new AppCatalog(apps, stats, (IReadOnlyList<string>?)errors ?? [], DateTimeOffset.Now);
    }

    /// <summary>
    /// OnlyShowIn/NotShowIn for XivDesktop. Apps launched from here run in ghostty-agent's headless Wayland
    /// compositor, which is no desktop environment at all. The rule:
    /// <list type="bullet">
    /// <item>NotShowIn never hides an entry unless it names "XivDesktop" (we are not GNOME or KDE).</item>
    /// <item>OnlyShowIn naming "XivDesktop" shows the entry.</item>
    /// <item>OnlyShowIn naming a single desktop family (GNOME with Unity/GNOME-Classic/GNOME-Flashback, or KDE, or one
    /// other name) hides it: those are shell-specific tools (GNOME Settings, Help, Extension Manager) that do not
    /// work outside that desktop.</item>
    /// <item>OnlyShowIn naming several families ("GNOME;Unity;XFCE;KDE;") shows it: that is an ordinary app whose
    /// author listed the desktops they tested, not a desktop component.</item>
    /// </list>
    /// </summary>
    public static bool ShownIn(DesktopEntry e, string desktop)
    {
        if (e.NotShowIn.Contains(desktop, StringComparer.OrdinalIgnoreCase))
            return false;
        if (e.OnlyShowIn.Count == 0 || e.OnlyShowIn.Contains(desktop, StringComparer.OrdinalIgnoreCase))
            return true;
        var families = e.OnlyShowIn.Select(DesktopFamily).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return families > 1;
    }

    private static string DesktopFamily(string name) => name.ToUpperInvariant() switch
    {
        "GNOME" or "UNITY" or "GNOME-CLASSIC" or "GNOME-FLASHBACK" => "GNOME",
        "KDE" or "PLASMA" => "KDE",
        var other => other,
    };

    /// <summary>Desktop file id: the path below the applications directory with '/' (or '\') replaced by '-'.</summary>
    public static string DesktopFileId(string appDir, string file)
    {
        var rel = Path.GetRelativePath(appDir, file);
        return rel.Replace('\\', '-').Replace('/', '-');
    }

    private static bool ExecutableExists(string tryExec, HostPaths paths)
    {
        try
        {
            if (tryExec.StartsWith('/'))
                return File.Exists(paths.ToLocal(tryExec));
            foreach (var dir in paths.ExecSearchDirs)
            {
                if (File.Exists(paths.ToLocal(dir + "/" + tryExec)))
                    return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true; // cannot tell: do not hide the entry
        }

        return false;
    }
}
