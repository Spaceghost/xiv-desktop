using System.Collections.Concurrent;
using XivDesktop.Core.Claude;

namespace XivDesktop.Plugin.Claude;

/// <summary>
/// One Claude Code session: its job, its rendered conversation, its NPC and what is remembered about it.
/// The transport's events land on its own thread and are queued here; <see cref="Tick"/> drains them on
/// the framework thread, so the panel only ever reads state that is not being written.
/// </summary>
public sealed class ClaudeSession
{
    private readonly ConcurrentQueue<(bool Stderr, string Line)> incoming = new();
    private readonly HashSet<int> seen = [];
    private readonly Queue<int> seenOrder = new();
    private readonly NpcDirector director = new();
    private readonly List<string> introAnswer = [];

    private bool replaying;
    private bool introPending;

    public ClaudeSession(SessionRecord record, NpcLook look)
    {
        Record = record;
        Look = look;
    }

    public SessionRecord Record { get; }

    public Conversation Conversation { get; } = new();

    public NpcLook Look { get; private set; }

    /// <summary>The agent's job id while one is running, else 0.</summary>
    public uint Job { get; private set; }

    /// <summary>The session is starting or running.</summary>
    public bool Live => Job != 0;

    /// <summary>A turn is in flight, so the stop button does something.</summary>
    public bool Busy => Conversation.Activity is SessionActivity.Working or SessionActivity.Starting or SessionActivity.Asking;

    /// <summary>The panel is open. Closing it hides the session; it keeps running.</summary>
    public bool PanelOpen { get; set; } = true;

    /// <summary>The panel's raw toggle.</summary>
    public bool ShowRaw { get; set; }

    /// <summary>The secret this session's job carries to the permission server.</summary>
    public string McpSecret { get; set; } = "";

    /// <summary>What the panel says about how permissions are being handled.</summary>
    public string PermissionNotice { get; set; } = "";

    /// <summary>The last cue the NPC was given, for the panel's "what is it doing" line.</summary>
    public NpcCue Cue { get; private set; }

    public string Key => Record.Key;

    /// <summary>Queues a line from the transport (any thread).</summary>
    public void Receive(bool stderr, string line) => incoming.Enqueue((stderr, line));

    /// <summary>The job started. <paramref name="intro"/> asks the model for its own name and look first.</summary>
    public void Started(uint job, bool intro)
    {
        Job = job;
        Record.JobId = job;
        introPending = intro;
        introAnswer.Clear();
        Conversation.Starting();
    }

    /// <summary>The job could not start, or the agent refused it.</summary>
    public void Failed(string why)
    {
        Job = 0;
        Record.JobId = 0;
        Conversation.Broken(why);
    }

    /// <summary>The job ended. The session stays in the list so it can be resumed.</summary>
    public void Ended(string reason)
    {
        Job = 0;
        Record.JobId = 0;
        introPending = false;
        Conversation.Broken(reason);
    }

    /// <summary>The connection came back: the agent replays the job's ring, so drop what is already shown.</summary>
    public void Reattaching() => replaying = true;

    /// <summary>
    /// Drains what the transport queued. Returns the cue the NPC should play, when it changed.
    /// </summary>
    public NpcCue? Tick()
    {
        var before = Conversation.Activity;
        var drained = false;
        while (incoming.TryDequeue(out var item))
        {
            drained = true;
            if (item.Stderr)
            {
                Conversation.FeedError(item.Line);
                continue;
            }

            if (replaying && AlreadySeen(item.Line))
                continue;
            replaying = false;
            Remember(item.Line);
            Consume(item.Line);
        }

        if (drained)
            Record.LastActivity = DateTimeOffset.UtcNow;
        var running = RunningTool();
        var cue = director.Cue(Conversation.Activity, running);
        Cue = cue;
        return cue.OneShot || before != Conversation.Activity ? cue : null;
    }

    /// <summary>The line the job's stdin takes for a turn.</summary>
    public string Turn(string prompt)
    {
        if (Record.Title.Length == 0)
            Record.Title = SessionHistory.TitleFrom(prompt);
        Conversation.Prompt(prompt);
        Record.LastActivity = DateTimeOffset.UtcNow;
        return ClaudeCommand.UserMessage(prompt);
    }

    /// <summary>Rerolls the NPC's look, keeping the name unless a new one is given.</summary>
    public void Reroll(uint seed, string? name = null)
    {
        Look = NpcLooks.For(Key, seed, name: name ?? Record.NpcName);
        Record.LookSeed = Look.Seed;
        Record.NpcName = Look.Name;
        Record.Style = Look.Style.ToString();
    }

    private void Consume(string line)
    {
        if (!introPending)
        {
            Conversation.Feed(line);
            return;
        }

        // The look question is not part of the conversation: only the session header is taken from it.
        foreach (var e in StreamJson.Parse(line))
        {
            switch (e.Kind)
            {
                case ClaudeEventKind.Init:
                    Conversation.Apply(e);
                    Record.SessionId = Conversation.SessionId;
                    Record.Model = Conversation.Model;
                    Record.Cwd = Conversation.Cwd;
                    break;
                case ClaudeEventKind.Text:
                    introAnswer.Add(e.Text);
                    break;
                case ClaudeEventKind.Result:
                    introPending = false;
                    Look = NpcLooks.ApplySelfDescription(Look, string.Join("\n", introAnswer));
                    Record.NpcName = Look.Name;
                    Record.Style = Look.Style.ToString();
                    Record.LookSeed = Look.Seed;
                    introAnswer.Clear();
                    Conversation.Notice($"{Look.Nameplate} — {Look.Describe()}");
                    break;
                case ClaudeEventKind.Error:
                    introPending = false;
                    break;
            }
        }
    }

    private ToolKind? RunningTool()
    {
        for (var i = Conversation.Items.Count - 1; i >= 0 && i >= Conversation.Items.Count - 8; i--)
        {
            var item = Conversation.Items[i];
            if (item.Kind == ItemKind.Tool && item.Status is ToolStatus.Running or ToolStatus.Asking)
                return item.Tool;
        }

        return null;
    }

    /// <summary>
    /// The replay is a prefix of lines already rendered: skip while each one is known, and stop skipping
    /// at the first that is not, so nothing new is ever dropped.
    /// </summary>
    private bool AlreadySeen(string line) => seen.Contains(line.GetHashCode(StringComparison.Ordinal));

    private void Remember(string line)
    {
        var hash = line.GetHashCode(StringComparison.Ordinal);
        if (!seen.Add(hash))
            return;
        seenOrder.Enqueue(hash);
        while (seenOrder.Count > Core.Claude.Conversation.MaxRawLines)
            seen.Remove(seenOrder.Dequeue());
    }

    /// <summary>Once the header is known, keep the record in step with it.</summary>
    public void SyncRecord()
    {
        if (Conversation.SessionId.Length > 0)
            Record.SessionId = Conversation.SessionId;
        if (Conversation.Model.Length > 0)
            Record.Model = Conversation.Model;
        if (Conversation.Cwd.Length > 0)
            Record.Cwd = Conversation.Cwd;
    }
}
