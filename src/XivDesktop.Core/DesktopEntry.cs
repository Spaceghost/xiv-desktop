namespace XivDesktop.Core;

/// <summary>How an entry's Icon= key resolved to something the game can draw.</summary>
public enum IconKind
{
    /// <summary>No Icon= key, or nothing matched.</summary>
    Missing = 0,

    /// <summary>A PNG file was found (<see cref="AppInfo.IconPath"/>).</summary>
    Png = 1,

    /// <summary>Only an SVG (or other non-PNG) was found; the UI draws a letter tile instead.</summary>
    SvgOnly = 2,
}

/// <summary>
/// The [Desktop Entry] group of one .desktop file, as parsed (before visibility rules).
/// Strings are already unescaped; lists are split on ';'.
/// </summary>
public sealed record DesktopEntry
{
    /// <summary>Desktop file id (e.g. "org.gnome.TextEditor.desktop"): path below applications/ with '/' replaced by '-'.</summary>
    public required string Id { get; init; }

    public string Type { get; init; } = "";

    /// <summary>Unlocalized Name, or the best localized one when the file has no plain Name.</summary>
    public string Name { get; init; } = "";

    public string GenericName { get; init; } = "";

    public string Comment { get; init; } = "";

    public IReadOnlyList<string> Keywords { get; init; } = [];

    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Exec= after string unescaping (quoting and field codes still in place).</summary>
    public string Exec { get; init; } = "";

    public string TryExec { get; init; } = "";

    public string Icon { get; init; } = "";

    public bool Terminal { get; init; }

    public bool NoDisplay { get; init; }

    public bool Hidden { get; init; }

    public IReadOnlyList<string> OnlyShowIn { get; init; } = [];

    public IReadOnlyList<string> NotShowIn { get; init; } = [];

    /// <summary>File the entry came from (as the scanning process sees it).</summary>
    public string SourcePath { get; init; } = "";
}

/// <summary>A launchable, visible application in the catalog.</summary>
public sealed record AppInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string GenericName { get; init; } = "";

    public string Comment { get; init; } = "";

    public IReadOnlyList<string> Keywords { get; init; } = [];

    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Exec= as written (unescaped string, before field-code removal).</summary>
    public string Exec { get; init; } = "";

    /// <summary>Shell command line for "window pull run ..." (sh -c on the host): field codes removed, arguments re-quoted.</summary>
    public string Command { get; init; } = "";

    public bool Terminal { get; init; }

    /// <summary>Icon= as written.</summary>
    public string Icon { get; init; } = "";

    public IconKind IconKind { get; init; }

    /// <summary>PNG path as this process opens it (Z:\... under Wine), or null.</summary>
    public string? IconPath { get; init; }

    public string SourcePath { get; init; } = "";
}
