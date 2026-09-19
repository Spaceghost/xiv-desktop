using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using XivDesktop.Core.Windows;
using XivDesktop.Shared;

namespace XivDesktop.Plugin.Services;

/// <summary>
/// ghostty-dalamud's JSON gate, <c>GhosttyDalamud.v1.Call</c>: launches with <c>window.open {run}</c>, and
/// lists, closes, focuses and places window panels. Polls <c>window.list</c> on the framework thread at most
/// every 250 ms and only rebuilds the snapshot when <c>rev</c> moved. While the Call gate is not registered
/// (ghostty-dalamud missing, or an older build) launching falls back to <see cref="GhosttyPostBackend"/>
/// and the window features report themselves unavailable.
/// </summary>
public sealed class GhosttyCallBackend : ILaunchBackend, IWindowManager, IDisposable
{
    private const long ListIntervalMs = 250;
    private const long AgentIntervalMs = 2000;
    private const int MaxTracked = 64;

    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<string, string> call;
    private readonly object gate = new();

    // Requests this plugin queued, by request id: a callback for the new panel id (window.open), else null.
    private readonly Dictionary<long, Action<long>?> tracked = [];
    private readonly HashSet<long> seenResults = [];

    private volatile WindowListSnapshot snapshot = WindowListSnapshot.Empty;
    private volatile AgentStatus? agent;
    private volatile FocusInfo? focus;
    private volatile IReadOnlyList<AgentApp> agentApps = [];
    private long appsForLists = -1;
    private long lastRefreshMs = long.MinValue / 2;
    private const long RefreshIntervalMs = 30_000;
    private const long PanelProbeIntervalMs = 10_000;
    private volatile PanelListSnapshot? panels;
    private bool panelsSupported = true;
    private long lastPanelProbeMs = long.MinValue / 2;
    private long lastListMs = long.MinValue / 2;
    private long lastAgentMs = long.MinValue / 2;
    private string? lastLoggedError;

    public GhosttyCallBackend(IDalamudPluginInterface pi, IFramework framework, IPluginLog log)
    {
        this.framework = framework;
        this.log = log;
        Post = new GhosttyPostBackend(pi);
        call = pi.GetIpcSubscriber<string, string>(IpcContract.GhosttyCall);
        framework.Update += OnUpdate;
    }

    /// <summary>The fallback for launching, and for /term lines (new, ask …), which Call does not carry.</summary>
    public GhosttyPostBackend Post { get; }

    public event Action<WindowListSnapshot, WindowListSnapshot>? Changed;

    public event Action<WindowRequestResult>? RequestFailed;

    public event Action<IReadOnlyList<AgentApp>>? AgentAppsChanged;

    public bool Extended { get; private set; }

    public FocusInfo? KeyboardFocus => focus;

    public IReadOnlyList<AgentApp> AgentApps => agentApps;

    public PanelListSnapshot? PanelSnapshot => panels;

    public string PanelChange(string method, long id, string arg = "") => method switch
    {
        "focus" => Change(Panels.Focus(id)),
        "close" => Change(Panels.Close(id)),
        "minimize" => Change(Panels.Minimize(id)),
        "toggle_pet" => Change(Panels.TogglePet(id)),
        "place" => Change(Panels.Place(id, arg)),
        "order" => Change(Panels.Order(id, arg)),
        _ => $"error: unknown panel method {method}",
    };

    public bool WindowsAvailable
    {
        get
        {
            try
            {
                return call.HasFunction;
            }
            catch
            {
                return false;
            }
        }
    }

    public WindowListSnapshot Snapshot => snapshot;

    public AgentStatus? Agent => agent;

    public string? LastError { get; private set; }

    // ILaunchBackend ---------------------------------------------------------------------------

    public string Name => WindowsAvailable ? "ghostty-dalamud (Call)" : Post.Name;

    public bool Available => WindowsAvailable || Post.Available;

    public string MissingMessage => Post.MissingMessage;

    public string? StatusText()
    {
        var text = Post.StatusText();
        if (WindowsAvailable && agent is { } a)
        {
            var agentText = !a.Connected ? "agent not connected" : a.WindowsOk ? "agent streams windows" : "agent too old for windows (update ghostty-agent)";
            text = text == null ? agentText : $"{text} · {agentText}";
        }

        return text;
    }

    public void Launch(string shellCommand)
    {
        if (!WindowsAvailable)
        {
            Post.Launch(shellCommand);
            return;
        }

        var result = Open(run: shellCommand);
        if (result.StartsWith("error", StringComparison.Ordinal))
            throw new InvalidOperationException(result[7..]);
    }

    // IWindowManager ---------------------------------------------------------------------------

    public string Open(string? run = null, string? match = null, long? wid = null, string? pin = null, Action<long>? onPanel = null)
        => Change(GhosttyWire.Open(run, match, wid, pin), onPanel);

    public string Close(long id) => Change(GhosttyWire.Close(id));

    public string Focus(long id) => Change(GhosttyWire.Focus(id));

    public string Place(long id, string pin) => Change(GhosttyWire.Place(id, pin));

    public string Hide(long id, bool hidden) => Change(GhosttyWire.Hide(id, hidden));

    public string TogglePet(long id) => Change(GhosttyWire.TogglePet(id));

    public string TerminalNew(string? profile, string? pin, Action<long>? onPanel = null) => Change(GhosttyWire.TerminalNew(profile, pin), onPanel);

    public string FocusCycle(int dir) => Change(GhosttyWire.FocusCycle(dir));

    public string RefreshAgentLists()
    {
        lastRefreshMs = Environment.TickCount64;
        return Change(GhosttyWire.AgentWindowsRefresh());
    }

    private string Change(string request, Action<long>? onPanel = null)
    {
        if (!WindowsAvailable)
            return "error: needs ghostty-dalamud with its window IPC (GhosttyDalamud.v1.Call)";
        CallReply reply;
        try
        {
            reply = GhosttyWire.ParseReply(call.InvokeFunc(request));
        }
        catch (Exception ex)
        {
            return Fail("ghostty call failed: " + ex.Message);
        }

        if (!reply.Ok)
            return Fail(reply.Error ?? "refused");
        var id = GhosttyWire.ParseQueued(reply);
        if (id is { } rid)
        {
            lock (gate)
            {
                if (tracked.Count >= MaxTracked)
                    tracked.Remove(tracked.Keys.Min());
                tracked[rid] = onPanel;
            }
        }

        lastListMs = long.MinValue / 2; // look for the outcome on the next frame
        return id is { } r ? $"ok: queued {r}" : "ok";
    }

    private string Fail(string message)
    {
        LastError = message;
        return "error: " + message;
    }

    // Polling ----------------------------------------------------------------------------------

    private void OnUpdate(IFramework fw)
    {
        var now = Environment.TickCount64;
        if (now - lastListMs < ListIntervalMs)
            return;
        lastListMs = now;
        try
        {
            if (!WindowsAvailable)
            {
                if (snapshot.Rev != -1)
                    Publish(WindowListSnapshot.Empty);
                agent = null;
                focus = null;
                panels = null;
                Extended = false;
                return;
            }

            if (now - lastAgentMs >= AgentIntervalMs)
            {
                lastAgentMs = now;
                agent = GhosttyWire.ParseAgentStatus(call.InvokeFunc(GhosttyWire.AgentStatusRequest()));
                PollAgentApps(now);
            }

            // focus.get doubles as the probe for the v1.1 methods (they arrived together).
            focus = GhosttyWire.ParseFocus(call.InvokeFunc(GhosttyWire.FocusGet()));
            Extended = focus != null;
            PollPanels(now);

            var reply = GhosttyWire.ParseReply(call.InvokeFunc(GhosttyWire.List()));
            var rev = GhosttyWire.PeekRev(reply);
            if (rev == null)
            {
                LogOnce("window.list: " + (reply.Error ?? "no rev"));
                return;
            }

            if (rev == snapshot.Rev)
                return;
            if (GhosttyWire.ParseWindowList(reply) is { } next)
                Publish(next);
        }
        catch (Exception ex)
        {
            LogOnce("window.list failed: " + ex.Message);
        }
    }

    /// <summary>
    /// agent.apps fills from the agent's window list (WLISTR). Re-read it whenever a new list arrived; while it
    /// is empty and the agent is connected, ask for a list at most every 30 s.
    /// </summary>
    private void PollAgentApps(long now)
    {
        if (!Extended || agent is not { Connected: true } a)
            return;
        if (a.WindowLists != appsForLists)
        {
            appsForLists = a.WindowLists;
            var apps = GhosttyWire.ParseAgentApps(call.InvokeFunc(GhosttyWire.AgentApps()));
            if (!SameApps(apps, agentApps))
            {
                agentApps = apps;
                log.Information("XivDesktop: ghostty-agent lists {Count} apps", apps.Count);
                try
                {
                    AgentAppsChanged?.Invoke(apps);
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "XivDesktop: agent apps handler failed");
                }
            }
        }

        if (agentApps.Count == 0 && now - lastRefreshMs >= RefreshIntervalMs)
            RefreshAgentLists();
    }

    /// <summary>panel.list each poll while it answers; after a refusal (older ghostty) probe again every 10 s.</summary>
    private void PollPanels(long now)
    {
        if (!panelsSupported && now - lastPanelProbeMs < PanelProbeIntervalMs)
            return;
        lastPanelProbeMs = now;
        var next = Panels.ParseList(call.InvokeFunc(Panels.List()));
        panelsSupported = next != null;
        if (next == null)
        {
            panels = null;
            return;
        }

        if (panels == null || panels.Rev != next.Rev)
            panels = next;
    }

    private static bool SameApps(IReadOnlyList<AgentApp> a, IReadOnlyList<AgentApp> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id || a[i].Name != b[i].Name || a[i].Icon != b[i].Icon)
                return false;
        }

        return true;
    }

    private void Publish(WindowListSnapshot next)
    {
        var before = snapshot;
        snapshot = next;
        ResolveRequests(next);
        try
        {
            Changed?.Invoke(before, next);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "XivDesktop: window list handler failed");
        }
    }

    private void ResolveRequests(WindowListSnapshot s)
    {
        foreach (var r in s.Requests)
        {
            Action<long>? onPanel;
            lock (gate)
            {
                if (!seenResults.Add(r.Request) || !tracked.Remove(r.Request, out onPanel))
                    continue;
                if (seenResults.Count > 256)
                    seenResults.Clear();
            }

            if (!r.Ok)
            {
                LastError = $"{r.Method}: {r.Error}";
                log.Information("XivDesktop: ghostty refused {Method}: {Error}", r.Method, r.Error ?? "");
                RequestFailed?.Invoke(r);
            }
            else if (onPanel != null && r.PanelId is { } panel)
            {
                onPanel(panel);
            }
        }
    }

    private void LogOnce(string message)
    {
        if (message == lastLoggedError)
            return;
        lastLoggedError = message;
        log.Debug("XivDesktop: {Message}", message);
    }

    public void Dispose() => framework.Update -= OnUpdate;
}
