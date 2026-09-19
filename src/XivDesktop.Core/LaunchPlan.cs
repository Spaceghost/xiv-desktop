namespace XivDesktop.Core;

/// <summary>
/// Decides whether an app can be launched and with which shell command. How the command reaches the host is
/// the launch backend's business; <see cref="PostLine"/> is the form ghostty-dalamud's Post gate takes today
/// (a /term command line without the "/term " prefix).
/// </summary>
public static class LaunchPlan
{
    public const string RunPrefix = "window pull run ";

    /// <summary>"window pull run CMD": ghostty-agent starts CMD with sh -c and shows its window as a pet.</summary>
    public static string PostLine(string command) => RunPrefix + command;

    /// <summary>
    /// The shell command for <paramref name="app"/>, or an error. Terminal=true apps are refused in v0: "window pull
    /// run" starts a Wayland client with no terminal attached, so a TUI would run invisibly. (Opening a ghostty tab
    /// with "new" and typing the command with "send" would work in principle, but the two posts are not ordered
    /// against the new tab becoming active, so v0 does not do it.)
    /// </summary>
    public static (string? Command, string? Error) For(AppInfo app)
    {
        if (app.Terminal)
            return (null, $"{app.Name} is a terminal application (Terminal=true); XivDesktop v0 does not launch those.");
        if (string.IsNullOrWhiteSpace(app.Command))
            return (null, $"{app.Name} has no usable Exec= line.");
        if (app.Command.Contains('\n') || app.Command.Contains('\r'))
            return (null, $"{app.Name}: the command contains a line break.");
        return (app.Command, null);
    }
}
