using System.Text;

namespace XivDesktop.Core;

/// <summary>
/// Parser for the [Desktop Entry] group of freedesktop .desktop files (Desktop Entry Specification 1.5).
/// Other groups (Desktop Action ...) are ignored. Never throws on malformed input; returns null when the
/// file has no [Desktop Entry] group.
/// </summary>
public static class DesktopEntryParser
{
    public const string MainGroup = "Desktop Entry";

    public static DesktopEntry? Parse(string text, string id, string sourcePath = "")
    {
        var plain = new Dictionary<string, string>(StringComparer.Ordinal);
        var localized = new Dictionary<string, List<(string Locale, string Value)>>(StringComparer.Ordinal);
        var inMain = false;
        var sawMain = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').TrimStart();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                var group = close > 0 ? line[1..close] : "";
                if (sawMain && inMain)
                    break; // the main group ended; nothing after it matters
                inMain = group == MainGroup;
                sawMain |= inMain;
                continue;
            }

            if (!inMain)
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].TrimEnd();
            var value = line[(eq + 1)..].TrimStart();

            var bracket = key.IndexOf('[');
            if (bracket > 0 && key.EndsWith(']'))
            {
                var baseKey = key[..bracket];
                var locale = key[(bracket + 1)..^1];
                if (!localized.TryGetValue(baseKey, out var list))
                    localized[baseKey] = list = [];
                list.Add((locale, value));
                continue;
            }

            // First occurrence wins (duplicate keys are invalid per spec).
            plain.TryAdd(key, value);
        }

        if (!sawMain)
            return null;

        string Str(string key) => plain.TryGetValue(key, out var v) ? UnescapeString(v) : "";

        // Prefer the unlocalized value; fall back to C, then English, then the first localization.
        string LocaleStr(string key)
        {
            if (plain.TryGetValue(key, out var v))
                return UnescapeString(v);
            return localized.TryGetValue(key, out var list) ? UnescapeString(PickLocale(list)) : "";
        }

        IReadOnlyList<string> List(string key, bool localizedKey = false)
        {
            if (plain.TryGetValue(key, out var v))
                return SplitList(v);
            if (localizedKey && localized.TryGetValue(key, out var list))
                return SplitList(PickLocale(list));
            return [];
        }

        bool Bool(string key) => plain.TryGetValue(key, out var v) && v.Trim() == "true";

        return new DesktopEntry
        {
            Id = id,
            SourcePath = sourcePath,
            Type = Str("Type"),
            Name = LocaleStr("Name"),
            GenericName = LocaleStr("GenericName"),
            Comment = LocaleStr("Comment"),
            Keywords = List("Keywords", localizedKey: true),
            Categories = List("Categories"),
            Exec = Str("Exec"),
            TryExec = Str("TryExec"),
            Icon = LocaleStr("Icon"),
            Terminal = Bool("Terminal"),
            NoDisplay = Bool("NoDisplay"),
            Hidden = Bool("Hidden"),
            OnlyShowIn = List("OnlyShowIn"),
            NotShowIn = List("NotShowIn"),
        };
    }

    private static string PickLocale(List<(string Locale, string Value)> list)
    {
        foreach (var (locale, value) in list)
        {
            if (locale == "C")
                return value;
        }

        foreach (var (locale, value) in list)
        {
            if (locale == "en" || locale.StartsWith("en_", StringComparison.Ordinal) || locale.StartsWith("en@", StringComparison.Ordinal))
                return value;
        }

        return list.Count > 0 ? list[0].Value : "";
    }

    /// <summary>
    /// Unescapes a string/localestring value: \s space, \n newline, \t tab, \r CR, \\ backslash.
    /// Any other backslash sequence is kept as written (the Exec parser handles its own escapes).
    /// </summary>
    public static string UnescapeString(string value)
    {
        if (value.IndexOf('\\') < 0)
            return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch != '\\' || i + 1 >= value.Length)
            {
                sb.Append(ch);
                continue;
            }

            var next = value[i + 1];
            switch (next)
            {
                case 's': sb.Append(' '); i++; break;
                case 'n': sb.Append('\n'); i++; break;
                case 't': sb.Append('\t'); i++; break;
                case 'r': sb.Append('\r'); i++; break;
                case '\\': sb.Append('\\'); i++; break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Splits a ';'-separated list ("\;" is a literal semicolon), unescaping each item and dropping empties.</summary>
    public static IReadOnlyList<string> SplitList(string raw)
    {
        var items = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (ch == '\\' && i + 1 < raw.Length && raw[i + 1] == ';')
            {
                sb.Append(';');
                i++;
            }
            else if (ch == '\\' && i + 1 < raw.Length)
            {
                sb.Append(ch).Append(raw[i + 1]);
                i++;
            }
            else if (ch == ';')
            {
                Flush();
            }
            else
            {
                sb.Append(ch);
            }
        }

        Flush();
        return items;

        void Flush()
        {
            var item = UnescapeString(sb.ToString()).Trim();
            if (item.Length > 0)
                items.Add(item);
            sb.Clear();
        }
    }
}
