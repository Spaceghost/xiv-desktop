using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivDesktop.Core.Claude;

/// <summary>What the player chose in the permission dialog.</summary>
public enum PermissionChoice
{
    /// <summary>Not answered yet.</summary>
    None,

    /// <summary>This call only.</summary>
    Once,

    /// <summary>This tool, for the rest of this session.</summary>
    Session,

    /// <summary>This tool, in every session, until the setting is cleared.</summary>
    Always,

    Deny,
}

/// <summary>
/// One permission prompt, as XivDesktop's MCP tool received it. The shape is Claude Code's own:
/// <c>{"tool_name": "Write", "input": {…}, "tool_use_id": "toolu_…"}</c> — confirmed against
/// <c>claude 2.1.278</c> by serving the tool and watching the call.
/// </summary>
public sealed record PermissionRequest
{
    public required string ToolName { get; init; }

    /// <summary>The tool's arguments, as JSON; the dialog lists them.</summary>
    public string InputJson { get; init; } = "{}";

    public string ToolUseId { get; init; } = "";

    /// <summary>Which session asked; the dialog says so when several Claudes are up.</summary>
    public string SessionKey { get; init; } = "";

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    public ToolKind Kind => ToolCards.KindOf(ToolName);

    /// <summary>The one-line "what it wants to do".</summary>
    public string Target => ToolCards.Target(ToolName, InputJson, 90);

    /// <summary>The arguments as "name: value" rows for the dialog.</summary>
    public IReadOnlyList<(string Name, string Value)> Arguments(int max = 8, int width = 120)
    {
        var rows = new List<(string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(InputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return [("input", ToolCards.Shorten(InputJson, width))];
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (rows.Count == max)
                {
                    rows.Add(("…", "more arguments"));
                    break;
                }

                var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
                rows.Add((p.Name, ToolCards.Shorten(value.ReplaceLineEndings(" ⏎ "), width)));
            }
        }
        catch (JsonException)
        {
            rows.Add(("input", ToolCards.Shorten(InputJson, width)));
        }

        return rows;
    }
}

/// <summary>The answer given back to Claude, in the shape its permission prompt tool must return.</summary>
public sealed record PermissionDecision(bool Allow, string Message = "", string UpdatedInputJson = "")
{
    /// <summary>
    /// The MCP text content Claude parses:
    /// <c>{"behavior":"allow","updatedInput":{…}}</c> or <c>{"behavior":"deny","message":"…"}</c>.
    /// </summary>
    public string ToToolResultText(string originalInputJson)
    {
        if (!Allow)
        {
            return new JsonObject
            {
                ["behavior"] = "deny",
                ["message"] = Message.Length > 0 ? Message : "Denied in game.",
            }.ToJsonString();
        }

        var input = UpdatedInputJson.Length > 0 ? UpdatedInputJson : originalInputJson;
        JsonNode? updated;
        try
        {
            updated = JsonNode.Parse(input.Length > 0 ? input : "{}");
        }
        catch (JsonException)
        {
            updated = new JsonObject();
        }

        return new JsonObject
        {
            ["behavior"] = "allow",
            ["updatedInput"] = updated ?? new JsonObject(),
        }.ToJsonString();
    }
}

/// <summary>
/// Remembers "allow for this session" and "always allow this tool". Nothing is ever added here without
/// the player choosing it: there is no rule that auto-approves by default.
/// </summary>
public sealed class PermissionRules
{
    private readonly HashSet<string> session = new(StringComparer.Ordinal);
    private readonly HashSet<string> always;

    public PermissionRules(IEnumerable<string>? persisted = null)
        => always = new HashSet<string>(persisted ?? [], StringComparer.Ordinal);

    /// <summary>The tools the player chose "always" for; persisted in the plugin configuration.</summary>
    public IReadOnlyCollection<string> Always => always;

    public bool IsAllowed(string toolName, string sessionKey)
        => always.Contains(toolName) || session.Contains(Key(toolName, sessionKey));

    public void Remember(string toolName, string sessionKey, PermissionChoice choice)
    {
        switch (choice)
        {
            case PermissionChoice.Session:
                session.Add(Key(toolName, sessionKey));
                break;
            case PermissionChoice.Always:
                always.Add(toolName);
                break;
        }
    }

    public void ForgetSession(string sessionKey)
        => session.RemoveWhere(k => k.StartsWith(sessionKey + "\u0000", StringComparison.Ordinal));

    public void ClearAlways() => always.Clear();

    private static string Key(string toolName, string sessionKey) => sessionKey + "\u0000" + toolName;
}

/// <summary>
/// The dialog's state: at most one prompt is shown, the rest queue behind it. A request whose tool the
/// player already allowed is answered without a dialog; everything else waits for a choice, and a prompt
/// nobody answers within the timeout is denied rather than left hanging (Claude would wait forever).
/// </summary>
public sealed class PermissionQueue
{
    /// <summary>How long a prompt waits for the player before it is denied.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly PermissionRules rules;
    private readonly Queue<Pending> waiting = new();
    private readonly Lock gate = new();
    private Pending? current;

    public PermissionQueue(PermissionRules rules) => this.rules = rules;

    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>The prompt the dialog is showing, or null.</summary>
    public PermissionRequest? Current
    {
        get
        {
            lock (gate)
                return current?.Request;
        }
    }

    public int Waiting
    {
        get
        {
            lock (gate)
                return waiting.Count;
        }
    }

    /// <summary>
    /// Takes a request from the MCP tool. Returns the decision straight away when a rule already covers
    /// it; otherwise queues it and returns null — the caller waits on <see cref="Await"/>.
    /// </summary>
    public PermissionDecision? Submit(PermissionRequest request, out Guid ticket)
    {
        ticket = Guid.Empty;
        if (rules.IsAllowed(request.ToolName, request.SessionKey))
            return new PermissionDecision(true);

        var pending = new Pending(request);
        ticket = pending.Ticket;
        lock (gate)
        {
            if (current == null)
                current = pending;
            else
                waiting.Enqueue(pending);
        }

        return null;
    }

    /// <summary>Blocks the MCP request thread until the player answers, or the prompt times out.</summary>
    public PermissionDecision Await(Guid ticket, CancellationToken cancel = default)
    {
        var pending = Find(ticket);
        if (pending == null)
            return new PermissionDecision(false, "The prompt went away.");
        if (!pending.Done.Wait(Timeout, cancel))
        {
            Answer(ticket, PermissionChoice.Deny, "Nobody answered in game.");
            return pending.Decision ?? new PermissionDecision(false, "Nobody answered in game.");
        }

        return pending.Decision ?? new PermissionDecision(false, "Denied in game.");
    }

    /// <summary>The player chose. Advances to the next queued prompt.</summary>
    public void Answer(Guid ticket, PermissionChoice choice, string message = "")
    {
        Pending? pending;
        lock (gate)
        {
            pending = current?.Ticket == ticket ? current : waiting.FirstOrDefault(p => p.Ticket == ticket);
            if (pending == null)
                return;
            if (current == pending)
                current = waiting.Count > 0 ? waiting.Dequeue() : null;
            else
                Remove(pending);
        }

        if (choice != PermissionChoice.None && choice != PermissionChoice.Deny)
            rules.Remember(pending.Request.ToolName, pending.Request.SessionKey, choice);
        pending.Decision = choice == PermissionChoice.Deny || choice == PermissionChoice.None
            ? new PermissionDecision(false, message.Length > 0 ? message : "Denied in game.")
            : new PermissionDecision(true);
        pending.Done.Set();
    }

    /// <summary>Answers the prompt on screen.</summary>
    public void AnswerCurrent(PermissionChoice choice, string message = "")
    {
        var ticket = Current is null ? Guid.Empty : CurrentTicket();
        if (ticket != Guid.Empty)
            Answer(ticket, choice, message);
    }

    /// <summary>Denies everything queued for a session that is going away.</summary>
    public void CancelSession(string sessionKey, string why)
    {
        List<Pending> doomed;
        lock (gate)
        {
            doomed = waiting.Where(p => p.Request.SessionKey == sessionKey).ToList();
            if (current is { } c && c.Request.SessionKey == sessionKey)
                doomed.Add(c);
        }

        foreach (var p in doomed)
            Answer(p.Ticket, PermissionChoice.Deny, why);
    }

    public Guid CurrentTicket()
    {
        lock (gate)
            return current?.Ticket ?? Guid.Empty;
    }

    private Pending? Find(Guid ticket)
    {
        lock (gate)
            return current?.Ticket == ticket ? current : waiting.FirstOrDefault(p => p.Ticket == ticket);
    }

    private void Remove(Pending pending)
    {
        var kept = waiting.Where(p => p != pending).ToList();
        waiting.Clear();
        foreach (var p in kept)
            waiting.Enqueue(p);
    }

    private sealed class Pending(PermissionRequest request)
    {
        public Guid Ticket { get; } = Guid.NewGuid();

        public PermissionRequest Request { get; } = request;

        public ManualResetEventSlim Done { get; } = new(false);

        public PermissionDecision? Decision { get; set; }
    }
}
