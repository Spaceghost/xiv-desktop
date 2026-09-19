namespace XivDesktop.Core.Palette;

/// <summary>A fuzzy match: a score (higher is better) and the matched character positions in the text.</summary>
public sealed record FuzzyMatch(double Score, int[] Positions)
{
    public static readonly FuzzyMatch All = new(1, []);
}

/// <summary>
/// fzf-style subsequence matching for the palette. Every query character (spaces ignored, case-insensitive)
/// must appear in order. Contiguous runs, word starts (after a separator or a lower→upper step) and an early
/// first match score higher; the positions are for highlighting. Greedy with a second pass that prefers
/// word starts, which is enough for short titles and cheap every frame.
/// </summary>
public static class Fuzzy
{
    public static FuzzyMatch? Match(string text, string query)
    {
        var q = query.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        if (q.Length == 0)
            return FuzzyMatch.All;
        if (text.Length == 0)
            return null;
        var lower = text.ToLowerInvariant();

        // An exact substring beats any scattered match: take the best-placed one.
        var sub = lower.IndexOf(q, StringComparison.Ordinal);
        if (sub >= 0)
        {
            var best = sub;
            for (var i = sub; i >= 0 && i < lower.Length; i = lower.IndexOf(q, i + 1, StringComparison.Ordinal))
            {
                if (IsWordStart(text, i))
                {
                    best = i;
                    break;
                }
            }

            var pos = Enumerable.Range(best, q.Length).ToArray();
            return new FuzzyMatch(ScorePositions(text, pos, q.Length) + (lower.Length == q.Length ? 50 : 0), pos);
        }

        var a = Greedy(text, lower, q, preferWordStarts: false);
        if (a == null)
            return null;
        var b = Greedy(text, lower, q, preferWordStarts: true);
        var sa = ScorePositions(text, a, q.Length);
        var sb = b == null ? double.MinValue : ScorePositions(text, b, q.Length);
        return sb > sa ? new FuzzyMatch(sb, b!) : new FuzzyMatch(sa, a);
    }

    private static int[]? Greedy(string text, string lower, string q, bool preferWordStarts)
    {
        var pos = new int[q.Length];
        var i = 0;
        for (var j = 0; j < q.Length; j++)
        {
            var found = -1;
            if (preferWordStarts)
            {
                // Jump to the next word start with this letter if the rest can still match after it.
                for (var k = i; k < lower.Length; k++)
                {
                    if (lower[k] == q[j] && IsWordStart(text, k) && CanFinish(lower, q, j + 1, k + 1))
                    {
                        found = k;
                        break;
                    }
                }
            }

            if (found < 0)
                found = lower.IndexOf(q[j], i);
            if (found < 0)
                return null;
            pos[j] = found;
            i = found + 1;
        }

        return pos;
    }

    private static bool CanFinish(string lower, string q, int j, int from)
    {
        for (; j < q.Length; j++)
        {
            var k = lower.IndexOf(q[j], from);
            if (k < 0)
                return false;
            from = k + 1;
        }

        return true;
    }

    public static bool IsWordStart(string text, int i)
        => i == 0 || !char.IsLetterOrDigit(text[i - 1]) || (char.IsUpper(text[i]) && char.IsLower(text[i - 1]));

    private static double ScorePositions(string text, int[] pos, int qlen)
    {
        var score = 100.0;
        for (var j = 0; j < pos.Length; j++)
        {
            if (IsWordStart(text, pos[j]))
                score += j == 0 ? 25 : 12; // starting on a word matters most
            if (j > 0)
            {
                var gap = pos[j] - pos[j - 1] - 1;
                score += gap == 0 ? 10 : -Math.Min(12, gap * 2);
            }
        }

        score -= Math.Min(15, pos.Length > 0 ? pos[0] : 0);
        score -= Math.Min(20, (text.Length - qlen) * 0.5);
        return Math.Max(1, score);
    }
}
