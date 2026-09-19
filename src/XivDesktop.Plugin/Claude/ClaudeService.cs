using Dalamud.Plugin.Services;
using XivDesktop.Core;
using XivDesktop.Core.Claude;

namespace XivDesktop.Plugin.Claude;

/// <summary>
/// Owns the Claude sessions: the agent connection, the permission server, the rules, the NPCs and what
/// is remembered between game sessions. Every public method returns "ok: …" or "error: …", like the
/// other XivDesktop services, and every one of them runs on the framework thread.
/// </summary>
public sealed class ClaudeService : IDisposable
{
    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly Configuration config;
    private readonly Action saveConfig;
    private readonly HostPaths paths;
    private readonly Func<string> home;
    private readonly Dictionary<string, ClaudeSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> order = [];

    private INpcAvatar avatar = NoNpcAvatar.Instance;
    private bool warnedBusy;

    public ClaudeService(
        IPluginLog log,
        IFramework framework,
        Configuration config,
        Action saveConfig,
        HostPaths paths,
        Func<string> home,
        IJobTransport? transport = null,
        McpHttpServer? mcp = null)
    {
        this.log = log;
        this.framework = framework;
        this.config = config;
        this.saveConfig = saveConfig;
        this.paths = paths;
        this.home = home;

        Rules = new PermissionRules(config.ClaudeAlwaysAllow);
        Prompts = new PermissionQueue(Rules);
        Mcp = mcp ?? new McpHttpServer(log, Ask);
        Transport = transport ?? new AgentConnection(log, paths, Endpoint, () => config.ClaudeEnabled);

        Transport.Line += OnLine;
        Transport.Ended += OnEnded;
        Transport.Reconnected += OnReconnected;
        framework.Update += OnUpdate;
    }

    public IJobTransport Transport { get; }

    public McpHttpServer Mcp { get; }

    public PermissionRules Rules { get; }

    public PermissionQueue Prompts { get; }

    /// <summary>The live and recently closed sessions, in the order they were opened.</summary>
    public IReadOnlyList<ClaudeSession> Sessions => order.Where(sessions.ContainsKey).Select(k => sessions[k]).ToList();

    /// <summary>The session the panel is showing, or null.</summary>
    public ClaudeSession? Active { get; private set; }

    /// <summary>The remembered sessions, newest first, for <c>/claude resume</c>.</summary>
    public IReadOnlyList<SessionRecord> Remembered => SessionHistory.Sorted(config.ClaudeSessions);

    /// <summary>Set once something can actually put an NPC in the world.</summary>
    public void UseAvatar(INpcAvatar npcAvatar) => avatar = npcAvatar;

    public bool NpcAvailable => avatar.Available;

    /// <summary>Why a session has no avatar, when it has none ("" when all is well).</summary>
    public string NpcNote => avatar.Note;

    /// <summary>Whether a permission prompt can be shown in game at all.</summary>
    public bool PromptToolAvailable => Mcp.Running && Mcp.Url.Length > 0;

    /// <summary>The header line the panel shows about permissions.</summary>
    public string PermissionSummary => PromptToolAvailable
        ? "Permission prompts are answered in game."
        : $"Permission prompts cannot be shown ({(Mcp.Problem.Length > 0 ? Mcp.Problem : "the prompt server is not running")}); "
          + $"running with --permission-mode {config.ClaudePermissionMode} and everything else denied.";

    public string Status()
    {
        if (!config.ClaudeEnabled)
            return "error: /claude is turned off in the settings";
        if (!Transport.Ready)
            return "error: " + (Transport.Problem.Length > 0 ? Transport.Problem : "not connected to ghostty-agent");
        var live = Sessions.Count(s => s.Live);
        return $"ok: connected to ghostty-agent; {live} live session{(live == 1 ? "" : "s")}, {Remembered.Count} remembered";
    }

    /// <summary>Opens or focuses a session's panel, starting one if there is none.</summary>
    public string Focus(string? name = null)
    {
        var session = Resolve(name);
        if (session == null)
            return New(name ?? "", "");
        Active = session;
        session.PanelOpen = true;
        return "ok";
    }

    /// <summary>
    /// Starts a session. <paramref name="name"/> may be empty; <paramref name="prompt"/> is sent as the
    /// first turn once the job is up.
    /// </summary>
    public string New(string name, string prompt, string? resumeSessionId = null)
    {
        if (!config.ClaudeEnabled)
            return "error: /claude is turned off in the settings";
        if (!Transport.Ready)
            return "error: " + (Transport.Problem.Length > 0 ? Transport.Problem : "not connected to ghostty-agent");
        if (Sessions.Count(s => s.Live) >= SessionHistory.MaxLive)
            return $"error: {SessionHistory.MaxLive} Claude sessions are already open; end one first";

        var key = SessionHistory.UniqueName(Sessions.Select(s => s.Record), name);
        var cwd = WorkingDirectory();
        var record = new SessionRecord
        {
            Name = key,
            Cwd = cwd,
            Model = config.ClaudeModel,
            SessionId = resumeSessionId ?? "",
            Title = prompt.Length > 0 ? SessionHistory.TitleFrom(prompt) : "",
        };
        var session = new ClaudeSession(record, NpcLooks.For(key));
        session.Reroll(NpcLooks.Hash(key));
        sessions[key] = session;
        order.Add(key);
        Active = session;

        var started = Start(session, resumeSessionId);
        if (started.StartsWith("error", StringComparison.Ordinal))
            return started;

        if (prompt.Length > 0)
            PendingPrompt[key] = prompt;
        Remember(record);
        return $"ok: {key}";
    }

    /// <summary>Prompts waiting for their job to report <c>init</c>.</summary>
    private Dictionary<string, string> PendingPrompt { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resumes a remembered session by name or id.</summary>
    public string Resume(string nameOrId)
    {
        var record = SessionHistory.Find(config.ClaudeSessions, nameOrId);
        if (record == null)
            return $"error: no remembered session matches \"{nameOrId}\"";
        if (sessions.TryGetValue(record.Key, out var open) && open.Live)
        {
            Active = open;
            open.PanelOpen = true;
            return $"ok: {record.Key} is already open";
        }

        if (record.SessionId.Length == 0)
            return $"error: {record.Key} has no session id to resume";
        return New(record.Name, "", record.SessionId);
    }

    /// <summary>Sends a turn to a session.</summary>
    public string Send(string? name, string prompt)
    {
        if (prompt.Trim().Length == 0)
            return "error: nothing to send";
        var session = Resolve(name);
        if (session == null)
            return New(name ?? "", prompt);
        if (!session.Live)
            return $"error: {session.Key} is not running; /claude resume {session.Key}";
        Active = session;
        session.PanelOpen = true;
        Transport.Send(session.Job, session.Turn(prompt));
        Remember(session.Record);
        return "ok";
    }

    /// <summary>Interrupts the turn in flight without ending the session.</summary>
    public string Stop(string? name = null)
    {
        var session = Resolve(name);
        if (session is not { Live: true })
            return "error: nothing is running";
        Transport.Interrupt(session.Job);
        session.Conversation.Notice("interrupted");
        Prompts.CancelSession(session.Key, "The player interrupted it.");
        return "ok: interrupted";
    }

    /// <summary>
    /// Ends a session for good: the job is killed, the NPC is dismissed and the secret stops working.
    /// Closing the panel does not do this — it only hides the session.
    /// </summary>
    public string End(string? name = null)
    {
        var session = Resolve(name);
        if (session == null)
            return "error: no such session";
        if (session.Live)
            Transport.Close(session.Job);
        Prompts.CancelSession(session.Key, "The session ended.");
        Mcp.Unregister(session.Key);
        Rules.ForgetSession(session.Key);
        avatar.Dismiss(session.Key);
        session.Ended("session ended");
        session.PanelOpen = false;
        sessions.Remove(session.Key);
        order.Remove(session.Key);
        if (Active == session)
            Active = Sessions.LastOrDefault();
        Remember(session.Record);
        return $"ok: {session.Key} ended";
    }

    /// <summary>Rerolls a session's NPC look, and re-summons it.</summary>
    public string Look(string? name, uint seed)
    {
        var session = Resolve(name);
        if (session == null)
            return "error: no such session";
        session.Reroll(seed != 0 ? seed : NpcLooks.Hash(Guid.NewGuid().ToString()));
        Summon(session);
        Remember(session.Record);
        return $"ok: {session.Look.Nameplate} — {session.Look.Describe()}";
    }

    /// <summary>The working directories a session could run in: home, then the repositories under it.</summary>
    public List<WorkingDirectoryChoice> Directories()
    {
        var root = home();
        return RepoPicker.Scan(
            root,
            directory => Directory.EnumerateDirectories(paths.ToLocal(directory)).Select(p => directory.TrimEnd('/') + "/" + Path.GetFileName(p)),
            path => File.Exists(paths.ToLocal(path)) || Directory.Exists(paths.ToLocal(path)));
    }

    /// <summary>The player's Linux home directory ("" when it could not be determined).</summary>
    public string Home => home();

    /// <summary>The Linux directory a new session starts in.</summary>
    public string WorkingDirectory()
        => RepoPicker.Resolve(config.ClaudeWorkingDirectory, home(), path => Directory.Exists(paths.ToLocal(path)));

    /// <summary>Answers the prompt on screen.</summary>
    public void Answer(PermissionChoice choice)
    {
        var request = Prompts.Current;
        Prompts.AnswerCurrent(choice, choice == PermissionChoice.Deny ? "The player denied it in game." : "");
        if (request == null)
            return;
        if (sessions.TryGetValue(request.SessionKey, out var session))
        {
            if (choice == PermissionChoice.Deny)
                session.Conversation.Denied(request.ToolUseId, "denied in game");
            else
                session.Conversation.Asking(request.ToolUseId, false);
        }

        if (choice == PermissionChoice.Always)
        {
            config.ClaudeAlwaysAllow = [.. Rules.Always];
            saveConfig();
        }
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        Transport.Line -= OnLine;
        Transport.Ended -= OnEnded;
        Transport.Reconnected -= OnReconnected;
        foreach (var session in Sessions)
        {
            // Jobs are opened with keep: the sessions survive an unload and are re-attached next time.
            Prompts.CancelSession(session.Key, "XivDesktop is unloading.");
            avatar.Dismiss(session.Key);
            Remember(session.Record);
        }

        Transport.Dispose();
        Mcp.Dispose();
    }

    private AgentEndpoint Endpoint()
        => AgentEndpoint.Resolve(paths, config.ClaudeAgentHost, config.ClaudeAgentPort, config.ClaudeTokenPath);

    private ClaudeSession? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Active ?? Sessions.LastOrDefault();
        return sessions.TryGetValue(name.Trim(), out var session) ? session : null;
    }

    private string Start(ClaudeSession session, string? resume)
    {
        var secret = PromptToolAvailable ? Mcp.Register(session.Key) : "";
        session.McpSecret = secret;
        session.PermissionNotice = PermissionSummary;

        var launch = new ClaudeLaunch
        {
            WorkingDirectory = session.Record.Cwd,
            Executable = config.ClaudeExecutable,
            Model = config.ClaudeModel,
            Name = session.Key,
            ResumeSessionId = resume ?? "",
            Route = PromptToolAvailable ? PermissionRoute.PromptTool : PermissionRoute.Mode,
            McpUrl = Mcp.Url,
            McpAuth = secret,
            PermissionMode = config.ClaudePermissionMode,
            StreamPartials = config.ClaudeStreamPartials,
            ExtraArgs = config.ClaudeExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };

        var intro = config.ClaudeAskForLook && resume == null;
        Transport.Open(session.Record.Cwd, ClaudeCommand.Argv(launch), [], (job, error) =>
            framework.RunOnFrameworkThread(() =>
            {
                if (job == 0)
                {
                    session.Failed(error.Length > 0 ? error : "the agent refused to start the session");
                    Mcp.Unregister(session.Key);
                    return;
                }

                session.Started(job, intro);
                session.Conversation.Notice(session.PermissionNotice);
                if (intro)
                    Transport.Send(job, ClaudeCommand.UserMessage(NpcLooks.SelfPrompt));
                Summon(session);
            }));

        return "ok";
    }

    private void Summon(ClaudeSession session)
    {
        if (!config.ClaudeNpc || !avatar.Available)
            return;
        if (!avatar.Summon(session.Key, session.Look))
            session.Conversation.Notice("the NPC could not be summoned here", error: true);
    }

    private PermissionDecision Ask(PermissionRequest request)
    {
        var decision = Prompts.Submit(request, out var ticket);
        if (decision != null)
            return decision;

        // Tell the panel and the NPC that it is waiting, then block this HTTP thread until it is answered.
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (sessions.TryGetValue(request.SessionKey, out var session))
                session.Conversation.Asking(request.ToolUseId, true);
        });

        return Prompts.Await(ticket);
    }

    private void OnLine(uint job, bool stderr, string line)
    {
        foreach (var session in Sessions)
        {
            if (session.Job == job)
            {
                session.Receive(stderr, line);
                return;
            }
        }
    }

    private void OnEnded(JobEnded ended) => _ = framework.RunOnFrameworkThread(() =>
    {
        foreach (var session in Sessions)
        {
            if (session.Job != ended.Job)
                continue;
            session.Ended(ended.Reason);
            Prompts.CancelSession(session.Key, "The session ended.");
            Mcp.Unregister(session.Key);
            avatar.Dismiss(session.Key);
            Remember(session.Record);
            return;
        }
    });

    /// <summary>The agent came back: re-attach every job the plugin still has, and replay what was missed.</summary>
    private void OnReconnected() => _ = framework.RunOnFrameworkThread(() =>
    {
        foreach (var session in Sessions)
        {
            if (session.Job == 0)
                continue;
            session.Reattaching();
            Transport.Attach(session.Job);
            session.Conversation.Notice("re-attached to the session");
        }
    });

    private void OnUpdate(IFramework _)
    {
        var busy = 0;
        foreach (var session in Sessions)
        {
            var cue = session.Tick();
            session.SyncRecord();
            if (cue is { } played && config.ClaudeNpc && avatar.Available)
                avatar.Play(session.Key, played);
            if (session.Busy)
                busy++;
            if (PendingPrompt.TryGetValue(session.Key, out var prompt)
                && session.Live
                && session.Conversation.Activity is SessionActivity.Waiting)
            {
                PendingPrompt.Remove(session.Key);
                Transport.Send(session.Job, session.Turn(prompt));
            }
        }

        if (busy >= SessionHistory.BusyWarning && !warnedBusy)
        {
            warnedBusy = true;
            Active?.Conversation.Notice($"{busy} Claude sessions are working at once", error: true);
        }
        else if (busy < SessionHistory.BusyWarning)
        {
            warnedBusy = false;
        }
    }

    private void Remember(SessionRecord record)
    {
        config.ClaudeSessions = SessionHistory.Touch(config.ClaudeSessions, record, DateTimeOffset.UtcNow);
        saveConfig();
    }
}
