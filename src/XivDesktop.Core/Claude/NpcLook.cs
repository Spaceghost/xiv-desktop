using System.Text;
using System.Text.Json;

namespace XivDesktop.Core.Claude;

/// <summary>The playable races, in the order the game's customise data numbers them.</summary>
public enum NpcRace : byte
{
    Hyur = 1,
    Elezen = 2,
    Lalafell = 3,
    Miqote = 4,
    Roegadyn = 5,
    AuRa = 6,
    Hrothgar = 7,
    Viera = 8,
}

/// <summary>A look preset: what the NPC is dressed as.</summary>
public enum NpcStyle
{
    /// <summary>Robes and a book: the default.</summary>
    Scholar,

    /// <summary>Goggles and a toolbelt.</summary>
    Artificer,

    /// <summary>Coat and spectacles.</summary>
    Archivist,

    /// <summary>Light armour; for a session that mostly runs commands.</summary>
    Adventurer,
}

/// <summary>
/// One Claude NPC's appearance: the 26 bytes of customise data the game reads off a character, plus the
/// equipment model ids for the five visible slots and the weapon. Built deterministically from a seed, so
/// the same session always summons the same person, and rerollable with a different seed.
/// </summary>
/// <remarks>
/// The values are chosen from curated tables rather than at random over the whole range: every race here
/// gets a tribe, a face and a hair that exist for it, and the palette indices stay inside the ranges the
/// character creator uses. Nothing in this file talks to the game — the plugin hands the bytes to
/// whichever spawner is available.
/// </remarks>
public sealed record NpcLook
{
    public const int CustomizeLength = 26;

    public required string Name { get; init; }

    public required NpcRace Race { get; init; }

    /// <summary>1 or 2: the two tribes of the race.</summary>
    public required byte Tribe { get; init; }

    /// <summary>0 masculine, 1 feminine.</summary>
    public required byte Gender { get; init; }

    public required NpcStyle Style { get; init; }

    /// <summary>The seed this look was rolled from; <c>/claude look</c> shows it so it can be repeated.</summary>
    public required uint Seed { get; init; }

    /// <summary>The 26 customise bytes, in the game's own order.</summary>
    public required byte[] Customize { get; init; }

    /// <summary>Head, body, hands, legs, feet model ids (variant 1 on each).</summary>
    public required IReadOnlyList<ushort> Equipment { get; init; }

    public required ushort Weapon { get; init; }

    /// <summary>What the nameplate says: "Claude" over the session's own name.</summary>
    public string Nameplate => $"Claude — {Name}";

    /// <summary>A one-line description for the panel and <c>/claude list</c>.</summary>
    public string Describe() => $"{Gender switch { 0 => "masculine", _ => "feminine" }} {Race} {Style.ToString().ToLowerInvariant()} (seed {Seed})";
}

/// <summary>
/// Rolls a Claude NPC's look and name. Deterministic: the same key and seed always give the same person,
/// which is what makes a session recognisable across a reload.
/// </summary>
public static class NpcLooks
{
    /// <summary>Tribe pairs and the face range each race actually has, so no roll lands on a missing model.</summary>
    private static readonly Dictionary<NpcRace, (byte Faces, byte Hairs)> Ranges = new()
    {
        [NpcRace.Hyur] = (6, 12),
        [NpcRace.Elezen] = (6, 12),
        [NpcRace.Lalafell] = (4, 10),
        [NpcRace.Miqote] = (4, 12),
        [NpcRace.Roegadyn] = (6, 10),
        [NpcRace.AuRa] = (4, 12),
        [NpcRace.Hrothgar] = (4, 8),
        [NpcRace.Viera] = (4, 10),
    };

    /// <summary>Head, body, hands, legs, feet for each style. Common, tasteful, low-level sets.</summary>
    private static readonly Dictionary<NpcStyle, (ushort[] Gear, ushort Weapon)> Kits = new()
    {
        [NpcStyle.Scholar] = ([0, 6041, 6042, 6043, 6044], 1601),
        [NpcStyle.Artificer] = ([6091, 6092, 6093, 6094, 6095], 1301),
        [NpcStyle.Archivist] = ([6101, 6102, 6103, 6104, 6105], 1601),
        [NpcStyle.Adventurer] = ([0, 6011, 6012, 6013, 6014], 201),
    };

    /// <summary>Names a session gets when the player did not name it and the model did not either.</summary>
    private static readonly string[] Names =
    [
        "Quill", "Ledger", "Index", "Margin", "Folio", "Codex", "Lantern", "Compass",
        "Ember", "Tally", "Cipher", "Almanac", "Sextant", "Beacon", "Vellum", "Errata",
    ];

    /// <summary>
    /// The look for <paramref name="key"/> (the session id or name). <paramref name="seed"/> rerolls it;
    /// 0 means "derive the seed from the key", which is what makes it stable.
    /// </summary>
    public static NpcLook For(string key, uint seed = 0, NpcStyle? style = null, string? name = null)
    {
        var s = seed != 0 ? seed : Hash(key);
        var rng = new Roll(s);
        var race = (NpcRace)(byte)(1 + rng.Next(8));
        var (faces, hairs) = Ranges[race];
        var tribe = (byte)(1 + rng.Next(2));
        var gender = (byte)rng.Next(2);
        var chosen = style ?? (NpcStyle)rng.Next(4);

        var c = new byte[NpcLook.CustomizeLength];
        c[0] = (byte)race;
        c[1] = gender;
        c[2] = 1;                                   // model type (adult)
        c[3] = (byte)(40 + rng.Next(30));           // height
        c[4] = (byte)((byte)race * 2 - 2 + tribe);  // tribe index, as the game numbers it
        c[5] = (byte)(1 + rng.Next(faces));         // face
        c[6] = (byte)(1 + rng.Next(hairs));         // hair
        c[7] = (byte)rng.Next(2);                   // highlights on/off
        c[8] = (byte)(1 + rng.Next(12));            // skin colour
        c[9] = (byte)(1 + rng.Next(32));            // right eye colour
        c[10] = (byte)(1 + rng.Next(48));           // hair colour
        c[11] = (byte)(1 + rng.Next(48));           // highlights colour
        c[12] = (byte)rng.Next(4);                  // facial features
        c[13] = (byte)(1 + rng.Next(8));            // facial feature colour
        c[14] = (byte)(1 + rng.Next(8));            // eyebrows
        c[15] = (byte)(1 + rng.Next(32));           // left eye colour
        c[16] = (byte)(1 + rng.Next(6));            // eye shape
        c[17] = (byte)(1 + rng.Next(6));            // nose
        c[18] = (byte)(1 + rng.Next(6));            // jaw
        c[19] = (byte)(1 + rng.Next(6));            // mouth
        c[20] = (byte)(1 + rng.Next(6));            // lip colour / pattern
        c[21] = (byte)(30 + rng.Next(40));          // muscle or bust
        c[22] = (byte)(30 + rng.Next(40));          // bust size
        c[23] = (byte)(1 + rng.Next(4));            // face paint
        c[24] = (byte)(1 + rng.Next(8));            // face paint colour
        c[25] = 0;                                  // reserved

        var (gear, weapon) = Kits[chosen];
        return new NpcLook
        {
            Name = Clean(name) is { Length: > 0 } given ? given : Names[(int)(s % (uint)Names.Length)],
            Race = race,
            Tribe = tribe,
            Gender = gender,
            Style = chosen,
            Seed = s,
            Customize = c,
            Equipment = gear,
            Weapon = weapon,
        };
    }

    /// <summary>
    /// The prompt sent once per new session, asking the model to choose its own name and look. Kept to one
    /// short JSON answer so it costs almost nothing and is easy to parse.
    /// </summary>
    public const string SelfPrompt =
        "Before we start: you are about to be given a body in Final Fantasy XIV, standing next to me. " +
        "Reply with ONLY one line of JSON and nothing else: " +
        "{\"name\":\"<one word you'd like to be called>\",\"race\":\"<Hyur|Elezen|Lalafell|Miqote|Roegadyn|AuRa|Hrothgar|Viera>\"," +
        "\"gender\":\"<masculine|feminine>\",\"style\":\"<scholar|artificer|archivist|adventurer>\"}";

    /// <summary>
    /// The model's answer to <see cref="SelfPrompt"/>, applied on top of the deterministic look. Anything
    /// missing or unparseable falls back to the roll, so a chatty answer never breaks the summon.
    /// </summary>
    public static NpcLook ApplySelfDescription(NpcLook fallback, string? answer)
    {
        var json = ExtractJson(answer);
        if (json == null)
            return fallback;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return fallback;
            var name = Clean(Str(root, "name"));
            var style = Str(root, "style").ToLowerInvariant() switch
            {
                "scholar" => NpcStyle.Scholar,
                "artificer" or "mechanic" or "engineer" => NpcStyle.Artificer,
                "archivist" or "librarian" => NpcStyle.Archivist,
                "adventurer" or "warrior" => NpcStyle.Adventurer,
                _ => (NpcStyle?)null,
            };
            var look = For(fallback.Name, fallback.Seed, style ?? fallback.Style, name.Length > 0 ? name : fallback.Name);

            if (Enum.TryParse<NpcRace>(Str(root, "race").Replace("'", "").Replace(" ", ""), ignoreCase: true, out var race))
            {
                var c = (byte[])look.Customize.Clone();
                c[0] = (byte)race;
                var (faces, hairs) = Ranges[race];
                c[4] = (byte)((byte)race * 2 - 2 + look.Tribe);
                c[5] = (byte)(1 + c[5] % faces);
                c[6] = (byte)(1 + c[6] % hairs);
                look = look with { Race = race, Customize = c };
            }

            var gender = Str(root, "gender").ToLowerInvariant();
            if (gender is "masculine" or "male" or "feminine" or "female")
            {
                var c = (byte[])look.Customize.Clone();
                c[1] = gender is "masculine" or "male" ? (byte)0 : (byte)1;
                look = look with { Gender = c[1], Customize = c };
            }

            return look;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>The first JSON object in <paramref name="text"/>, so a fenced or prefixed answer still parses.</summary>
    public static string? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    /// <summary>A stable hash of the key: FNV-1a, so it does not change between runs like string.GetHashCode.</summary>
    public static uint Hash(string key)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(key ?? ""))
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash == 0 ? 1 : hash;
    }

    private static string Clean(string? name)
    {
        var trimmed = (name ?? "").Trim();
        var kept = new StringBuilder();
        foreach (var c in trimmed)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '\'' or ' ')
                kept.Append(c);
            if (kept.Length == 20)
                break;
        }

        return kept.ToString().Trim();
    }

    private static string Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>A tiny, stable PRNG (xorshift32): the same seed gives the same sequence on every machine.</summary>
    private struct Roll(uint seed)
    {
        private uint state = seed == 0 ? 1 : seed;

        public int Next(int bound)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return bound <= 0 ? 0 : (int)(state % (uint)bound);
        }
    }
}
