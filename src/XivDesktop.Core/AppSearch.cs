namespace XivDesktop.Core;

/// <summary>
/// Fuzzy app search: each query word is scored against the name, keywords, generic name and id
/// (exact &gt; prefix &gt; word prefix &gt; substring &gt; subsequence), every word must match somewhere,
/// and favourites and recents get a small boost. Pure and allocation-light; safe on the UI thread
/// for a few hundred apps.
/// </summary>
public static class AppSearch
{
    private const double NameWeight = 1.0;
    private const double KeywordWeight = 0.8;
    private const double GenericWeight = 0.7;
    private const double IdWeight = 0.5;

    /// <summary>Apps matching <paramref name="query"/>, best first. An empty query returns every app in catalog order.</summary>
    public static List<AppInfo> Search(IReadOnlyList<AppInfo> apps, string query, IReadOnlyCollection<string>? favourites = null, IReadOnlyList<string>? recents = null)
    {
        var words = query.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return [.. apps];

        var scored = new List<(AppInfo App, double Score, int Index)>();
        for (var i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            var score = Score(app, words);
            if (score <= 0)
                continue;
            if (favourites != null && favourites.Contains(app.Id))
                score += 50;
            if (recents != null)
            {
                var r = IndexOf(recents, app.Id);
                if (r >= 0)
                    score += Math.Max(0, 30 - (r * 3));
            }

            scored.Add((app, score, i));
        }

        scored.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Index.CompareTo(b.Index));
        return scored.Select(s => s.App).ToList();
    }

    /// <summary>Total score for lower-cased query words; 0 when any word matches nothing.</summary>
    public static double Score(AppInfo app, IReadOnlyList<string> words)
    {
        var total = 0.0;
        var id = app.Id.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) ? app.Id[..^8] : app.Id;
        foreach (var w in words)
        {
            var best = NameWeight * Field(app.Name, w);
            foreach (var k in app.Keywords)
                best = Math.Max(best, KeywordWeight * Field(k, w));
            best = Math.Max(best, GenericWeight * Field(app.GenericName, w));
            best = Math.Max(best, IdWeight * Field(id, w));
            if (best <= 0)
                return 0;
            total += best;
        }

        return total;
    }

    /// <summary>Score of one lower-cased query word against one field.</summary>
    public static double Field(string field, string word)
    {
        if (field.Length == 0 || word.Length == 0)
            return 0;
        var f = field.ToLowerInvariant();
        if (f == word)
            return 1000;
        if (f.StartsWith(word, StringComparison.Ordinal))
            return 800 - Math.Min(100, f.Length - word.Length);
        var idx = f.IndexOf(word, StringComparison.Ordinal);
        if (idx > 0)
        {
            // Substring at a word boundary ("text" in "gnome text editor", "Editor" in "TextEditor") ranks above a mid-word hit.
            var boundary = !char.IsLetterOrDigit(f[idx - 1]) || (char.IsUpper(field[idx]) && char.IsLower(field[idx - 1]));
            return boundary ? 600 - Math.Min(100, idx) : 400 - Math.Min(100, idx);
        }

        return Subsequence(field, f, word);
    }

    /// <summary>Subsequence match: 1..300, higher when matched letters start words and sit close together.</summary>
    private static double Subsequence(string original, string lower, string word)
    {
        var score = 300.0;
        var last = -1;
        var j = 0;
        for (var i = 0; i < lower.Length && j < word.Length; i++)
        {
            if (lower[i] != word[j])
                continue;
            var wordStart = i == 0 || !char.IsLetterOrDigit(lower[i - 1]) || (char.IsUpper(original[i]) && char.IsLower(original[i - 1]));
            if (last >= 0)
                score -= Math.Min(20, (i - last - 1) * 4);
            else
                score -= Math.Min(40, i * 2);
            if (wordStart)
                score += 10;
            last = i;
            j++;
        }

        return j == word.Length ? Math.Clamp(score, 1, 300) : 0;
    }

    private static int IndexOf(IReadOnlyList<string> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], id, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }
}
