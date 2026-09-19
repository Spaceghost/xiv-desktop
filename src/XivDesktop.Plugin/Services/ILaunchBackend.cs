namespace XivDesktop.Plugin.Services;

/// <summary>
/// How a validated shell command reaches the Linux host and becomes an in-world window. Today there is one
/// implementation (<see cref="GhosttyPostBackend"/>); a backend for ghostty-dalamud's JSON Call gate
/// (window.open and friends) replaces it without touching the launcher, IPC or commands.
/// </summary>
public interface ILaunchBackend
{
    /// <summary>Short name for status lines.</summary>
    string Name { get; }

    /// <summary>The backend's IPC is registered, so launching can be attempted.</summary>
    bool Available { get; }

    /// <summary>Shown when <see cref="Available"/> is false.</summary>
    string MissingMessage { get; }

    /// <summary>Backend status text for the UI, or null when unavailable.</summary>
    string? StatusText();

    /// <summary>Starts <paramref name="shellCommand"/> (run with sh -c on the host). Called on the framework thread; may throw.</summary>
    void Launch(string shellCommand);
}
