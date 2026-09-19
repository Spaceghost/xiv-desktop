namespace XivDesktop.Core.Claude;

/// <summary>
/// What XivDesktop remembers about one Claude session between game sessions. Persisted in the plugin
/// configuration; it holds no transcript and no token, only what is needed to find the session again.
/// </summary>
[Serializable]
public sealed class SessionRecord
{
    /// <summary>The name the player (or the model) gave it; <c>/claude &lt;name&gt; …</c> takes this.</summary>
    public string Name { get; set; } = "";

    /// <summary>Claude's own session id, from the <c>init</c> event; what <c>--resume</c> takes.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The Linux working directory it ran in.</summary>
    public string Cwd { get; set; } = "";

    /// <summary>The model the <c>init</c> event reported.</summary>
    public string Model { get; set; } = "";

    /// <summary>The first prompt, shortened: what <c>/claude resume</c> lists.</summary>
    public string Title { get; set; } = "";

    /// <summary>The agent's job id while the job is alive, so a reload can JATTACH instead of restarting.</summary>
    public uint JobId { get; set; }

    /// <summary>Output bytes already consumed, for JATTACH's <c>fed</c> so the replay skips them.</summary>
    public long Fed { get; set; }

    /// <summary>The seed the NPC's look was rolled from.</summary>
    public uint LookSeed { get; set; }

    /// <summary>The NPC's chosen name, when the model picked one.</summary>
    public string NpcName { get; set; } = "";

    public string Style { get; set; } = "";

    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The key the panel, the NPC and the permission rules all use; the name, or the session id.</summary>
    public string Key => Name.Length > 0 ? Name : SessionId;

    /// <summary>"scholar · ~/code/thing · 2h ago".</summary>
    public string Summary(DateTimeOffset now)
    {
        var parts = new List<string>(3);
        if (Model.Length > 0)
            parts.Add(ShortModel(Model));
        if (Cwd.Length > 0)
            parts.Add(Cwd);
        parts.Add(Ago(now - LastActivity));
        return string.Join(" · ", parts);
    }

    /// <summary>"claude-opus-4-5-20251101" reads better as "opus-4-5".</summary>
    public static string ShortModel(string model)
    {
        var name = model.StartsWith("claude-", StringComparison.Ordinal) ? model[7..] : model;
        var parts = name.Split('-');
        var kept = parts.TakeWhile(p => !(p.Length == 8 && p.All(char.IsDigit))).ToArray();
        return kept.Length > 0 ? string.Join('-', kept) : name;
    }

    public static string Ago(TimeSpan span) => span switch
    {
        { TotalSeconds: < 60 } => "just now",
        { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes}m ago",
        { TotalHours: < 24 } => $"{(int)span.TotalHours}h ago",
        _ => $"{(int)span.TotalDays}d ago",
    };
}

/// <summary>
/// The remembered sessions, newest first. Pure list handling: the plugin owns the persisted list and this
/// decides what goes in it, what comes out and in what order.
/// </summary>
public static class SessionHistory
{
    /// <summary>How many sessions are remembered; older ones are dropped.</summary>
    public const int Max = 30;

    /// <summary>A warning is shown once this many sessions are busy at the same time.</summary>
    public const int BusyWarning = 3;

    /// <summary>The most a player can have open at once; <c>/claude new</c> refuses past it.</summary>
    public const int MaxLive = 6;

    public static List<SessionRecord> Touch(IEnumerable<SessionRecord> existing, SessionRecord record, DateTimeOffset now)
    {
        record.LastActivity = now;
        var list = existing.Where(r => !Same(r, record)).ToList();
        list.Insert(0, record);
        if (list.Count > Max)
            list.RemoveRange(Max, list.Count - Max);
        return list;
    }

    public static List<SessionRecord> Sorted(IEnumerable<SessionRecord> records)
        => records.OrderByDescending(r => r.LastActivity).ToList();

    /// <summary>The record matching a name or a session id (a prefix of one is enough), or null.</summary>
    public static SessionRecord? Find(IEnumerable<SessionRecord> records, string nameOrId)
    {
        var needle = nameOrId.Trim();
        if (needle.Length == 0)
            return null;
        var list = records.ToList();
        return list.FirstOrDefault(r => string.Equals(r.Name, needle, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(r => string.Equals(r.SessionId, needle, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(r => r.SessionId.StartsWith(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A name that is not taken: "claude", then "claude-2"…</summary>
    public static string UniqueName(IEnumerable<SessionRecord> records, string wanted)
    {
        var taken = records.Select(r => r.Name).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var basis = wanted.Trim().Length > 0 ? wanted.Trim() : "claude";
        if (!taken.Contains(basis))
            return basis;
        for (var n = 2; n < 100; n++)
        {
            var candidate = $"{basis}-{n}";
            if (!taken.Contains(candidate))
                return candidate;
        }

        return basis + "-" + Guid.NewGuid().ToString("N")[..4];
    }

    /// <summary>A session's title: the first prompt, on one line.</summary>
    public static string TitleFrom(string prompt) => ToolCards.Shorten(prompt.ReplaceLineEndings(" ").Trim(), 60);

    private static bool Same(SessionRecord a, SessionRecord b)
        => (a.SessionId.Length > 0 && a.SessionId == b.SessionId)
        || (a.Name.Length > 0 && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
}
