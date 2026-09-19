using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using XivDesktop.Core;
using XivDesktop.Core.Windows;
using XivDesktop.Core.Workspaces;
using XivDesktop.Shared;

namespace XivDesktop.Plugin.Services;

/// <summary>
/// The desktop session on top of the window list: which panel actions apply to, workspaces (hiding the
/// others' panels), focus cycling, pin/pet, the terminal key, and open/end notifications. Everything that
/// changes state runs on the framework thread; reads (<see cref="WindowsPayload"/>) are safe anywhere.
/// </summary>
public sealed class SessionService : IDisposable
{
    public const string HideArgs = "hide";
    public const string ShowArgs = "hide off";

    private readonly IWindowManager windows;
    private readonly GhosttyPostBackend post;
    private readonly DesktopService desktop;
    private readonly Configuration config;
    private readonly IFramework framework;
    private readonly INotificationManager notifications;
    private readonly IPluginLog log;
    private readonly WorkspaceModel workspaces;
    private long lastFocused;
    private volatile string payloadJson = IpcContract.Serialize(new WindowsPayload());

    public SessionService(IWindowManager windows, GhosttyPostBackend post, DesktopService desktop, Configuration config, IFramework framework, INotificationManager notifications, IPluginLog log)
    {
        this.windows = windows;
        this.post = post;
        this.desktop = desktop;
        this.config = config;
        this.framework = framework;
        this.notifications = notifications;
        this.log = log;
        workspaces = new WorkspaceModel(config.Workspaces, config.CurrentWorkspace);
        windows.Changed += OnChanged;
        windows.RequestFailed += OnRequestFailed;
    }

    public IWindowManager Windows => windows;

    public int CurrentWorkspace => workspaces.Current;

    public int WorkspaceOf(long id) => workspaces.WorkspaceOf(id);

    public bool IsHidden(long id) => workspaces.Hidden.Contains(id);

    public string? LastMessage { get; private set; }

    /// <summary>Live and pending panels, oldest first.</summary>
    public List<WindowPanel> OpenPanels() => windows.Snapshot.Windows.Where(w => !w.IsEnded).ToList();

    /// <summary>The panel window actions apply to: the focused one, else the last one focused, else the newest on this workspace.</summary>
    public WindowPanel? Target()
    {
        var open = OpenPanels();
        return open.FirstOrDefault(w => w.Focused)
               ?? open.FirstOrDefault(w => w.Id == lastFocused)
               ?? open.LastOrDefault(w => workspaces.WorkspaceOf(w.Id) is var ws && (ws == 0 || ws == workspaces.Current));
    }

    public AppInfo? AppFor(WindowPanel w)
    {
        desktop.LaunchedApps.TryGetValue(w.Id, out var launched);
        return WindowApps.Find(desktop.Catalog.Catalog.Apps, w, launched);
    }

    // Actions (framework thread) ---------------------------------------------------------------

    public string Focus(long id)
    {
        var ws = workspaces.WorkspaceOf(id);
        if (ws != 0 && ws != workspaces.Current)
            SwitchWorkspace(ws); // sway: focusing a window on another workspace goes there
        var r = windows.Focus(id);
        if (r.StartsWith("ok", StringComparison.Ordinal))
            lastFocused = id;
        return Report(r);
    }

    public string Close(long id) => Report(windows.Close(id));

    public string Place(long id, string pin)
    {
        var r = windows.Place(id, pin);
        if (r.StartsWith("ok", StringComparison.Ordinal))
        {
            // A new anchor is shown whatever the old one was; keep the model in step.
            workspaces.MarkHidden(id, pin.Trim() == HideArgs);
            lastFocused = id;
        }

        return Report(r);
    }

    public string CloseTarget() => Target() is { } w ? Close(w.Id) : Report(NoTarget());

    /// <summary>Pet → pinned (<see cref="Configuration.PinArgs"/>), anything else → pet.</summary>
    public string TogglePet() => Target() is { } w ? Place(w.Id, w.IsPet ? config.PinArgs : "pet") : Report(NoTarget());

    public string PinFront() => Target() is { } w ? Place(w.Id, config.PinFrontArgs) : Report(NoTarget());

    /// <summary>Focus the next (+1) or previous (-1) panel on the current workspace, wrapping around.</summary>
    public string CycleFocus(int step)
    {
        var ids = workspaces.PanelsOn(workspaces.Current, windows.Snapshot.Windows);
        foreach (var w in OpenPanels())
        {
            if (workspaces.WorkspaceOf(w.Id) == 0 && !ids.Contains(w.Id))
                ids.Add(w.Id);
        }

        ids.Sort();
        if (ids.Count == 0)
            return Report("error: no window panels on this workspace");
        var current = Target()?.Id ?? 0;
        var i = ids.IndexOf(current);
        var next = i < 0 ? (step > 0 ? ids[0] : ids[^1]) : ids[((i + step) % ids.Count + ids.Count) % ids.Count];
        return Focus(next);
    }

    public string SwitchWorkspace(int n)
    {
        if (n is < 1 or > WorkspaceModel.Count)
            return Report($"error: workspaces are 1..{WorkspaceModel.Count}");
        if (!workspaces.Switch(n))
            return Report($"ok: workspace {n}");
        Persist();
        ApplyVisibility();
        return Report($"ok: workspace {n}");
    }

    public string MoveTarget(int n) => Target() is { } w ? Move(w.Id, n) : Report(NoTarget());

    public string Move(long id, int n)
    {
        if (n is < 1 or > WorkspaceModel.Count)
            return Report($"error: workspaces are 1..{WorkspaceModel.Count}");
        var w = windows.Snapshot.Find(id);
        if (w == null || w.IsEnded)
            return Report($"error: no window panel {id}");
        workspaces.Move(id, n, w);
        Persist();
        ApplyVisibility();
        return Report($"ok: {w.DisplayName} → workspace {n}");
    }

    /// <summary>Super+Enter: the configured /term line ("new" opens a ghostty tab).</summary>
    public string OpenTerminal() => PostLine(config.TerminalLine);

    public string PostLine(string line)
    {
        if (!post.Available)
            return Report("error: " + post.MissingMessage);
        try
        {
            post.Post(line);
            return Report("ok: /term " + line);
        }
        catch (Exception ex)
        {
            return Report("error: " + ex.Message);
        }
    }

    /// <summary>Re-send hide/show for every panel (after turning hiding on or off).</summary>
    public void ResetVisibility()
    {
        foreach (var w in OpenPanels())
        {
            if (IsHidden(w.Id) && !config.WorkspacesHide)
            {
                windows.Place(w.Id, ShowArgs);
                workspaces.MarkHidden(w.Id, false);
            }
        }

        ApplyVisibility();
    }

    private string NoTarget() => windows.WindowsAvailable ? "error: no window panel to act on" : "error: needs ghostty-dalamud's window IPC (GhosttyDalamud.v1.Call)";

    private string Report(string result)
    {
        LastMessage = result;
        RefreshPayload();
        if (result.StartsWith("error", StringComparison.Ordinal))
            log.Information("XivDesktop: {Result}", result);
        return result;
    }

    // Window list changes ----------------------------------------------------------------------

    private void OnChanged(WindowListSnapshot before, WindowListSnapshot after)
    {
        if (after.Rev == -1)
            workspaces.ForgetHidden(); // ghostty went away: its anchors are gone with it

        if (after.Windows.FirstOrDefault(w => w.Focused) is { } focused)
            lastFocused = focused.Id;

        if (workspaces.Reconcile(after.Windows))
            Persist();
        ApplyVisibility();
        RefreshPayload();

        if (before.Rev == -1)
            return; // the first list after load is not news
        foreach (var e in WindowDiff.Diff(before, after))
            Notify(e);
        foreach (var id in desktop.LaunchedApps.Keys)
        {
            if (after.Find(id) == null)
                desktop.LaunchedApps.TryRemove(id, out _);
        }
    }

    private void ApplyVisibility()
    {
        if (!config.WorkspacesHide || !windows.WindowsAvailable)
            return;
        foreach (var change in workspaces.Plan(windows.Snapshot.Windows))
        {
            var r = windows.Place(change.Id, change.Hide ? HideArgs : ShowArgs);
            if (r.StartsWith("ok", StringComparison.Ordinal))
                workspaces.MarkHidden(change.Id, change.Hide);
            else
                break; // queue full or ghostty gone: try again on the next change
        }
    }

    private void OnRequestFailed(WindowRequestResult r)
    {
        LastMessage = $"error: {r.Method}: {r.Error}";
        if (r.Method == "window.open")
        {
            notifications.AddNotification(new Notification
            {
                Title = "Window did not open",
                Content = r.Error ?? "ghostty refused it",
                Type = NotificationType.Warning,
            });
        }
    }

    private void Notify(WindowEvent e)
    {
        var opened = e.Kind == WindowEventKind.Opened;
        if (opened ? !config.NotifyOpened : !config.NotifyEnded)
            return;
        try
        {
            var app = AppFor(e.Window);
            var n = new Notification
            {
                Title = opened ? e.Window.DisplayName : $"{e.Window.DisplayName} closed",
                Content = opened
                    ? $"Opened as a {(e.Window.Kind.Length > 0 ? e.Window.Kind : "panel")} on workspace {Math.Max(1, workspaces.WorkspaceOf(e.Window.Id))}" + (app != null && app.Name != e.Window.Title ? $" · {app.Name}" : "")
                    : "The window panel ended.",
                Type = NotificationType.Info,
                InitialDuration = TimeSpan.FromSeconds(opened ? 4 : 3),
                Minimized = !opened,
            };
            if (app?.IconPath is { } icon)
                n.Icon = INotificationIcon.FromFile(icon);
            else
                n.Icon = INotificationIcon.From(opened ? Dalamud.Interface.FontAwesomeIcon.WindowRestore : Dalamud.Interface.FontAwesomeIcon.WindowClose);
            notifications.AddNotification(n);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "XivDesktop: notification failed");
        }
    }

    private void Persist()
    {
        config.Workspaces = workspaces.Export();
        config.CurrentWorkspace = workspaces.Current;
        desktop.Save();
    }

    // Reads (any thread) -----------------------------------------------------------------------

    /// <summary>The Windows IPC payload, rebuilt on the framework thread after every change; safe from any thread.</summary>
    public string PayloadJson => payloadJson;

    private void RefreshPayload()
    {
        try
        {
            payloadJson = IpcContract.Serialize(Payload());
        }
        catch (Exception ex)
        {
            log.Debug(ex, "XivDesktop: windows payload failed");
        }
    }

    /// <summary>Framework thread only (reads the workspace model).</summary>
    public WindowsPayload Payload()
    {
        var s = windows.Snapshot;
        var apps = desktop.Catalog.Catalog.Apps;
        return new WindowsPayload
        {
            Available = windows.WindowsAvailable,
            Rev = s.Rev,
            Workspace = workspaces.Current,
            Target = Target()?.Id,
            Windows = s.Windows.Select(w =>
            {
                desktop.LaunchedApps.TryGetValue(w.Id, out var launched);
                return new WindowPayload
                {
                    Id = w.Id,
                    Title = w.Title,
                    App = w.App,
                    State = w.State,
                    Kind = w.Kind,
                    Focused = w.Focused,
                    Workspace = workspaces.WorkspaceOf(w.Id),
                    Hidden = workspaces.Hidden.Contains(w.Id),
                    AppId = WindowApps.Find(apps, w, launched)?.Id,
                };
            }).ToList(),
        };
    }

    public void Dispose()
    {
        windows.Changed -= OnChanged;
        windows.RequestFailed -= OnRequestFailed;
    }
}
