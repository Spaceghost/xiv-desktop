using XivDesktop.Core.Windows;

namespace XivDesktop.Plugin.Services;

/// <summary>
/// Window panels on ghostty-dalamud: the list, and the changes other code can ask for. Every change is
/// queued by ghostty and applied at the end of its next frame; the outcome shows up in the next
/// <see cref="Snapshot"/> (its <c>Requests</c>). Methods return "ok: …" or "error: …" at once.
/// </summary>
public interface IWindowManager
{
    /// <summary>ghostty-dalamud's Call gate is registered.</summary>
    bool WindowsAvailable { get; }

    /// <summary>The latest window.list result (immutable; safe to read from any thread).</summary>
    WindowListSnapshot Snapshot { get; }

    /// <summary>The latest agent.status result, when known.</summary>
    AgentStatus? Agent { get; }

    /// <summary>Raised on the framework thread with (before, after) when window.list's rev changes.</summary>
    event Action<WindowListSnapshot, WindowListSnapshot>? Changed;

    /// <summary>Raised on the framework thread when a change this plugin asked for failed in ghostty.</summary>
    event Action<WindowRequestResult>? RequestFailed;

    /// <summary>window.open: one of run / match / wid (or none: the agent's choice), and an optional pin.</summary>
    string Open(string? run = null, string? match = null, long? wid = null, string? pin = null, Action<long>? onPanel = null);

    string Close(long id);

    string Focus(long id);

    string Place(long id, string pin);

    /// <summary>
    /// ghostty-dalamud answers the v1.1 methods (window.hide, window.toggle_pet, terminal.new, focus.get,
    /// focus.cycle, agent.apps). Probed with focus.get; false on an older build.
    /// </summary>
    bool Extended { get; }

    /// <summary>The world panel with the keyboard (window or terminal), from focus.get; null when unknown.</summary>
    FocusInfo? KeyboardFocus { get; }

    /// <summary>The agent's app list (agent.apps); empty when the agent sends none.</summary>
    IReadOnlyList<AgentApp> AgentApps { get; }

    /// <summary>Raised on the framework thread when <see cref="AgentApps"/> changes.</summary>
    event Action<IReadOnlyList<AgentApp>>? AgentAppsChanged;

    string Hide(long id, bool hidden);

    string TogglePet(long id);

    string TerminalNew(string? profile, string? pin, Action<long>? onPanel = null);

    string FocusCycle(int dir);

    /// <summary>panel.list, when ghostty-dalamud has it; null on an older build (use <see cref="Snapshot"/>).</summary>
    PanelListSnapshot? PanelSnapshot { get; }

    /// <summary>A panel.* change: focus, close, minimize, toggle_pet, place (arg: pin), order (arg: to).</summary>
    string PanelChange(string method, long id, string arg = "");

    /// <summary>Asks the agent for its window and app lists again.</summary>
    string RefreshAgentLists();
}
