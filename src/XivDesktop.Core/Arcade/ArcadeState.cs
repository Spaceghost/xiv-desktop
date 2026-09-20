using System.Text.Json;

namespace XivDesktop.Core.Arcade;

/// <summary>One first-run step: what it is, whether it is true yet, and the one command that makes it true.</summary>
public sealed record ArcadeCheck(string Key, string Title, bool Ok, string Detail, string Command);

/// <summary>A pairing step for save sync: what to do and the command to copy. Nothing is run for the player.</summary>
public sealed record ArcadeStep(string Text, string Command);

/// <summary>Save sync as the host helper last saw it.</summary>
public sealed record ArcadeSync(string Code, string Label, string Detail, bool CanLaunch, IReadOnlyList<ArcadeStep> Steps, IReadOnlyList<string> Conflicts)
{
    public static readonly ArcadeSync Unknown = new("unknown", "Checking…", "", true, [], []);

    /// <summary>ok: nothing to do; busy: data is moving; attention: the player should look.</summary>
    public string Tone => Code switch
    {
        "in-sync" or "no-peers" => "ok",
        "pulling" or "pushing" or "unknown" => "busy",
        _ => "attention",
    };
}

/// <summary>A placeholder on the Final Fantasy shelf: text only. It lights up when <see cref="Games"/> is not empty.</summary>
public sealed record ArcadeShelfEntry(string Key, string Title, int Year, string Platforms, IReadOnlyList<string> Games);

public sealed record ArcadeGame(string Id, string Title, int? Year, string System, string Path, int Discs, IReadOnlyList<string> Ff, string? Boxart, double? LastPlayed, bool Ready);

public sealed record ArcadeSystem(string Id, string Name, string Folder, IReadOnlyList<string> Extensions, string? Core, string Hint, string Note);

/// <summary>
/// What tools/xiv-arcade wrote to state.json: the first-run checks, the save sync verdict, the shelf, and the
/// library. The plugin only ever reads this; every decision about games and saves is the helper's.
/// </summary>
public sealed record ArcadeState
{
    public static readonly ArcadeState Empty = new();

    public double At { get; init; }

    public string Legal { get; init; } = ArcadeText.Legal;

    public string Message { get; init; } = "";

    public string? PlayingTitle { get; init; }

    public string GamesRoot { get; init; } = "";

    public string SyncRoot { get; init; } = "";

    public string LaunchBoxText { get; init; } = "";

    public string LaunchBoxCommand { get; init; } = "";

    public IReadOnlyList<string> LaunchBoxFound { get; init; } = [];

    public IReadOnlyList<ArcadeCheck> Checks { get; init; } = [];

    public ArcadeSync Sync { get; init; } = ArcadeSync.Unknown;

    public IReadOnlyList<ArcadeShelfEntry> Shelf { get; init; } = [];

    public IReadOnlyList<ArcadeGame> Games { get; init; } = [];

    public IReadOnlyList<ArcadeSystem> Systems { get; init; } = [];

    public string? Last { get; init; }

    public bool Loaded => At > 0;

    public bool SetupDone => Checks.Count > 0 && Checks.All(c => c.Ok);

    public ArcadeGame? Game(string? id) => id == null ? null : Games.FirstOrDefault(g => g.Id == id);

    public string SystemName(string id) => Systems.FirstOrDefault(s => s.Id == id)?.Name ?? id;

    /// <summary>Games no shelf entry claims, grouped by system in the helper's system order.</summary>
    public IEnumerable<(ArcadeSystem System, List<ArcadeGame> Games)> EverythingElse()
    {
        foreach (var s in Systems)
        {
            var games = Games.Where(g => g.System == s.Id && g.Ff.Count == 0).ToList();
            if (games.Count > 0)
                yield return (s, games);
        }
    }

    /// <summary>Parses state.json; anything missing or malformed reads as empty rather than throwing.</summary>
    public static ArcadeState Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object)
                return Empty;
            var checks = List(r, "checks", c => new ArcadeCheck(Str(c, "key"), Str(c, "title"), Bool(c, "ok"), Str(c, "detail"), Str(c, "command")));
            var lb = r.TryGetProperty("checks", out var cs) && cs.ValueKind == JsonValueKind.Array
                ? cs.EnumerateArray().Select(c => c.TryGetProperty("launchbox", out var l) ? l : default).FirstOrDefault(l => l.ValueKind == JsonValueKind.Object)
                : default;
            var sync = r.TryGetProperty("sync", out var s) && s.ValueKind == JsonValueKind.Object
                ? new ArcadeSync(Str(s, "code"), Str(s, "label"), Str(s, "detail"), !s.TryGetProperty("can_launch", out var cl) || cl.ValueKind != JsonValueKind.False,
                    List(s, "steps", x => new ArcadeStep(Str(x, "text"), Str(x, "command"))),
                    List(s, "conflicts", x => System.IO.Path.GetFileName(Str(x, "kept_as"))))
                : ArcadeSync.Unknown;
            return new ArcadeState
            {
                At = Num(r, "at") ?? 0,
                Legal = Str(r, "legal") is { Length: > 0 } legal ? legal : ArcadeText.Legal,
                Message = Str(r, "message"),
                PlayingTitle = r.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.Object ? Str(p, "title") : null,
                GamesRoot = Str(r, "games_root"),
                SyncRoot = Str(r, "sync_root"),
                LaunchBoxText = lb.ValueKind == JsonValueKind.Object ? Str(lb, "text") : "",
                LaunchBoxCommand = lb.ValueKind == JsonValueKind.Object ? Str(lb, "command") : "",
                LaunchBoxFound = lb.ValueKind == JsonValueKind.Object ? Strings(lb, "found") : [],
                Checks = checks,
                Sync = sync,
                Shelf = List(r, "shelf", e => new ArcadeShelfEntry(Str(e, "key"), Str(e, "title"), (int)(Num(e, "year") ?? 0), Str(e, "platforms"), Strings(e, "games"))),
                Games = List(r, "games", g => new ArcadeGame(Str(g, "id"), Str(g, "title"), (int?)Num(g, "year"), Str(g, "system"), Str(g, "path"),
                    (int)(Num(g, "discs") ?? 0), Strings(g, "ff"), NullStr(g, "boxart"), Num(g, "last_played"), Bool(g, "ready"))),
                Systems = List(r, "systems", x => new ArcadeSystem(Str(x, "id"), Str(x, "name"), Str(x, "folder"), Strings(x, "exts"), NullStr(x, "core"), Str(x, "hint"), Str(x, "note"))),
                Last = NullStr(r, "last"),
            };
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static string Str(JsonElement e, string name) => NullStr(e, name) ?? "";

    private static string? NullStr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static List<string> Strings(JsonElement e, string name) => List(e, name, x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "");

    private static List<T> List<T>(JsonElement e, string name, Func<JsonElement, T> map)
    {
        var result = new List<T>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
                result.Add(map(item));
        }

        return result;
    }
}

/// <summary>Words the window, the chat output and the docs all use.</summary>
public static class ArcadeText
{
    public const string Legal = "XivDesktop never downloads, links to or helps find ROMs, disc images or BIOS files. It only reads folders you point it at. Use your own dumps of games you own.";

    public const string Help = "/arcade opens the library; /arcade <name> plays the closest match; /arcade last resumes; /arcade list; /arcade sync; /arcade setup; "
        + "/arcade rescan; /arcade launchbox <folder>; /arcade import-saves <folder>; /arcade launch --dry-run <name or file>.";
}
