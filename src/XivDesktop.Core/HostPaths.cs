namespace XivDesktop.Core;

/// <summary>
/// The Linux directories XivDesktop reads, and how this process reaches them. Every path in here is a
/// Linux path; <see cref="ToLocal"/> maps it for file access (identity on the host, "Z:\..." under Wine).
/// </summary>
public sealed class HostPaths
{
    public HostPaths(string linuxHome, Func<string, string> toLocal)
    {
        LinuxHome = linuxHome.TrimEnd('/');
        ToLocal = toLocal;
    }

    /// <summary>The user's Linux home directory (e.g. "/home/name"); may be empty when unknown.</summary>
    public string LinuxHome { get; }

    /// <summary>Maps a Linux path to one this process can open.</summary>
    public Func<string, string> ToLocal { get; }

    /// <summary>Paths on the host itself (tests, the scan tool).</summary>
    public static HostPaths Native(string linuxHome) => new(linuxHome, p => p);

    /// <summary>Paths as seen from inside Wine, where drive Z: is the Linux root.</summary>
    public static HostPaths Wine(string linuxHome) => new(linuxHome, WineMap);

    public static string WineMap(string linuxPath) => "Z:" + linuxPath.Replace('/', '\\');

    /// <summary>
    /// Application directories in increasing priority: an entry in a later directory replaces one with the
    /// same desktop-file id from an earlier one.
    /// </summary>
    public IReadOnlyList<string> ApplicationDirs => WithHome(
    [
        "/usr/share/applications",
        "/usr/local/share/applications",
        "~/.local/share/applications",
        "/var/lib/flatpak/exports/share/applications",
        "~/.local/share/flatpak/exports/share/applications",
    ]);

    /// <summary>Icon theme base directories, searched in order.</summary>
    public IReadOnlyList<string> IconRoots => WithHome(
    [
        "/usr/share/icons",
        "~/.local/share/icons",
        "/var/lib/flatpak/exports/share/icons",
        "~/.local/share/flatpak/exports/share/icons",
    ]);

    public IReadOnlyList<string> PixmapDirs => ["/usr/share/pixmaps"];

    /// <summary>Where a relative TryExec= is looked up (the game's PATH is Wine's, so this list is fixed).</summary>
    public IReadOnlyList<string> ExecSearchDirs => WithHome(
    [
        "/usr/local/bin",
        "/usr/bin",
        "/bin",
        "/usr/local/sbin",
        "/usr/sbin",
        "~/.local/bin",
        "/var/lib/flatpak/exports/bin",
        "~/.local/share/flatpak/exports/bin",
    ]);

    private List<string> WithHome(string[] paths)
    {
        var result = new List<string>(paths.Length);
        foreach (var p in paths)
        {
            if (!p.StartsWith("~/", StringComparison.Ordinal))
                result.Add(p);
            else if (LinuxHome.Length > 0)
                result.Add(LinuxHome + p[1..]);
        }

        return result;
    }

    /// <summary>
    /// Converts a Wine path on drive Z: ("Z:\home\name\x") back to a Linux path ("/home/name/x").
    /// Also accepts the NT form Wine puts in WINEHOMEDIR ("\??\Z:\home\name"). Returns null otherwise.
    /// </summary>
    public static string? LinuxFromWine(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var p = path.StartsWith(@"\??\", StringComparison.Ordinal) ? path[4..] : path;
        if (p.Length < 2 || char.ToUpperInvariant(p[0]) != 'Z' || p[1] != ':')
            return null;
        var rest = p[2..].Replace('\\', '/');
        return rest.Length == 0 ? "/" : rest.StartsWith('/') ? rest : "/" + rest;
    }

    /// <summary>
    /// Best guess at the Linux home directory from the environment Wine gives the game: $HOME when it is a
    /// Linux path, then WINEHOMEDIR, then the part of <paramref name="xlcorePath"/> (e.g. the plugin config
    /// directory, "Z:\home\name\.xlcore\pluginConfigs\XivDesktop") before "/.xlcore". Empty when unknown.
    /// </summary>
    public static string GuessHome(string? homeEnv, string? wineHomeDir, string? xlcorePath)
    {
        if (!string.IsNullOrEmpty(homeEnv) && homeEnv.StartsWith('/'))
            return homeEnv.TrimEnd('/');
        if (LinuxFromWine(wineHomeDir) is { } fromWine)
            return fromWine.TrimEnd('/');
        if (LinuxFromWine(xlcorePath) is { } fromXl)
        {
            var idx = fromXl.IndexOf("/.xlcore", StringComparison.Ordinal);
            if (idx > 0)
                return fromXl[..idx];
        }

        return "";
    }
}
