using System.Text;
using System.Text.Json;

namespace XivDesktop.Core.Ask;

/// <summary>One thing a streamed chat response said.</summary>
public readonly record struct StreamEvent(StreamEventKind Kind, string Text)
{
    public static StreamEvent Delta(string text) => new(StreamEventKind.Delta, text);

    public static StreamEvent Done() => new(StreamEventKind.Done, "");

    public static StreamEvent Error(string text) => new(StreamEventKind.Error, text);
}

public enum StreamEventKind
{
    /// <summary>More answer text.</summary>
    Delta,

    /// <summary>The answer is complete ([DONE], a finish_reason, or Anthropic's message_stop).</summary>
    Done,

    /// <summary>The server reported an error inside the stream.</summary>
    Error,
}

/// <summary>
/// Incremental Server-Sent Events parser for chat completions. Feed it the bytes as they arrive, in any
/// chunking; it yields answer deltas. Understands the OpenAI chat-completions chunk shape
/// (<c>choices[0].delta.content</c>, <c>[DONE]</c>) that almanac's gateway passes through from Ollama, and
/// Anthropic's <c>content_block_delta</c> / <c>message_stop</c> so either gateway route works. Reasoning
/// ("thinking") deltas are ignored: only the spoken answer is returned. Not thread-safe; one per response.
/// </summary>
public sealed class ChatStreamParser
{
    private readonly StringBuilder line = new();
    private readonly StringBuilder data = new();
    private readonly Decoder utf8 = Encoding.UTF8.GetDecoder();
    private readonly char[] chars = new char[4096];

    /// <summary>A Done event was produced; later input is ignored.</summary>
    public bool Finished { get; private set; }

    public IEnumerable<StreamEvent> Feed(ReadOnlySpan<byte> bytes)
    {
        var events = new List<StreamEvent>();
        while (!bytes.IsEmpty)
        {
            var take = Math.Min(bytes.Length, 1024);
            var n = utf8.GetChars(bytes[..take], chars, flush: false);
            bytes = bytes[take..];
            Feed(chars.AsSpan(0, n), events);
        }

        return events;
    }

    public IEnumerable<StreamEvent> Feed(string text)
    {
        var events = new List<StreamEvent>();
        Feed(text.AsSpan(), events);
        return events;
    }

    /// <summary>End of the HTTP body: flushes a last event that had no blank line after it.</summary>
    public IEnumerable<StreamEvent> Complete()
    {
        var events = new List<StreamEvent>();
        if (line.Length > 0)
        {
            TakeLine(events);
        }

        Dispatch(events);
        if (!Finished)
        {
            Finished = true;
            events.Add(StreamEvent.Done());
        }

        return events;
    }

    private void Feed(ReadOnlySpan<char> text, List<StreamEvent> events)
    {
        foreach (var c in text)
        {
            if (c == '\n')
                TakeLine(events);
            else if (c != '\r')
                line.Append(c);
        }
    }

    private void TakeLine(List<StreamEvent> events)
    {
        var l = line.ToString();
        line.Clear();
        if (l.Length == 0)
        {
            Dispatch(events);
            return;
        }

        if (l.StartsWith(':'))
            return; // SSE comment / keep-alive
        if (l.StartsWith("data:", StringComparison.Ordinal))
        {
            var v = l[5..];
            if (v.StartsWith(' '))
                v = v[1..];
            if (data.Length > 0)
                data.Append('\n');
            data.Append(v);
        }

        // "event:", "id:", "retry:" carry nothing we need: the JSON says what it is.
    }

    private void Dispatch(List<StreamEvent> events)
    {
        if (data.Length == 0)
            return;
        var payload = data.ToString();
        data.Clear();
        if (Finished)
            return;
        foreach (var e in ParsePayload(payload))
        {
            events.Add(e);
            if (e.Kind is StreamEventKind.Done or StreamEventKind.Error)
            {
                Finished = true;
                return;
            }
        }
    }

    /// <summary>One SSE data payload → events. Malformed JSON is skipped, never thrown.</summary>
    public static IEnumerable<StreamEvent> ParsePayload(string payload)
    {
        if (payload.Trim() == "[DONE]")
            return [StreamEvent.Done()];
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return Parse(doc.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<StreamEvent> Parse(JsonElement root)
    {
        var events = new List<StreamEvent>();
        if (root.ValueKind != JsonValueKind.Object)
            return events;

        if (root.TryGetProperty("error", out var err))
        {
            var msg = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) ? m.GetString() : err.ToString();
            events.Add(StreamEvent.Error(string.IsNullOrWhiteSpace(msg) ? "the model reported an error" : msg!));
            return events;
        }

        // OpenAI chat.completion.chunk
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                    && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var s = content.GetString();
                    if (!string.IsNullOrEmpty(s))
                        events.Add(StreamEvent.Delta(s));
                }

                // A non-streamed reply (stream=false) has message.content instead.
                if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("content", out var mc) && mc.ValueKind == JsonValueKind.String)
                {
                    var s = mc.GetString();
                    if (!string.IsNullOrEmpty(s))
                        events.Add(StreamEvent.Delta(s));
                }

                if (choice.TryGetProperty("finish_reason", out var fin) && fin.ValueKind == JsonValueKind.String)
                    events.Add(StreamEvent.Done());
            }

            return events;
        }

        // Anthropic messages stream
        if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            switch (type.GetString())
            {
                case "content_block_delta"
                    when root.TryGetProperty("delta", out var d)
                         && d.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta"
                         && d.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String:
                    var s = t.GetString();
                    if (!string.IsNullOrEmpty(s))
                        events.Add(StreamEvent.Delta(s));
                    break;
                case "message_stop":
                    events.Add(StreamEvent.Done());
                    break;
            }
        }

        return events;
    }
}
