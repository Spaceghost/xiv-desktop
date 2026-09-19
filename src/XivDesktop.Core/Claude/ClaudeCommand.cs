using System.Text.Json.Nodes;

namespace XivDesktop.Core.Claude;

/// <summary>How permission prompts are answered for a session.</summary>
public enum PermissionRoute
{
    /// <summary>
    /// The supported route: <c>--permission-prompts host</c> with <c>--permission-prompt-tool</c> pointing
    /// at XivDesktop's own MCP tool, so every prompt becomes a dialog in game.
    /// </summary>
    PromptTool,

    /// <summary>
    /// The fallback: <c>--permission-mode</c> decides, and <c>--permission-prompts none</c> means anything
    /// that would still have prompted is <b>denied</b>, never silently allowed. The panel says which mode
    /// is in force and what it lets through.
    /// </summary>
    Mode,
}

/// <summary>Everything a job needs to start one Claude Code session.</summary>
public sealed record ClaudeLaunch
{
    /// <summary>The Linux working directory the session runs in; it decides what Claude can touch.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The <c>claude</c> executable; a bare name is found on the agent's PATH.</summary>
    public string Executable { get; init; } = "";

    /// <summary>Model alias or full name; "" leaves the account default.</summary>
    public string Model { get; init; } = "";

    /// <summary>A display name for the session, passed through as <c>--name</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>A session id to start under, so XivDesktop can resume it even if nothing else is stored.</summary>
    public string SessionId { get; init; } = "";

    /// <summary>A previous session id to continue (<c>--resume</c>), or "".</summary>
    public string ResumeSessionId { get; init; } = "";

    /// <summary>Ask for a fresh id while resuming, so the old transcript is left alone.</summary>
    public bool ForkSession { get; init; }

    public PermissionRoute Route { get; init; } = PermissionRoute.PromptTool;

    /// <summary>For <see cref="PermissionRoute.PromptTool"/>: where XivDesktop's MCP server listens.</summary>
    public string McpUrl { get; init; } = "";

    /// <summary>A per-session secret sent as a header, so only this job can reach the MCP server.</summary>
    public string McpAuth { get; init; } = "";

    /// <summary>For <see cref="PermissionRoute.Mode"/>: acceptEdits, auto, manual, plan, dontAsk.</summary>
    public string PermissionMode { get; init; } = "manual";

    /// <summary>Emit partial message chunks, so replies appear as they are written.</summary>
    public bool StreamPartials { get; init; } = true;

    /// <summary>Extra <c>claude</c> arguments from the settings, already split into argv elements.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];
}

/// <summary>
/// Builds the job argv for a Claude session. XivDesktop hardcodes no <c>claude</c> flags of its own: the
/// agent's <c>@claude</c> runner owns the base command line
/// (<c>-p --output-format stream-json --input-format stream-json --include-partial-messages --verbose</c>)
/// and this produces the flat JSON options object it expands — ghostty-dalamud <c>docs/JOBS.md</c>.
/// Pure and total: a prompt or a path is always one argv element, never a shell string.
/// </summary>
public static class ClaudeCommand
{
    /// <summary>The argv[0] the agent recognises as "a Claude session".</summary>
    public const string Runner = "@claude";

    /// <summary>The MCP server name XivDesktop registers; it becomes part of the tool name.</summary>
    public const string McpServerName = "xivdesktop";

    /// <summary>The MCP tool that answers permission prompts.</summary>
    public const string PermissionToolName = "permission_prompt";

    /// <summary>What <c>--permission-prompt-tool</c> is given: <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>.</summary>
    public const string PermissionTool = $"mcp__{McpServerName}__{PermissionToolName}";

    /// <summary>The header carrying <see cref="ClaudeLaunch.McpAuth"/>.</summary>
    public const string AuthHeader = "X-XivDesktop-Auth";

    /// <summary>
    /// <c>--permission-prompt-tool</c> is not listed in <c>claude --help</c>, so the agent's option table
    /// has no key for it; it is still accepted by the CLI and is passed through <c>args</c>.
    /// </summary>
    public const string PermissionToolFlag = "--permission-prompt-tool";

    /// <summary>The job's argv: the runner, then its options as one JSON element.</summary>
    public static List<string> Argv(ClaudeLaunch launch) => [Runner, Options(launch).ToJsonString()];

    /// <summary>The flat options object the <c>@claude</c> runner expands into a command line.</summary>
    public static JsonObject Options(ClaudeLaunch launch)
    {
        var options = new JsonObject();
        if (launch.Executable.Trim().Length > 0)
            options["claude"] = launch.Executable.Trim();
        if (launch.Model.Trim().Length > 0)
            options["model"] = launch.Model.Trim();
        if (launch.WorkingDirectory.Trim().Length > 0)
            options["cwd"] = launch.WorkingDirectory.Trim();
        if (launch.SessionId.Trim().Length > 0 && launch.ResumeSessionId.Trim().Length == 0)
            options["session_id"] = launch.SessionId.Trim();
        if (launch.ResumeSessionId.Trim().Length > 0)
            options["resume"] = launch.ResumeSessionId.Trim();
        if (!launch.StreamPartials)
            options["partial"] = false;

        var args = new JsonArray();
        if (launch.Name.Trim().Length > 0)
        {
            args.Add("--name");
            args.Add(launch.Name.Trim());
        }

        if (launch.ResumeSessionId.Trim().Length > 0 && launch.ForkSession)
            args.Add("--fork-session");

        if (launch.Route == PermissionRoute.PromptTool && launch.McpUrl.Trim().Length > 0)
        {
            options["permission_prompts"] = "host";
            options["mcp_config"] = new JsonArray(McpConfig(launch.McpUrl.Trim(), launch.McpAuth));
            args.Add(PermissionToolFlag);
            args.Add(PermissionTool);
        }
        else
        {
            // Nothing is auto-approved: the mode decides, and whatever would still have prompted is denied.
            options["permission_mode"] = launch.PermissionMode.Trim().Length > 0 ? launch.PermissionMode.Trim() : "manual";
            options["permission_prompts"] = "none";
        }

        foreach (var extra in launch.ExtraArgs)
        {
            if (extra.Trim().Length > 0)
                args.Add(extra);
        }

        if (args.Count > 0)
            options["args"] = args;
        return options;
    }

    /// <summary>
    /// The inline <c>--mcp-config</c> value: one HTTP server, XivDesktop's own, reachable on the loopback
    /// address the plugin listens on (Wine shares the host's network stack, so the game's listener is the
    /// host's 127.0.0.1).
    /// </summary>
    public static string McpConfig(string url, string auth)
    {
        var server = new JsonObject
        {
            ["type"] = "http",
            ["url"] = url,
        };
        if (auth.Length > 0)
            server["headers"] = new JsonObject { [AuthHeader] = auth };
        return new JsonObject
        {
            ["mcpServers"] = new JsonObject { [McpServerName] = server },
        }.ToJsonString();
    }

    /// <summary>
    /// One <c>--input-format stream-json</c> user turn: the line written to the job's stdin.
    /// </summary>
    public static string UserMessage(string text)
    {
        var o = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            },
        };
        return o.ToJsonString() + "\n";
    }
}
