namespace XivDesktop.Core;

/// <summary>
/// Resolves Icon= values to PNG files without an icon-theme engine: an absolute path, then the hicolor,
/// Adwaita (plus AdwaitaLegacy, where current Adwaita keeps its full-colour PNGs) and breeze themes at
/// 256/128/96/64/48 px (then 512 px as a last resort) under each icon root, then the pixmap directories.
/// Directory listings are cached, so resolving many icons costs one listing per candidate directory.
/// SVG-only icons are reported as <see cref="IconKind.SvgOnly"/> (the UI draws a letter tile).
/// </summary>
public sealed class IconResolver
{
    public static readonly string[] Themes = ["hicolor", "Adwaita", "AdwaitaLegacy", "breeze"];

    /// <summary>Preferred sizes, largest first; 512 last because it is four times the texture memory of 256.</summary>
    public static readonly int[] Sizes = [256, 128, 96, 64, 48, 512];

    private readonly HostPaths paths;
    private readonly Dictionary<string, Dictionary<string, string>?> listings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> subdirs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IconKind, string?)> cache = new(StringComparer.Ordinal);

    public IconResolver(HostPaths paths) => this.paths = paths;

    /// <summary>Resolves one Icon= value. Returned paths are local (mapped) paths.</summary>
    public (IconKind Kind, string? Path) Resolve(string icon)
    {
        icon = icon.Trim();
        if (icon.Length == 0)
            return (IconKind.Missing, null);
        if (cache.TryGetValue(icon, out var hit))
            return hit;
        var result = ResolveCore(icon);
        cache[icon] = result;
        return result;
    }

    private (IconKind, string?) ResolveCore(string icon)
    {
        if (icon.StartsWith('/'))
        {
            var local = paths.ToLocal(icon);
            if (icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return File.Exists(local) ? (IconKind.Png, local) : (IconKind.Missing, null);
            if (File.Exists(local + ".png"))
                return (IconKind.Png, local + ".png");
            return File.Exists(local) || File.Exists(local + ".svg") ? (IconKind.SvgOnly, null) : (IconKind.Missing, null);
        }

        // Names should not carry an extension, but some files do ("foo.png").
        var name = icon;
        foreach (var ext in new[] { ".png", ".svg", ".xpm" })
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^ext.Length];
                break;
            }
        }

        var sawSvg = false;
        foreach (var root in paths.IconRoots)
        {
            foreach (var theme in Themes)
            {
                var themeDir = Join(root, theme);
                foreach (var size in Sizes)
                {
                    // freedesktop layout: <theme>/<size>x<size>/<context>/<name>.png
                    foreach (var context in Subdirs(Join(themeDir, $"{size}x{size}")))
                    {
                        if (Find(Join(themeDir, $"{size}x{size}", context), name, ".png") is { } png)
                            return (IconKind.Png, png);
                        sawSvg |= Find(Join(themeDir, $"{size}x{size}", context), name, ".svg") != null;
                    }

                    // breeze layout: <theme>/<context>/<size>/<name>.(png|svg)
                    foreach (var context in Subdirs(themeDir))
                    {
                        var dir = Join(themeDir, context, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        if (Find(dir, name, ".png") is { } png)
                            return (IconKind.Png, png);
                        sawSvg |= Find(dir, name, ".svg") != null;
                    }
                }

                if (!sawSvg)
                {
                    foreach (var context in Subdirs(Join(themeDir, "scalable")))
                        sawSvg |= Find(Join(themeDir, "scalable", context), name, ".svg") != null;
                }
            }
        }

        foreach (var dir in paths.PixmapDirs)
        {
            if (Find(dir, name, ".png") is { } png)
                return (IconKind.Png, png);
            sawSvg |= Find(dir, name, ".svg") != null || Find(dir, name, ".xpm") != null;
        }

        return sawSvg ? (IconKind.SvgOnly, null) : (IconKind.Missing, null);
    }

    private static string Join(params string[] parts) => string.Join('/', parts);

    /// <summary>Looks up "name+ext" in a Linux directory; returns the local path of the file or null.</summary>
    private string? Find(string linuxDir, string name, string ext)
    {
        var listing = Listing(linuxDir);
        return listing != null && listing.TryGetValue(name + ext, out var local) ? local : null;
    }

    private Dictionary<string, string>? Listing(string linuxDir)
    {
        if (listings.TryGetValue(linuxDir, out var cached))
            return cached;
        Dictionary<string, string>? map = null;
        try
        {
            var local = paths.ToLocal(linuxDir);
            if (Directory.Exists(local))
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var file in Directory.EnumerateFiles(local))
                    map.TryAdd(Path.GetFileName(file), file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            map = null;
        }

        listings[linuxDir] = map;
        return map;
    }

    private List<string> Subdirs(string linuxDir)
    {
        if (subdirs.TryGetValue(linuxDir, out var cached))
            return cached;
        var list = new List<string>();
        try
        {
            var local = paths.ToLocal(linuxDir);
            if (Directory.Exists(local))
            {
                foreach (var dir in Directory.EnumerateDirectories(local))
                    list.Add(Path.GetFileName(dir));
                list.Sort(StringComparer.Ordinal);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            list.Clear();
        }

        subdirs[linuxDir] = list;
        return list;
    }
}
