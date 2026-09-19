namespace XivDesktop.Core.Ask;

/// <summary>A whitelisted conversational emote: its Emote row and the ActionTimeline row it plays.</summary>
public sealed record EmoteInfo(string Name, uint EmoteId, ushort TimelineId, string TimelineKey);

/// <summary>
/// The only emotes a cue can play, all harmless conversational gestures. Ids are the game's Emote sheet
/// rows and the first ActionTimeline of each (<c>Emote.ActionTimeline[0]</c>), read with xiv-mcp's
/// get_sheet_row on 2026-09-19 (game data of that date). They are played as local timelines only; the
/// server-side /emote command is never used. Aliases map what a model tends to write onto these rows.
/// </summary>
public static class EmoteTable
{
    public static readonly IReadOnlyList<EmoteInfo> All =
    [
        new("wave", 16, 706, "emote/goodbye_st"),
        new("goodbye", 15, 705, "emote/goodbye"),
        new("bow", 5, 694, "emote/bow"),
        new("laugh", 21, 712, "emote/laugh_st"),
        new("comfort", 9, 698, "emote/comfort"),
        new("think", 39, 736, "emote/think"),
        new("surprised", 1, 689, "emote/amazed"),
        new("yes", 42, 739, "emote/yes"),
        new("no", 24, 716, "emote/no"),
        new("cheer", 6, 695, "emote/cheer"),
        new("clap", 7, 696, "emote/clap"),
        new("beckon", 8, 697, "emote/comeon"),
        new("shrug", 33, 730, "emote/shrug"),
        new("joy", 18, 708, "emote/joy"),
        new("welcome", 41, 738, "emote/welcome"),
        new("converse", 159, 5810, "emote/act_emot12"),
        new("me", 23, 715, "emote/me"),
        new("point", 27, 720, "emote/point"),
        new("thumbsup", 43, 740, "emote/yes_st"),
        new("huh", 17, 707, "emote/huh"),
        new("upset", 40, 737, "emote/upset"),
        new("blush", 4, 693, "emote/blush"),
        new("pray", 58, 742, "emote/pray"),
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nod"] = "yes",
        ["agree"] = "yes",
        ["headshake"] = "no",
        ["disagree"] = "no",
        ["hello"] = "wave",
        ["hi"] = "wave",
        ["greet"] = "wave",
        ["bye"] = "goodbye",
        ["farewell"] = "goodbye",
        ["thanks"] = "bow",
        ["thank"] = "bow",
        ["amazed"] = "surprised",
        ["surprise"] = "surprised",
        ["shock"] = "surprised",
        ["ponder"] = "think",
        ["hmm"] = "think",
        ["happy"] = "joy",
        ["excited"] = "joy",
        ["sad"] = "upset",
        ["frustrated"] = "upset",
        ["console"] = "comfort",
        ["pat"] = "comfort",
        ["talk"] = "converse",
        ["explain"] = "converse",
        ["giggle"] = "laugh",
        ["chuckle"] = "laugh",
        ["thumbs up"] = "thumbsup",
        ["thumbs-up"] = "thumbsup",
        ["applaud"] = "clap",
        ["confused"] = "huh",
        ["embarrassed"] = "blush",
        ["come"] = "beckon",
    };

    private static readonly Dictionary<string, EmoteInfo> ByName = All.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>A whitelisted emote by name or alias; null for anything else.</summary>
    public static EmoteInfo? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var n = name.Trim().TrimStart('/');
        if (ByName.TryGetValue(n, out var e))
            return e;
        return Aliases.TryGetValue(n, out var real) ? ByName[real] : null;
    }

    /// <summary>The tag names offered to the model in the system prompt.</summary>
    public static string PromptList => string.Join(", ", All.Select(e => e.Name));
}

/// <summary>
/// Non-emote ActionTimeline rows used for conversation and locomotion, verified the same way
/// (ActionTimeline.Key in brackets).
/// </summary>
public static class Timelines
{
    /// <summary>[normal/idle]</summary>
    public const ushort Idle = 3;

    /// <summary>[normal/idle_inactive1]: the idle fidget; minions and mounts "react" with it.</summary>
    public const ushort IdleFidget = 4;

    /// <summary>[normal/walk]</summary>
    public const ushort Walk = 13;

    /// <summary>[normal/run]</summary>
    public const ushort Run = 22;

    /// <summary>[event_base/event_base_stand_talk]: the talking loop, set as the base override while answering.</summary>
    public const ushort StandTalk = 799;

    /// <summary>[event_base/event_base_stand_listen]: the listening loop while idle in conversation.</summary>
    public const ushort StandListen = 801;

    /// <summary>[event/event_talk1]: the player's "asking" gesture when a question is sent.</summary>
    public const ushort AskGesture = 763;

    /// <summary>[event/event_talk_onehand]: an alternative asking gesture.</summary>
    public const ushort AskOneHand = 4218;
}
