using XivDesktop.Core.Palette;

namespace XivDesktop.Core.Ask;

/// <summary>Where a speaker comes from; each is a tab in the picker.</summary>
public enum SpeakerSource
{
    Npc,
    Minion,
    Mount,
    Pet,
    Self,
}

/// <summary>
/// One pickable speaker. <see cref="Key"/> ("npc:1040124", "minion:3", "mount:1", "pet:6", "self") is what
/// settings, favourites and recents store. For NPCs <see cref="Id"/> is the ENpcBase/ENpcResident row, for
/// minions the Companion row, for mounts the Mount row, for pets the Pet row.
/// </summary>
public sealed record SpeakerEntry(SpeakerSource Source, uint Id, string Name, string Title = "", string Location = "", int ModelCharaId = 0)
{
    public string Key => Source == SpeakerSource.Self ? "self" : $"{SourceKey(Source)}:{Id}";

    public string SearchText => Title.Length == 0 ? Name : $"{Name} {Title}";

    public SpeakerKind Kind => Source switch
    {
        SpeakerSource.Minion => SpeakerKind.Minion,
        SpeakerSource.Mount => SpeakerKind.Mount,
        SpeakerSource.Pet => SpeakerKind.Pet,
        SpeakerSource.Self => SpeakerKind.Self,
        _ => SpeakerKind.Npc,
    };

    /// <summary>Mounts and minions do not talk: they react with their own idle motions instead of emotes.</summary>
    public bool Talks => Source is SpeakerSource.Npc or SpeakerSource.Self or SpeakerSource.Pet;

    public static string SourceKey(SpeakerSource s) => s switch
    {
        SpeakerSource.Npc => "npc",
        SpeakerSource.Minion => "minion",
        SpeakerSource.Mount => "mount",
        SpeakerSource.Pet => "pet",
        _ => "self",
    };

    /// <summary>"npc:1040124" → (Npc, 1040124); "self" → (Self, 0). Null for anything else.</summary>
    public static (SpeakerSource Source, uint Id)? ParseKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        key = key.Trim().ToLowerInvariant();
        if (key == "self" || key == "me" || key == "myself")
            return (SpeakerSource.Self, 0);
        var colon = key.IndexOf(':');
        if (colon <= 0 || !uint.TryParse(key.AsSpan(colon + 1), out var id))
            return null;
        SpeakerSource? source = key[..colon] switch
        {
            "npc" => SpeakerSource.Npc,
            "minion" => SpeakerSource.Minion,
            "mount" => SpeakerSource.Mount,
            "pet" => SpeakerSource.Pet,
            _ => null,
        };
        return source is { } s ? (s, id) : null;
    }
}

/// <summary>
/// Built-in presets: ENpcResident/ENpcBase rows checked with xiv-mcp's search_sheet and get_sheet_row
/// (2026-09-19). Each has a humanoid or moogle body from its ENpcBase row.
/// </summary>
public static class SpeakerPresets
{
    public static readonly IReadOnlyList<(string Preset, string Key, string Name)> All =
    [
        // "Studium researcher" (ENpcResident 1041316): a Sharlayan scholar.
        ("scholar", "npc:1041316", "Studium researcher"),
        // "delivery moogle" (ENpcResident 1000063).
        ("moogle", "npc:1000063", "delivery moogle"),
        // "approachable archivist" (ENpcResident 1040124).
        ("archivist", "npc:1040124", "approachable archivist"),
        ("myself", "self", "You"),
    ];

    public const string DefaultKey = "npc:1041316";

    /// <summary>A preset name ("scholar") → its key; a key passes through; null when neither.</summary>
    public static string? Resolve(string? nameOrKey)
    {
        if (string.IsNullOrWhiteSpace(nameOrKey))
            return null;
        var t = nameOrKey.Trim();
        foreach (var p in All)
        {
            if (string.Equals(p.Preset, t, StringComparison.OrdinalIgnoreCase))
                return p.Key;
        }

        return SpeakerEntry.ParseKey(t) is not null ? t.ToLowerInvariant() : null;
    }
}

/// <summary>
/// The speaker catalog the picker searches: one immutable snapshot built off the framework thread. Many
/// NPC rows share a name ("Alphinaud" has hundreds of ENpc rows); <see cref="Search"/> shows each
/// (name, title) once, preferring a row with a known location.
/// </summary>
public sealed class SpeakerCatalog
{
    public static readonly SpeakerCatalog Empty = new([]);

    private readonly Dictionary<string, SpeakerEntry> byKey;

    public SpeakerCatalog(IReadOnlyList<SpeakerEntry> entries)
    {
        Entries = entries;
        byKey = new Dictionary<string, SpeakerEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
            byKey.TryAdd(e.Key, e);
    }

    public IReadOnlyList<SpeakerEntry> Entries { get; }

    public SpeakerEntry? Find(string? key) => key is not null && byKey.TryGetValue(key, out var e) ? e : null;

    public int Count(SpeakerSource source) => Entries.Count(e => e.Source == source);

    /// <summary>
    /// Ranked matches. <paramref name="source"/> null searches every tab. An empty query lists favourites
    /// first, then recents, then the source's entries in catalog order.
    /// </summary>
    public List<SpeakerEntry> Search(string query, SpeakerSource? source, IReadOnlyList<string> favourites, IReadOnlyList<string> recents, int limit = 60)
    {
        query = (query ?? "").Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<SpeakerEntry>();

        bool Add(SpeakerEntry e)
        {
            if (source is { } s && e.Source != s)
                return false;
            var dedupe = $"{e.Source}|{e.Name}|{e.Title}";
            if (!seen.Add(dedupe))
                return false;
            results.Add(e);
            return results.Count >= limit;
        }

        if (query.Length == 0)
        {
            foreach (var k in favourites.Concat(recents))
            {
                if (Find(k) is { } e && Add(e))
                    return results;
            }

            foreach (var e in Entries)
            {
                if (Add(e))
                    break;
            }

            return results;
        }

        var fav = new HashSet<string>(favourites, StringComparer.OrdinalIgnoreCase);
        var scored = new List<(SpeakerEntry E, double Score)>();
        foreach (var e in Entries)
        {
            if (source is { } s && e.Source != s)
                continue;
            if (Fuzzy.Match(e.SearchText, query) is not { } m)
                continue;
            var score = m.Score + (fav.Contains(e.Key) ? 0.5 : 0) + (e.Location.Length > 0 ? 0.05 : 0);
            if (string.Equals(e.Name, query, StringComparison.OrdinalIgnoreCase))
                score += 1;
            scored.Add((e, score));
        }

        foreach (var (e, _) in scored.OrderByDescending(x => x.Score).ThenBy(x => x.E.Name.Length).ThenBy(x => x.E.Id))
        {
            if (Add(e))
                break;
        }

        return results;
    }
}
