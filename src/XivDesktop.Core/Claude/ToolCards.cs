using System.Text.Json;

namespace XivDesktop.Core.Claude;

/// <summary>
/// The kind of a tool card, which picks its icon. Terminal output says "Read(path)"; the panel says the
/// same thing with a glyph and a target.
/// </summary>
public enum ToolKind
{
    Edit,
    Read,
    Run,
    Search,
    Web,
    Task,
    Plan,
    Mcp,
    Other,
}

/// <summary>How far a tool call has got.</summary>
public enum ToolStatus
{
    /// <summary>Asked for; the spinner is up.</summary>
    Running,

    /// <summary>Waiting for the player to allow or deny it.</summary>
    Asking,

    Done,

    Failed,

    /// <summary>Denied in the permission dialog, or interrupted.</summary>
    Denied,
}

/// <summary>Naming and summarising tool calls: what the card says before and after the result.</summary>
public static class ToolCards
{
    public static ToolKind KindOf(string toolName)
    {
        if (toolName.StartsWith("mcp__", StringComparison.Ordinal))
            return ToolKind.Mcp;
        return toolName switch
        {
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" or "Update" => ToolKind.Edit,
            "Read" or "NotebookRead" or "ReadMcpResource" => ToolKind.Read,
            "Bash" or "BashOutput" or "KillShell" or "KillBash" => ToolKind.Run,
            "Grep" or "Glob" or "LS" or "SearchRepo" => ToolKind.Search,
            "WebFetch" or "WebSearch" => ToolKind.Web,
            "Task" or "Agent" or "SendMessage" => ToolKind.Task,
            "TodoWrite" or "ExitPlanMode" => ToolKind.Plan,
            _ => ToolKind.Other,
        };
    }

    /// <summary>A short glyph for the kind, for the ImGui fallback and the log.</summary>
    public static string Glyph(ToolKind kind) => kind switch
    {
        ToolKind.Edit => "✎",   // pencil
        ToolKind.Read => "▤",   // lines
        ToolKind.Run => ">_",
        ToolKind.Search => "⌕", // magnifier
        ToolKind.Web => "◌",    // globe stand-in
        ToolKind.Task => "▶",
        ToolKind.Plan => "☑",
        ToolKind.Mcp => "⚙",
        _ => "•",
    };

    /// <summary>
    /// The card's target line: the file, the command, the pattern or the url, shortened for one row.
    /// Falls back to the whole argument JSON when the tool is not one of the known ones.
    /// </summary>
    public static string Target(string toolName, string inputJson, int max = 72)
    {
        var target = RawTarget(toolName, inputJson);
        target = target.ReplaceLineEndings(" ").Trim();
        return Shorten(target, max);
    }

    private static string RawTarget(string toolName, string inputJson)
    {
        var input = TryParse(inputJson);
        if (input is null)
            return "";
        var o = input.Value;
        switch (toolName)
        {
            case "Bash":
                return Str(o, "command");
            case "Read" or "Edit" or "MultiEdit" or "Write" or "NotebookEdit" or "NotebookRead":
                return Str(o, "file_path") is { Length: > 0 } p ? p : Str(o, "notebook_path");
            case "Grep":
                var pattern = Str(o, "pattern");
                var path = Str(o, "path");
                return path.Length > 0 ? $"{pattern} in {path}" : pattern;
            case "Glob":
                return Str(o, "pattern");
            case "LS":
                return Str(o, "path");
            case "WebFetch":
                return Str(o, "url");
            case "WebSearch":
                return Str(o, "query");
            case "Task" or "Agent":
                return Str(o, "description");
            case "TodoWrite":
                return o.TryGetProperty("todos", out var todos) && todos.ValueKind == JsonValueKind.Array
                    ? $"{todos.GetArrayLength()} items"
                    : "";
            default:
                // An MCP tool or one XivDesktop has no template for: first string argument, else the JSON.
                if (o.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in o.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                            return prop.Value.GetString() ?? "";
                    }
                }

                return inputJson == "{}" ? "" : inputJson;
        }
    }

    /// <summary>
    /// The one-line result: "+12 −3" for an edit, "exit 0" for a command, "14 files" for a search.
    /// <paramref name="isError"/> comes from the tool_result; the wording stays short either way.
    /// </summary>
    public static string ResultLine(string toolName, string inputJson, string result, bool isError)
    {
        if (isError)
        {
            var first = FirstLine(result);
            return first.Length > 0 ? Shorten("failed: " + first, 60) : "failed";
        }

        switch (toolName)
        {
            case "Edit" or "MultiEdit":
                var (added, removed) = EditLines(inputJson);
                return $"+{added} −{removed}";
            case "Write":
                var input = TryParse(inputJson);
                var content = input is { } w ? Str(w, "content") : "";
                return $"+{CountLines(content)} −0";
            case "Bash":
                var lines = CountLines(result);
                return lines > 0 ? $"exit 0 · {Plural(lines, "line")}" : "exit 0";
            case "Read" or "NotebookRead":
                return Plural(CountLines(result), "line");
            case "Grep":
                return GrepSummary(result);
            case "Glob" or "LS":
                return Plural(CountLines(result), "entry", "entries");
            case "WebFetch" or "WebSearch":
                return Plural(CountLines(result), "line");
            case "TodoWrite":
                return "updated";
            default:
                var n = CountLines(result);
                return n == 0 ? "done" : Plural(n, "line");
        }
    }

    /// <summary>
    /// The card's expandable detail: at most <paramref name="max"/> lines of the result, with a marker
    /// when more was dropped. The raw toggle shows the whole thing instead.
    /// </summary>
    public static IReadOnlyList<string> Detail(string result, int max = 6, int width = 160)
    {
        if (string.IsNullOrEmpty(result))
            return [];
        var lines = result.ReplaceLineEndings("\n").Split('\n');
        var kept = new List<string>(max + 1);
        foreach (var line in lines)
        {
            if (kept.Count == max)
            {
                kept.Add($"… {lines.Length - max} more lines (raw)");
                break;
            }

            kept.Add(Shorten(line.TrimEnd(), width));
        }

        return kept;
    }

    private static (int Added, int Removed) EditLines(string inputJson)
    {
        var input = TryParse(inputJson);
        if (input is not { } o)
            return (0, 0);
        if (o.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
        {
            var added = 0;
            var removed = 0;
            foreach (var e in edits.EnumerateArray())
            {
                added += CountLines(Str(e, "new_string"));
                removed += CountLines(Str(e, "old_string"));
            }

            return (added, removed);
        }

        return (CountLines(Str(o, "new_string")), CountLines(Str(o, "old_string")));
    }

    private static string GrepSummary(string result)
    {
        var first = FirstLine(result);
        // ripgrep's own "Found N files"/"Found N matches" header, when the mode printed one.
        if (first.StartsWith("Found ", StringComparison.Ordinal))
            return Shorten(first[6..], 40);
        var n = CountLines(result);
        return n == 0 ? "no matches" : Plural(n, "match", "matches");
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var n = 1;
        foreach (var c in text)
        {
            if (c == '\n')
                n++;
        }

        return text.EndsWith('\n') ? n - 1 : n;
    }

    private static string FirstLine(string text)
    {
        var idx = text.IndexOfAny(['\n', '\r']);
        return (idx < 0 ? text : text[..idx]).Trim();
    }

    private static string Plural(int n, string one, string? many = null)
        => n == 1 ? $"1 {one}" : $"{n} {many ?? one + "s"}";

    public static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";

    private static JsonElement? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Str(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
