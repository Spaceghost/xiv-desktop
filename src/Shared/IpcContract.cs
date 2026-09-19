// Contract between XivDesktop.Plugin (provider) and its IPC consumers: the XivDesktop.Umbra widget,
// other Dalamud plugins, and (later) MCP tools. Compiled into Plugin and Umbra via
// <Compile Include="..\Shared\IpcContract.cs" />. Payloads are JSON strings so no side needs the
// other's types. Additive changes only; a breaking change gets a new "v2" gate name.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XivDesktop.Shared;

public static class IpcContract
{
    /// <summary>
    /// Func&lt;string&gt;: JSON array of visible apps, catalog order:
    /// [{ id, name, generic, categories: string[], favourite, keywords: string[], recent: int|null (0 = most recent),
    ///    terminal, launchable }].
    /// The first five fields are the documented v1 surface; the rest are additive.
    /// </summary>
    public const string ListApps = "XivDesktop.v1.ListApps";

    /// <summary>
    /// Func&lt;string, string&gt;: launch by desktop-file id ("org.gnome.TextEditor" or with ".desktop") or by search query
    /// (best match). Returns "ok: launched NAME (ID)" or "error: REASON". The launch itself is posted to ghostty-dalamud
    /// on the framework thread; "ok" means the command was handed over, not that a window appeared.
    /// </summary>
    public const string Launch = "XivDesktop.v1.Launch";

    /// <summary>Func&lt;string&gt;: JSON object, see <see cref="StatusPayload"/>.</summary>
    public const string Status = "XivDesktop.v1.Status";

    /// <summary>Action(string): toggles the /desktop launcher window (argument: optional initial search text).</summary>
    public const string ToggleLauncher = "XivDesktop.v1.ToggleLauncher";

    /// <summary>Func&lt;string, bool&gt;: toggles an app id's favourite flag; returns the new state.</summary>
    public const string ToggleFavourite = "XivDesktop.v1.ToggleFavourite";

    /// <summary>Func&lt;string&gt;: JSON object, see <see cref="WindowsPayload"/>: window panels with their workspaces.</summary>
    public const string Windows = "XivDesktop.v1.Windows";

    /// <summary>
    /// Func&lt;int, string&gt;: 1..9 switches to that workspace; 0 only reports the current one. Returns
    /// "ok: workspace N" or "error: REASON". The switch runs on the framework thread.
    /// </summary>
    public const string Workspace = "XivDesktop.v1.Workspace";

    /// <summary>Func&lt;string, string&gt;: the palette's top results for a query, a JSON array of <see cref="PaletteResultPayload"/>.</summary>
    public const string Palette = "XivDesktop.v1.Palette";

    /// <summary>
    /// Func&lt;string, string&gt;: a window action as JSON, see <see cref="WindowActionRequest"/>
    /// (<c>{"action":"focus|close|pet|pin|place|move","id":12,"pin":"orbit 3","workspace":3}</c>).
    /// Returns "ok: …" or "error: …"; "ok" means ghostty queued it.
    /// </summary>
    public const string WindowAction = "XivDesktop.v1.WindowAction";

    /// <summary>
    /// Func&lt;string, string&gt;: "Ask an NPC". The argument is what follows /ask: a question (summons the speaker if
    /// needed and asks), "" (summon for a conversation), "bye" (dismiss), "as &lt;preset|key|favourite&gt; [question]"
    /// (switch speaker first; keys are "npc:ID", "minion:ID", "mount:ID", "pet:ID", "self"). Returns "ok: …" or
    /// "error: …" at once; the answer streams into the in-game dialogue.
    /// </summary>
    public const string Ask = "XivDesktop.v1.Ask";

    // ghostty-dalamud's gates (provided by ghostty-dalamud, consumed here).
    public const string GhosttyStatus = "GhosttyDalamud.v1.Status";
    public const string GhosttyPost = "GhosttyDalamud.v1.Post";
    public const string GhosttyCall = "GhosttyDalamud.v1.Call";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    /// <summary>Parses a ListApps payload; empty on malformed input (never throws).</summary>
    public static List<AppSummary> ParseApps(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<AppSummary>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Parses a Windows payload; null on malformed input (never throws).</summary>
    public static WindowsPayload? ParseWindows(string? json) => TryParse<WindowsPayload>(json);

    /// <summary>Parses a Palette payload; empty on malformed input (never throws).</summary>
    public static List<PaletteResultPayload> ParsePalette(string? json) => TryParse<List<PaletteResultPayload>>(json) ?? [];

    public static WindowActionRequest? ParseWindowAction(string? json) => TryParse<WindowActionRequest>(json);

    private static T? TryParse<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses a Status payload; null on malformed input (never throws).</summary>
    public static StatusPayload? ParseStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<StatusPayload>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record AppSummary
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string Generic { get; init; } = "";

    public List<string> Categories { get; init; } = [];

    public bool Favourite { get; init; }

    public List<string> Keywords { get; init; } = [];

    public int? Recent { get; init; }

    public bool Terminal { get; init; }

    public bool Launchable { get; init; }
}

public sealed record StatusPayload
{
    /// <summary>ghostty-dalamud's Post gate is registered (launching possible).</summary>
    public bool Ghostty { get; init; }

    /// <summary>ghostty-dalamud's own status text, when its Status gate answered.</summary>
    public string? GhosttyStatus { get; init; }

    public int Apps { get; init; }

    public bool Scanning { get; init; }

    public DateTimeOffset? ScannedAt { get; init; }

    public string? LastLaunch { get; init; }

    public string? LastError { get; init; }

    /// <summary>One-line human summary.</summary>
    public string Summary { get; init; } = "";
}

public sealed record WindowsPayload
{
    /// <summary>ghostty-dalamud's Call gate is registered (window list and actions work).</summary>
    public bool Available { get; init; }

    /// <summary>ghostty's window.list rev (-1 before the first answer).</summary>
    public long Rev { get; init; } = -1;

    /// <summary>Current workspace, 1..9.</summary>
    public int Workspace { get; init; } = 1;

    /// <summary>The panel window actions without an id apply to (focused, else last focused), if any.</summary>
    public long? Target { get; init; }

    public List<WindowPayload> Windows { get; init; } = [];
}

public sealed record WindowPayload
{
    public long Id { get; init; }

    public string Title { get; init; } = "";

    /// <summary>ghostty's "app": the program of a run command, the match text, or "".</summary>
    public string App { get; init; } = "";

    /// <summary>pending | live | ended.</summary>
    public string State { get; init; } = "";

    /// <summary>pet | pin | full | tab.</summary>
    public string Kind { get; init; } = "";

    public bool Focused { get; init; }

    /// <summary>1..9, or 0 while not assigned yet.</summary>
    public int Workspace { get; init; }

    /// <summary>XivDesktop hid it (another workspace is current).</summary>
    public bool Hidden { get; init; }

    /// <summary>Best-guess desktop-file id from the catalog, for an icon; null when unknown.</summary>
    public string? AppId { get; init; }
}

public sealed record PaletteResultPayload
{
    /// <summary>app | window | action | calc | command.</summary>
    public string Provider { get; init; } = "";

    public string Title { get; init; } = "";

    public string Subtitle { get; init; } = "";

    public double Score { get; init; }

    public bool Enabled { get; init; }

    public string? Reason { get; init; }

    /// <summary>What running it would do: { kind, arg, id, number } (see PaletteCommand in Core).</summary>
    public PaletteCommandPayload Command { get; init; } = new();

    public string? AppId { get; init; }

    public long? WindowId { get; init; }
}

public sealed record PaletteCommandPayload
{
    public string Kind { get; init; } = "";

    public string Arg { get; init; } = "";

    public long Id { get; init; }

    public int Number { get; init; }
}

public sealed record WindowActionRequest
{
    /// <summary>focus | close | pet | pin (pin here) | toggle (pet ↔ pin where it is) | place (with Pin) | move (with Workspace).</summary>
    public string Action { get; init; } = "";

    /// <summary>Panel id; 0 or missing means the target panel (focused, else last focused).</summary>
    public long Id { get; init; }

    /// <summary>For "place": /term pin arguments ("here", "orbit 3.5", "pet" …).</summary>
    public string? Pin { get; init; }

    /// <summary>For "move": 1..9.</summary>
    public int Workspace { get; init; }
}
