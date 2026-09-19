using System.Text;

namespace XivDesktop.Core.Claude;

public enum ItemKind
{
    /// <summary>What the player sent.</summary>
    Prompt,

    /// <summary>Claude's reply, streamed.</summary>
    Reply,

    /// <summary>Claude's thinking, collapsed by default.</summary>
    Thinking,

    /// <summary>A tool call: the compact card with an icon, a target and a result.</summary>
    Tool,

    /// <summary>A line from XivDesktop itself (the session started, the agent went away, a fallback notice).</summary>
    Notice,

    /// <summary>The turn's totals.</summary>
    Summary,
}

/// <summary>One row of the panel. Mutable where a row grows (streamed text, a card's result).</summary>
public sealed class ConversationItem
{
    public required ItemKind Kind { get; init; }

    /// <summary>Tool rows: the <c>toolu_…</c> id, so the result finds its card.</summary>
    public string ToolUseId { get; init; } = "";

    public string ToolName { get; init; } = "";

    public ToolKind Tool { get; init; } = ToolKind.Other;

    /// <summary>The file, command, pattern or url the card names.</summary>
    public string Target { get; set; } = "";

    /// <summary>The tool's arguments as JSON; the card expands to them and the dialog shows them.</summary>
    public string ToolInput { get; set; } = "{}";

    public ToolStatus Status { get; set; } = ToolStatus.Running;

    /// <summary>"+12 −3", "exit 0", "14 files": what the card says once it is done.</summary>
    public string ResultLine { get; set; } = "";

    /// <summary>A few lines of the result, for the expanded card.</summary>
    public IReadOnlyList<string> Detail { get; set; } = [];

    /// <summary>Prompt, reply, thinking, notice: the body. Tool rows: the whole result.</summary>
    public string Text => text.ToString();

    /// <summary>The panel's expander state (tool rows and thinking start collapsed).</summary>
    public bool Expanded { get; set; }

    /// <summary>Notices: true for a warning.</summary>
    public bool IsError { get; init; }

    /// <summary>When it appeared, for the "…s ago" on resumed sessions.</summary>
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    private readonly StringBuilder text = new();

    public void Append(string chunk) => text.Append(chunk);

    public void Set(string value)
    {
        text.Clear();
        text.Append(value);
    }

    public int Length => text.Length;
}

/// <summary>The session's totals, as the last <c>result</c> line reported them.</summary>
public sealed record SessionTotals(double CostUsd, long InputTokens, long OutputTokens, long DurationMs)
{
    public static readonly SessionTotals Zero = new(0, 0, 0, 0);

    public SessionTotals Add(ClaudeEvent e) => new(
        CostUsd + e.CostUsd,
        InputTokens + e.InputTokens,
        OutputTokens + e.OutputTokens,
        DurationMs + e.DurationMs);

    /// <summary>"12.3k in · 1.1k out · $0.03", or "" while nothing has been counted.</summary>
    public string Summary()
    {
        if (InputTokens == 0 && OutputTokens == 0 && CostUsd == 0)
            return "";
        var parts = new List<string>(3);
        if (InputTokens > 0)
            parts.Add($"{Compact(InputTokens)} in");
        if (OutputTokens > 0)
            parts.Add($"{Compact(OutputTokens)} out");
        if (CostUsd > 0)
            parts.Add($"${CostUsd:0.####}");
        return string.Join(" · ", parts);
    }

    private static string Compact(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000 => $"{n / 1_000.0:0.#}k",
        _ => n.ToString(),
    };
}

/// <summary>What the session as a whole is doing; the NPC and the panel header both read it.</summary>
public enum SessionActivity
{
    /// <summary>No job yet, or the job ended.</summary>
    Idle,

    /// <summary>The job is starting and has not said <c>init</c> yet.</summary>
    Starting,

    /// <summary>Claude is working: thinking, writing, running tools.</summary>
    Working,

    /// <summary>A permission dialog is up: it needs the player.</summary>
    Asking,

    /// <summary>The turn finished; waiting for the next prompt.</summary>
    Waiting,

    /// <summary>The job failed or the agent went away.</summary>
    Broken,
}

/// <summary>
/// The rendered conversation: stream-json events in, panel rows out. Pure — no Dalamud, no sockets, no
/// clock beyond <see cref="DateTimeOffset.UtcNow"/> — so the panel's whole behaviour is testable.
/// </summary>
public sealed class Conversation
{
    /// <summary>Rows older than this are dropped from the front; the raw log keeps its own limit.</summary>
    public const int MaxItems = 400;

    /// <summary>Raw lines kept for the "raw" toggle.</summary>
    public const int MaxRawLines = 2000;

    private readonly List<ConversationItem> items = [];
    private readonly List<string> raw = [];
    private readonly Dictionary<string, ConversationItem> cards = new(StringComparer.Ordinal);
    private ConversationItem? streaming;

    public IReadOnlyList<ConversationItem> Items => items;

    /// <summary>Every stream-json line, exactly as the job printed it: the panel's raw toggle.</summary>
    public IReadOnlyList<string> Raw => raw;

    public string SessionId { get; private set; } = "";

    public string Model { get; private set; } = "";

    public string Cwd { get; private set; } = "";

    public string PermissionMode { get; private set; } = "";

    public SessionTotals Totals { get; private set; } = SessionTotals.Zero;

    public SessionActivity Activity { get; private set; } = SessionActivity.Idle;

    /// <summary>The last row, for the panel's auto-scroll and the NPC's "looks up" moment.</summary>
    public ConversationItem? Last => items.Count > 0 ? items[^1] : null;

    /// <summary>Tool cards still waiting for a result.</summary>
    public int Pending => cards.Count;

    public void Prompt(string text)
    {
        streaming = null;
        Add(new ConversationItem { Kind = ItemKind.Prompt });
        items[^1].Set(text);
        Activity = SessionActivity.Working;
    }

    public void Notice(string text, bool error = false)
    {
        streaming = null;
        var item = new ConversationItem { Kind = ItemKind.Notice, IsError = error };
        item.Set(text);
        Add(item);
    }

    public void Starting() => Activity = SessionActivity.Starting;

    public void Broken(string why)
    {
        Activity = SessionActivity.Broken;
        foreach (var card in cards.Values)
        {
            if (card.Status is ToolStatus.Running or ToolStatus.Asking)
            {
                card.Status = ToolStatus.Failed;
                card.ResultLine = "interrupted";
            }
        }

        cards.Clear();
        if (why.Length > 0)
            Notice(why, error: true);
    }

    /// <summary>One line of the job's stdout. Everything the panel shows comes through here.</summary>
    public void Feed(string line)
    {
        AddRaw(line);
        foreach (var e in StreamJson.Parse(line))
            Apply(e);
    }

    /// <summary>A line of the job's stderr: kept raw and shown as a warning notice.</summary>
    public void FeedError(string line)
    {
        if (line.Trim().Length == 0)
            return;
        AddRaw("stderr: " + line);
        Notice(ToolCards.Shorten(line.Trim(), 200), error: true);
    }

    public void Apply(ClaudeEvent e)
    {
        switch (e.Kind)
        {
            case ClaudeEventKind.Init:
                SessionId = e.SessionId.Length > 0 ? e.SessionId : SessionId;
                Model = e.Model.Length > 0 ? e.Model : Model;
                Cwd = e.Cwd.Length > 0 ? e.Cwd : Cwd;
                PermissionMode = e.PermissionMode;
                Activity = SessionActivity.Waiting;
                break;

            case ClaudeEventKind.Partial:
                Stream(e.Text);
                Activity = SessionActivity.Working;
                break;

            case ClaudeEventKind.Text:
                // With --include-partial-messages the finished block repeats what the deltas already said.
                if (streaming is { } open && open.Text.EndsWith(e.Text, StringComparison.Ordinal))
                {
                    streaming = null;
                    break;
                }

                streaming = null;
                var reply = new ConversationItem { Kind = ItemKind.Reply };
                reply.Set(e.Text);
                Add(reply);
                Activity = SessionActivity.Working;
                break;

            case ClaudeEventKind.Thinking:
                streaming = null;
                var thought = new ConversationItem { Kind = ItemKind.Thinking };
                thought.Set(e.Text);
                Add(thought);
                Activity = SessionActivity.Working;
                break;

            case ClaudeEventKind.ToolUse:
                streaming = null;
                var card = new ConversationItem
                {
                    Kind = ItemKind.Tool,
                    ToolUseId = e.ToolUseId,
                    ToolName = e.ToolName,
                    Tool = ToolCards.KindOf(e.ToolName),
                    Target = ToolCards.Target(e.ToolName, e.ToolInput),
                    ToolInput = e.ToolInput,
                    Status = ToolStatus.Running,
                };
                Add(card);
                if (e.ToolUseId.Length > 0)
                    cards[e.ToolUseId] = card;
                Activity = SessionActivity.Working;
                break;

            case ClaudeEventKind.ToolResult:
                if (e.ToolUseId.Length > 0 && cards.Remove(e.ToolUseId, out var target))
                {
                    target.Status = e.IsError ? ToolStatus.Failed : ToolStatus.Done;
                    target.ResultLine = ToolCards.ResultLine(target.ToolName, target.ToolInput, e.Text, e.IsError);
                    target.Detail = ToolCards.Detail(e.Text);
                    target.Set(e.Text);
                }

                break;

            case ClaudeEventKind.Result:
                streaming = null;
                Totals = Totals.Add(e);
                Activity = e.IsError ? SessionActivity.Broken : SessionActivity.Waiting;
                var summary = new ConversationItem { Kind = ItemKind.Summary, IsError = e.IsError };
                summary.Set(Totals.Summary());
                Add(summary);
                break;

            case ClaudeEventKind.RateLimit or ClaudeEventKind.System:
                if (e.Text.Length > 0)
                    Notice(e.Text);
                break;

            case ClaudeEventKind.Error:
                Notice(e.Text.Length > 0 ? e.Text : "the session reported an error", error: true);
                Activity = SessionActivity.Broken;
                break;
        }
    }

    /// <summary>The permission dialog opened or closed for a card.</summary>
    public void Asking(string toolUseId, bool asking)
    {
        if (toolUseId.Length > 0 && cards.TryGetValue(toolUseId, out var card))
            card.Status = asking ? ToolStatus.Asking : ToolStatus.Running;
        Activity = asking ? SessionActivity.Asking : SessionActivity.Working;
    }

    /// <summary>A tool the player denied: the card says so even before the result arrives.</summary>
    public void Denied(string toolUseId, string reason)
    {
        if (toolUseId.Length > 0 && cards.TryGetValue(toolUseId, out var card))
        {
            card.Status = ToolStatus.Denied;
            card.ResultLine = reason.Length > 0 ? ToolCards.Shorten(reason, 60) : "denied";
        }

        Activity = SessionActivity.Working;
    }

    /// <summary>The whole conversation as a terminal would have printed it (the raw toggle's text).</summary>
    public string RawText() => string.Join("\n", raw);

    private void Stream(string chunk)
    {
        if (streaming == null)
        {
            streaming = new ConversationItem { Kind = ItemKind.Reply };
            Add(streaming);
        }

        streaming.Append(chunk);
    }

    private void Add(ConversationItem item)
    {
        items.Add(item);
        if (items.Count <= MaxItems)
            return;
        var drop = items.Count - MaxItems;
        for (var i = 0; i < drop; i++)
        {
            if (items[i].ToolUseId.Length > 0)
                cards.Remove(items[i].ToolUseId);
        }

        items.RemoveRange(0, drop);
    }

    private void AddRaw(string line)
    {
        raw.Add(line);
        if (raw.Count > MaxRawLines)
            raw.RemoveRange(0, raw.Count - MaxRawLines);
    }
}
