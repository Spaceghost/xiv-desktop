using XivDesktop.Core.Windows;

namespace XivDesktop.Core;

/// <summary>
/// Builds an <see cref="AppCatalog"/> from ghostty-agent's app list (<c>agent.apps</c>). The agent runs on the
/// host, so it sees every .desktop file, while the game under a sandboxed (Flatpak) XIVLauncher sees only a
/// fraction through Z:\. Ids get a ".desktop" suffix so favourites and recents stay compatible with the
/// directory scan; <see cref="AppInfo.AgentId"/> keeps the agent's own id for launching.
/// </summary>
public static class AgentCatalog
{
    /// <param name="apps">The agent's list.</param>
    /// <param name="iconPath">
    /// Maps the agent's <c>icon</c> (an absolute PNG path the agent rendered, or an icon name) to a path this
    /// process can open, or null (drawn as a letter tile).
    /// </param>
    /// <param name="scanned">The directory scan, used for icons, keywords and comments of apps it also found.</param>
    public static AppCatalog Build(IReadOnlyList<AgentApp> apps, Func<string, string?> iconPath, AppCatalog? scanned = null, DateTimeOffset? at = null)
    {
        var list = new List<AppInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int png = 0, missing = 0;
        foreach (var a in apps)
        {
            var id = a.Id.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) ? a.Id : a.Id + ".desktop";
            if (!seen.Add(id))
                continue;
            var known = scanned?.Find(id);
            var icon = a.Icon.Length > 0 ? iconPath(a.Icon) : null;
            icon ??= known?.IconPath;
            if (icon != null)
                png++;
            else
                missing++;
            var agentId = a.Id.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) ? a.Id[..^8] : a.Id;
            list.Add(new AppInfo
            {
                Id = id,
                Name = a.Name,
                GenericName = known?.GenericName ?? "",
                Comment = known?.Comment ?? "",
                Keywords = known?.Keywords ?? [],
                Categories = a.Categories.Count > 0 ? a.Categories : known?.Categories ?? [],
                Icon = a.Icon,
                IconKind = icon != null ? IconKind.Png : IconKind.Missing,
                IconPath = icon,
                AgentId = agentId,

                // Display only (tooltips, "copy command"); never run: agent apps start by id.
                Command = GhosttyWire.AppMatch(agentId),
            });
        }

        list.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase));
        return new AppCatalog(list, new CatalogStats { IconsPng = png, IconsMissing = missing }, [], at ?? DateTimeOffset.Now);
    }

    /// <summary>
    /// The default icon mapping: an absolute path ending in .png goes through <paramref name="toLocal"/>
    /// (Z:\ under Wine); an icon name is looked up with <paramref name="resolver"/> when given.
    /// </summary>
    public static Func<string, string?> IconMapper(Func<string, string> toLocal, IconResolver? resolver)
        => icon =>
        {
            if (icon.StartsWith('/'))
                return icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? toLocal(icon) : null;
            if (resolver == null)
                return null;
            var (kind, path) = resolver.Resolve(icon);
            return kind == IconKind.Png ? path : null;
        };
}
