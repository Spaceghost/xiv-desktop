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
        if (app.AgentId != null)
            return (null, $"{app.Name} is listed by ghostty-agent and needs ghostty-dalamud's window IPC to start.");
        if (app.Terminal)
            return (null, $"{app.Name} is a terminal application (Terminal=true); XivDesktop v0 does not launch those.");
        if (string.IsNullOrWhiteSpace(app.Command))
            return (null, $"{app.Name} has no usable Exec= line.");
        if (app.Command.Contains('\n') || app.Command.Contains('\r'))
            return (null, $"{app.Name}: the command contains a line break.");
        return (app.Command, null);
    }

    /// <summary>
    /// How <paramref name="app"/> starts, when ghostty-dalamud's Call gate is available: an agent app by its id,
    /// a Terminal=true app as a command typed into a new world terminal, anything else as a run command.
    /// </summary>
    public static (LaunchTarget? Target, string? Error) Plan(AppInfo app)
    {
        if (app.AgentId is { } agentId)
            return (new LaunchTarget(LaunchKind.AgentApp, agentId), null);
        if (string.IsNullOrWhiteSpace(app.Command))
            return (null, $"{app.Name} has no usable Exec= line.");
        if (app.Command.Contains('\n') || app.Command.Contains('\r'))
            return (null, $"{app.Name}: the command contains a line break.");
        return (new LaunchTarget(app.Terminal ? LaunchKind.Terminal : LaunchKind.Run, app.Command), null);
    }

    /// <summary>
    /// The /term line that types <paramref name="command"/> into terminal <paramref name="panelId"/> and presses
    /// Enter. ghostty unescapes backslash sequences in send text, so backslashes are doubled.
    /// </summary>
    public static string SendLine(long panelId, string command) => $"send #{panelId} {command.Replace("\\", "\\\\", StringComparison.Ordinal)}";
}

public enum LaunchKind
{
    /// <summary>window.open {"run": command}: the agent starts it with sh -c.</summary>
    Run,

    /// <summary>window.open {"match": "app:ID"}: the agent starts its own listed app.</summary>
    AgentApp,

    /// <summary>terminal.new, then the command typed into it (/term send #ID command).</summary>
    Terminal,
}

/// <summary>What to start: a shell command (Run, Terminal) or an agent app id (AgentApp).</summary>
public sealed record LaunchTarget(LaunchKind Kind, string Value);
