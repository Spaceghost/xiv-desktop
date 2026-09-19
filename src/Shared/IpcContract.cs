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

    // ghostty-dalamud's gates (provided by ghostty-dalamud, consumed here).
    public const string GhosttyStatus = "GhosttyDalamud.v1.Status";
    public const string GhosttyPost = "GhosttyDalamud.v1.Post";

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
