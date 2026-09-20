using XivDesktop.Core.Palette;

namespace XivDesktop.Core.Arcade;

public enum ArcadeVerb
{
    Open,
    Play,
    Last,
    List,
    Sync,
    Setup,
    Rescan,
    LaunchBox,
    ImportSaves,
    DryRun,
    Help,
}

/// <summary>What the player typed after /arcade.</summary>
public sealed record ArcadeRequest(ArcadeVerb Verb, string Arg = "")
{
    public static ArcadeRequest Parse(string? arguments)
    {
        var args = (arguments ?? "").Trim();
        if (args.Length == 0)
            return new ArcadeRequest(ArcadeVerb.Open);
        var space = args.IndexOf(' ');
        var verb = (space < 0 ? args : args[..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : args[(space + 1)..].Trim();
        switch (verb)
        {
            case "last" or "resume" when rest.Length == 0:
                return new ArcadeRequest(ArcadeVerb.Last);
            case "list" or "ls" when rest.Length == 0:
                return new ArcadeRequest(ArcadeVerb.List);
            case "sync" when rest.Length == 0:
                return new ArcadeRequest(ArcadeVerb.Sync);
            case "setup" when rest.Length == 0:
                return new ArcadeRequest(ArcadeVerb.Setup);
            case "rescan" or "scan" or "reload" when rest.Length == 0:
                return new ArcadeRequest(ArcadeVerb.Rescan);
            case "help" or "?" when rest.Length == 0:
                return new ArcadeRequest(ArcadeVerb.Help);
            case "launchbox":
                return new ArcadeRequest(ArcadeVerb.LaunchBox, rest);
            case "import-saves" or "importsaves":
                return new ArcadeRequest(ArcadeVerb.ImportSaves, rest);
            case "launch" or "play" or "run" when rest.Length > 0:
                if (rest.StartsWith("--dry-run", StringComparison.Ordinal))
                    return new ArcadeRequest(ArcadeVerb.DryRun, rest["--dry-run".Length..].Trim());
                return new ArcadeRequest(ArcadeVerb.Play, rest);
            default:
                return new ArcadeRequest(ArcadeVerb.Play, args);
        }
    }
}

/// <summary>
/// The shell lines that run tools/xiv-arcade on the Linux host, and the name matching that picks a game.
/// Pure: the plugin hands the line to its launch backend, which starts it with sh -c inside the agent's
/// compositor, so the emulator the helper starts shows up as a game panel.
/// </summary>
public static class ArcadeCommands
{
    /// <summary>POSIX single-quoting: safe for any path or title, including ones with quotes in them.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>"python3 '/path/xiv-arcade' verb 'arg' …". A line break in an argument is refused (null).</summary>
    public static string? Line(string helperPath, string verb, params string[] args)
    {
        if (helperPath.Length == 0 || helperPath.Contains('\n') || args.Any(a => a.Contains('\n') || a.Contains('\r')))
            return null;
        var parts = new List<string> { "python3", Quote(helperPath), verb };
        parts.AddRange(args.Select(Quote));
        return string.Join(' ', parts);
    }

    /// <summary>The same command as an argument vector, for a runner that does not go through a shell.</summary>
    public static IReadOnlyList<string> Argv(string helperPath, string verb, params string[] args) => ["python3", helperPath, verb, .. args];

    /// <summary>The verb and arguments the helper takes for a request, or null when it is not one the helper runs.</summary>
    public static (string Verb, string[] Args)? HelperCall(ArcadeRequest request, string? gameId = null) => request.Verb switch
    {
        ArcadeVerb.Play when gameId != null => ("launch", [gameId]),
        ArcadeVerb.Last => ("last", []),
        ArcadeVerb.Sync => ("sync", []),
        ArcadeVerb.Setup => ("setup", []),
        ArcadeVerb.Rescan => ("scan", []),
        ArcadeVerb.LaunchBox when request.Arg.Length > 0 => ("setup", ["--launchbox", request.Arg]),
        ArcadeVerb.ImportSaves when request.Arg.Length > 0 => ("import-saves", [request.Arg]),
        ArcadeVerb.DryRun when request.Arg.Length > 0 => ("launch", ["--dry-run", request.Arg]),
        _ => null,
    };

    /// <summary>The helper line for a request, or null when the request is not one the helper runs.</summary>
    public static string? For(string helperPath, ArcadeRequest request, string? gameId = null)
        => HelperCall(request, gameId) is { } call ? Line(helperPath, call.Verb, call.Args) : null;

    /// <summary>Roman numerals to digits and "ff7" to "final fantasy 7", so "ff7", "FF VII" and "final fantasy 7" agree.</summary>
    public static string Normalize(string text)
    {
        var words = new List<string>();
        foreach (var raw in new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = raw;
            if (w.Length > 2 && w.StartsWith("ff", StringComparison.Ordinal))
            {
                var tail = w[2..];
                var split = tail.TakeWhile(c => !char.IsDigit(c)).Count();
                if (Roman(tail[..split]) is { } n)
                {
                    words.AddRange(["final", "fantasy", n]);
                    if (split < tail.Length)
                        words.Add(tail[split..]);
                    continue;
                }

                if (tail.All(char.IsDigit))
                {
                    words.AddRange(["final", "fantasy", tail]);
                    continue;
                }
            }

            words.Add(w == "ff" ? "final fantasy" : Roman(w) ?? w);
        }

        return string.Join(' ', words);
    }

    private static string? Roman(string w) => w switch
    {
        "i" => "1",
        "ii" => "2",
        "iii" => "3",
        "iv" => "4",
        "v" => "5",
        "vi" => "6",
        "vii" => "7",
        "viii" => "8",
        "ix" => "9",
        "x" => "10",
        "xi" => "11",
        "xii" => "12",
        _ => null,
    };

    /// <summary>
    /// A score for <paramref name="query"/> against a title, or null. An exact normalized match beats a prefix,
    /// which beats a contained phrase, which beats the palette's subsequence match; shorter titles win ties,
    /// so "ff7" is Final Fantasy VII and not VIII.
    /// </summary>
    public static double? Score(string title, string query)
    {
        var t = Normalize(title);
        var q = Normalize(query);
        if (q.Length == 0)
            return 0;
        if (t == q)
            return 1000;
        if (t.StartsWith(q + " ", StringComparison.Ordinal))
            return 800 - t.Length;
        var at = (" " + t + " ").IndexOf(" " + q + " ", StringComparison.Ordinal);
        if (at >= 0)
            return 700 - at - (t.Length * 0.5);
        if (t.Contains(q, StringComparison.Ordinal))
            return 600 - (t.Length * 0.5);
        return Fuzzy.Match(t, q) is { } m ? Math.Min(500, m.Score) - (t.Length * 0.25) : null;
    }

    /// <summary>The best match, preferring games whose core is installed when scores tie.</summary>
    public static ArcadeGame? Pick(IReadOnlyList<ArcadeGame> games, string query)
    {
        ArcadeGame? best = null;
        double bestScore = double.MinValue;
        foreach (var g in games)
        {
            if (Score(g.Title, query) is not { } s)
                continue;
            s += (g.Ff.Count > 0 ? 5 : 0) + (g.Ready ? 1 : 0);
            if (s > bestScore)
                (best, bestScore) = (g, s);
        }

        return best;
    }
}
