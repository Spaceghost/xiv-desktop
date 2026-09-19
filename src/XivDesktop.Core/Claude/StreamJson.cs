using System.Text.Json;

namespace XivDesktop.Core.Claude;

/// <summary>What one line of <c>--output-format stream-json</c> turned out to be.</summary>
public enum ClaudeEventKind
{
    /// <summary>Not JSON, or JSON XivDesktop has no use for; <see cref="ClaudeEvent.Raw"/> still has it.</summary>
    Unknown,

    /// <summary><c>system/init</c>: the session id, model, working directory and permission mode.</summary>
    Init,

    /// <summary>Another <c>system</c> line (hooks, thinking token counts, compaction…).</summary>
    System,

    /// <summary>Assistant text.</summary>
    Text,

    /// <summary>Assistant thinking (collapsed by default).</summary>
    Thinking,

    /// <summary>The assistant asking for a tool.</summary>
    ToolUse,

    /// <summary>A tool's result, carried back on a <c>user</c> message.</summary>
    ToolResult,

    /// <summary>A <c>stream_event</c> delta while <c>--include-partial-messages</c> is on.</summary>
    Partial,

    /// <summary>The turn's <c>result</c> line: cost, usage and why it stopped.</summary>
    Result,

    /// <summary>A rate limit notice.</summary>
    RateLimit,

    /// <summary>The CLI reporting an error it could not recover from.</summary>
    Error,
}

/// <summary>
/// One parsed stream-json event. Only the fields XivDesktop renders are lifted out; <see cref="Raw"/>
/// keeps the original line for the panel's raw toggle.
/// </summary>
public sealed record ClaudeEvent
{
    public required ClaudeEventKind Kind { get; init; }

    public string Raw { get; init; } = "";

    public string SessionId { get; init; } = "";

    public string Model { get; init; } = "";

    public string Cwd { get; init; } = "";

    public string PermissionMode { get; init; } = "";

    public string Version { get; init; } = "";

    /// <summary>Text, thinking, a partial delta, an error message or a notice.</summary>
    public string Text { get; init; } = "";

    /// <summary>Tool use and tool result: the <c>toolu_…</c> id that pairs them.</summary>
    public string ToolUseId { get; init; } = "";

    public string ToolName { get; init; } = "";

    /// <summary>The tool's arguments as JSON (the card shows them; the dialog shows them too).</summary>
    public string ToolInput { get; init; } = "";

    public bool IsError { get; init; }

    /// <summary>The <c>result</c> line's subtype ("success", "error_max_turns"…).</summary>
    public string Subtype { get; init; } = "";

    public double CostUsd { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long DurationMs { get; init; }
}

/// <summary>
/// Turns <c>stream-json</c> lines into <see cref="ClaudeEvent"/>s. Total: a malformed line becomes an
/// <see cref="ClaudeEventKind.Unknown"/> event, never an exception, because the job's stdout is not
/// something XivDesktop controls.
/// </summary>
public static class StreamJson
{
    /// <summary>
    /// Parses one line. An assistant message carries several content blocks, so this returns a list:
    /// text, thinking and tool_use each become their own event, in order.
    /// </summary>
    public static IReadOnlyList<ClaudeEvent> Parse(string? line)
    {
        var text = (line ?? "").Trim();
        if (text.Length == 0)
            return [];
        if (text[0] != '{')
            return [new ClaudeEvent { Kind = ClaudeEventKind.Unknown, Raw = text, Text = text }];

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return [new ClaudeEvent { Kind = ClaudeEventKind.Unknown, Raw = text, Text = text }];
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return [new ClaudeEvent { Kind = ClaudeEventKind.Unknown, Raw = text, Text = text }];
            var session = Str(root, "session_id");
            return Str(root, "type") switch
            {
                "system" => [System(root, text, session)],
                "assistant" => Message(root, text, session, assistant: true),
                "user" => Message(root, text, session, assistant: false),
                "stream_event" => Partial(root, text, session),
                "result" => [Result(root, text, session)],
                "rate_limit_event" => [new ClaudeEvent { Kind = ClaudeEventKind.RateLimit, Raw = text, SessionId = session, Text = RateLimitText(root) }],
                "error" => [new ClaudeEvent { Kind = ClaudeEventKind.Error, Raw = text, SessionId = session, Text = Str(root, "message"), IsError = true }],
                _ => [new ClaudeEvent { Kind = ClaudeEventKind.Unknown, Raw = text, SessionId = session }],
            };
        }
    }

    private static ClaudeEvent System(JsonElement root, string raw, string session)
    {
        var subtype = Str(root, "subtype");
        if (subtype == "init")
        {
            return new ClaudeEvent
            {
                Kind = ClaudeEventKind.Init,
                Raw = raw,
                SessionId = session,
                Model = Str(root, "model"),
                Cwd = Str(root, "cwd"),
                PermissionMode = Str(root, "permissionMode"),
                Version = Str(root, "claude_code_version"),
                Subtype = subtype,
            };
        }

        var note = subtype switch
        {
            "hook_started" => $"hook {Str(root, "hook_name")}",
            "hook_response" => "",
            "thinking_tokens" => "",
            "compact_boundary" => "context compacted",
            _ => subtype,
        };
        return new ClaudeEvent { Kind = ClaudeEventKind.System, Raw = raw, SessionId = session, Subtype = subtype, Text = note };
    }

    private static List<ClaudeEvent> Message(JsonElement root, string raw, string session, bool assistant)
    {
        var events = new List<ClaudeEvent>();
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return [new ClaudeEvent { Kind = ClaudeEventKind.Unknown, Raw = raw, SessionId = session }];
        var model = Str(message, "model");
        if (!message.TryGetProperty("content", out var content))
            return events;

        // A text-only turn can carry a bare string instead of a block list.
        if (content.ValueKind == JsonValueKind.String)
        {
            var body = content.GetString() ?? "";
            if (body.Length > 0)
                events.Add(new ClaudeEvent { Kind = ClaudeEventKind.Text, Raw = raw, SessionId = session, Model = model, Text = body });
            return events;
        }

        if (content.ValueKind != JsonValueKind.Array)
            return events;

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                continue;
            switch (Str(block, "type"))
            {
                case "text":
                    var body = Str(block, "text");
                    if (body.Length > 0)
                        events.Add(new ClaudeEvent { Kind = assistant ? ClaudeEventKind.Text : ClaudeEventKind.Unknown, Raw = raw, SessionId = session, Model = model, Text = body });
                    break;
                case "thinking":
                    var thought = Str(block, "thinking");
                    if (thought.Length > 0)
                        events.Add(new ClaudeEvent { Kind = ClaudeEventKind.Thinking, Raw = raw, SessionId = session, Model = model, Text = thought });
                    break;
                case "tool_use":
                    events.Add(new ClaudeEvent
                    {
                        Kind = ClaudeEventKind.ToolUse,
                        Raw = raw,
                        SessionId = session,
                        Model = model,
                        ToolUseId = Str(block, "id"),
                        ToolName = Str(block, "name"),
                        ToolInput = block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}",
                    });
                    break;
                case "tool_result":
                    events.Add(new ClaudeEvent
                    {
                        Kind = ClaudeEventKind.ToolResult,
                        Raw = raw,
                        SessionId = session,
                        ToolUseId = Str(block, "tool_use_id"),
                        Text = ResultContent(block),
                        IsError = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True,
                    });
                    break;
            }
        }

        return events;
    }

    private static List<ClaudeEvent> Partial(JsonElement root, string raw, string session)
    {
        if (!root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object)
            return [];
        if (Str(ev, "type") != "content_block_delta" || !ev.TryGetProperty("delta", out var delta))
            return [];
        var chunk = Str(delta, "type") switch
        {
            "text_delta" => Str(delta, "text"),
            "thinking_delta" => "",
            _ => "",
        };
        if (chunk.Length == 0)
            return [];
        return [new ClaudeEvent { Kind = ClaudeEventKind.Partial, Raw = raw, SessionId = session, Text = chunk }];
    }

    private static ClaudeEvent Result(JsonElement root, string raw, string session)
    {
        long input = 0, output = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input = Num(usage, "input_tokens") + Num(usage, "cache_creation_input_tokens") + Num(usage, "cache_read_input_tokens");
            output = Num(usage, "output_tokens");
        }

        var subtype = Str(root, "subtype");
        return new ClaudeEvent
        {
            Kind = ClaudeEventKind.Result,
            Raw = raw,
            SessionId = session,
            Subtype = subtype,
            Text = Str(root, "result"),
            IsError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True,
            CostUsd = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0,
            InputTokens = input,
            OutputTokens = output,
            DurationMs = Num(root, "duration_ms") is var d and > 0 ? d : Num(root, "duration_api_ms"),
        };
    }

    private static string RateLimitText(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limit_info", out var info) || info.ValueKind != JsonValueKind.Object)
            return "rate limit";
        var status = Str(info, "status");
        var kind = Str(info, "rateLimitType");
        return status == "allowed" ? "" : $"rate limit ({kind}): {status}";
    }

    /// <summary>A tool_result's content is a string, or a list of blocks that are usually text.</summary>
    private static string ResultContent(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
            return "";
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array)
            return content.GetRawText();
        var parts = new List<string>();
        foreach (var b in content.EnumerateArray())
        {
            if (b.ValueKind == JsonValueKind.String)
                parts.Add(b.GetString() ?? "");
            else if (b.ValueKind == JsonValueKind.Object && Str(b, "type") == "text")
                parts.Add(Str(b, "text"));
            else if (b.ValueKind == JsonValueKind.Object && Str(b, "type") == "image")
                parts.Add("[image]");
        }

        return string.Join("\n", parts);
    }

    private static string Str(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
