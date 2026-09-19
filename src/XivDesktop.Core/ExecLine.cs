using System.Text;

namespace XivDesktop.Core;

/// <summary>
/// Exec= handling per the Desktop Entry Specification ("The Exec key"): split into arguments with the
/// spec's double-quote rules, drop field codes (nothing is being opened, so %f %F %u %U expand to nothing;
/// %i %c %k are dropped too), then re-quote every argument for POSIX sh. The result is what XivDesktop
/// sends after "window pull run ", which ghostty-agent runs with sh -c on the Linux host.
/// </summary>
public static class ExecLine
{
    /// <summary>Whole arguments that are dropped: flatpak --file-forwarding markers left empty without files.</summary>
    private static readonly HashSet<string> DroppedArguments = new(StringComparer.Ordinal) { "@@", "@@u", "@@f" };

    /// <summary>
    /// Splits an (already string-unescaped) Exec value into arguments. Returns null when the quoting is
    /// malformed (unterminated quote). Inside double quotes, backslash escapes ", `, $ and \.
    /// </summary>
    public static List<string>? Split(string exec)
    {
        var args = new List<string>();
        var sb = new StringBuilder();
        var inArg = false;
        var quoted = false;
        for (var i = 0; i < exec.Length; i++)
        {
            var ch = exec[i];
            if (quoted)
            {
                if (ch == '\\' && i + 1 < exec.Length && exec[i + 1] is '"' or '`' or '$' or '\\')
                {
                    sb.Append(exec[++i]);
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    sb.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case ' ' or '\t' or '\n':
                    if (inArg)
                    {
                        args.Add(sb.ToString());
                        sb.Clear();
                        inArg = false;
                    }

                    break;
                case '"':
                    quoted = true;
                    inArg = true;
                    break;
                case '\\' when i + 1 < exec.Length:
                    // Not allowed by the spec outside quotes, but common in the wild: treat as a literal escape.
                    sb.Append(exec[++i]);
                    inArg = true;
                    break;
                default:
                    sb.Append(ch);
                    inArg = true;
                    break;
            }
        }

        if (quoted)
            return null;
        if (inArg)
            args.Add(sb.ToString());
        return args;
    }

    /// <summary>Removes field codes from one argument. Returns null when the argument was only a field code.</summary>
    public static string? StripFieldCodes(string arg)
    {
        if (arg.IndexOf('%') < 0)
            return arg;
        var sb = new StringBuilder(arg.Length);
        var removedAny = false;
        for (var i = 0; i < arg.Length; i++)
        {
            var ch = arg[i];
            if (ch != '%' || i + 1 >= arg.Length)
            {
                sb.Append(ch);
                continue;
            }

            var code = arg[++i];
            if (code == '%')
            {
                sb.Append('%');
            }
            else
            {
                // %f %F %u %U %i %c %k (and deprecated %d %D %n %N %v %m) expand to nothing here;
                // any other code is invalid per spec and is dropped as well.
                removedAny = true;
            }
        }

        return removedAny && sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// Converts an Exec value into a sh command line, or null when it is empty or malformed.
    /// </summary>
    public static string? ToShellCommand(string exec)
    {
        var args = Split(exec);
        if (args == null)
            return null;
        var parts = new List<string>(args.Count);
        foreach (var arg in args)
        {
            if (DroppedArguments.Contains(arg))
                continue;
            var stripped = StripFieldCodes(arg);
            if (stripped == null)
                continue;
            parts.Add(ShellQuote(stripped));
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>Quotes one argument for POSIX sh: bare when it only has safe characters, else single-quoted.</summary>
    public static string ShellQuote(string arg)
    {
        if (arg.Length == 0)
            return "''";
        var safe = true;
        foreach (var ch in arg)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or '/' or '=' or ':' or ',' or '+' or '@' or '%'))
            {
                safe = false;
                break;
            }
        }

        return safe ? arg : "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>The first argument (the program), or "" when there is none.</summary>
    public static string Program(string exec) => Split(exec) is { Count: > 0 } a ? a[0] : "";
}
