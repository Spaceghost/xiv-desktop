using System.Text;

namespace XivDesktop.Core.Ask;

/// <summary>Who a cue is for: the summoned NPC or the player's own character.</summary>
public enum CueTarget
{
    Npc,
    Player,
}

/// <summary>An emote cue: "laugh" for the NPC, "yes" for the player, … (names from <see cref="EmoteTable"/>).</summary>
public readonly record struct Cue(CueTarget Target, string Emote);

/// <summary>
/// Streaming filter for the model's answer. The system prompt asks for tags such as <c>[emote:laugh]</c>
/// and <c>[player:nod]</c>; this removes them from the text as it streams (a tag split across deltas is
/// held back until it closes) and reports them as cues. It also drops <c>&lt;think&gt;…&lt;/think&gt;</c>
/// blocks, which some local models put inline. Unknown tags (<c>[note]</c>, <c>[1]</c>) pass through as text.
/// </summary>
public sealed class CueFilter
{
    private const int MaxTag = 40;
    private readonly StringBuilder pending = new();
    private bool inThink;

    public (string Text, List<Cue> Cues) Push(string delta)
    {
        pending.Append(delta);
        return Drain(final: false);
    }

    /// <summary>End of the answer: whatever is held back is text (an unclosed tag is not a tag).</summary>
    public (string Text, List<Cue> Cues) Flush() => Drain(final: true);

    private (string, List<Cue>) Drain(bool final)
    {
        var text = new StringBuilder();
        var cues = new List<Cue>();
        var s = pending.ToString();
        var i = 0;
        while (i < s.Length)
        {
            if (inThink)
            {
                var end = s.IndexOf("</think>", i, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                {
                    // Keep a possible partial "</think" at the tail; drop the rest.
                    var keep = PartialSuffix(s, i, "</think>");
                    i = s.Length - keep;
                    break;
                }

                i = end + "</think>".Length;
                inThink = false;
                continue;
            }

            var c = s[i];
            if (c == '<')
            {
                var rest = s.Length - i;
                if (string.Compare(s, i, "<think>", 0, Math.Min(rest, 7), StringComparison.OrdinalIgnoreCase) == 0)
                {
                    if (rest < 7 && !final)
                        break; // could still become "<think>"
                    if (rest >= 7)
                    {
                        inThink = true;
                        i += 7;
                        continue;
                    }
                }

                text.Append(c);
                i++;
                continue;
            }

            if (c == '[')
            {
                var close = s.IndexOf(']', i + 1);
                if (close < 0)
                {
                    if (!final && s.Length - i <= MaxTag && !s.AsSpan(i + 1).ContainsAny('\n', '['))
                        break; // wait for the rest of the tag
                    text.Append(c);
                    i++;
                    continue;
                }

                if (TryParseTag(s.AsSpan(i + 1, close - i - 1), out var cue))
                {
                    cues.Add(cue);
                    i = close + 1;
                    continue;
                }

                text.Append(c);
                i++;
                continue;
            }

            text.Append(c);
            i++;
        }

        pending.Remove(0, i);
        if (final)
        {
            if (!inThink)
                text.Append(pending);
            pending.Clear();
            inThink = false;
        }

        return (CollapseTagGaps(text.ToString()), cues);
    }

    private static int PartialSuffix(string s, int from, string token)
    {
        for (var n = Math.Min(token.Length - 1, s.Length - from); n > 0; n--)
        {
            if (string.Compare(s, s.Length - n, token, 0, n, StringComparison.OrdinalIgnoreCase) == 0)
                return n;
        }

        return 0;
    }

    // "[emote:laugh] Ha!" leaves " Ha!"; a double space where a tag sat mid-sentence becomes one.
    private static string CollapseTagGaps(string s) => s.Replace("  ", " ", StringComparison.Ordinal);

    /// <summary>"emote:laugh", "npc: wave", "player:nod" → a cue; anything else → false.</summary>
    public static bool TryParseTag(ReadOnlySpan<char> body, out Cue cue)
    {
        cue = default;
        var colon = body.IndexOf(':');
        if (colon <= 0)
            return false;
        var key = body[..colon].Trim().ToString().ToLowerInvariant();
        var value = body[(colon + 1)..].Trim().ToString().ToLowerInvariant().TrimStart('/');
        CueTarget target;
        switch (key)
        {
            case "emote" or "npc" or "me":
                target = CueTarget.Npc;
                break;
            case "player" or "you" or "user":
                target = CueTarget.Player;
                break;
            default:
                return false;
        }

        if (EmoteTable.Find(value) is not { } e)
            return false;
        cue = new Cue(target, e.Name);
        return true;
    }
}

/// <summary>
/// The cheap fallback when the model gave no cue: a keyword guess from what the player typed. Returns the
/// NPC's reaction (and optionally the player's own gesture while asking).
/// </summary>
public static class KeywordCues
{
    private static readonly (string[] Words, string Npc, string? Player)[] Rules =
    [
        (["thank", "thanks", "thx", "ty ", "cheers", "appreciate", "grateful"], "bow", null),
        (["haha", "lol", "lmao", "joke", "funny", "hehe", "rofl", "pun"], "laugh", "laugh"),
        (["ugh", "frustrat", "annoy", "angry", "hate this", "stuck", "broken", "doesn't work", "does not work", "sad", "tired", "stressed", "worried", "help me"], "comfort", "upset"),
        (["hello", "hi ", "hey", "greetings", "good morning", "good evening", "good afternoon", "yo "], "wave", "wave"),
        (["bye", "goodbye", "farewell", "see you", "later!"], "goodbye", "wave"),
        (["congrat", "i did it", "we did it", "finally", "yay", "hooray", "woo"], "cheer", "joy"),
        (["why", "how come", "what if", "wonder", "hmm", "curious"], "think", "think"),
        (["sorry", "apolog", "my bad", "oops"], "comfort", null),
        (["wow", "whoa", "really?", "no way", "amazing", "incredible"], "surprised", null),
    ];

    /// <summary>The first rule matching <paramref name="userText"/>, or null. Case-insensitive; "hi" matches as a word.</summary>
    public static (string Npc, string? Player)? Guess(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return null;
        var t = " " + userText.ToLowerInvariant().Trim() + " ";
        foreach (var (words, npc, player) in Rules)
        {
            foreach (var w in words)
            {
                var needle = w.EndsWith(' ') ? " " + w : w; // word-ish match for short greetings
                if (t.Contains(needle, StringComparison.Ordinal))
                    return (npc, player);
            }
        }

        return null;
    }
}
