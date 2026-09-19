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
}
