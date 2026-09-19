using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using XivDesktop.Core;
using XivDesktop.Shared;

namespace XivDesktop.Plugin.Services;

/// <summary>
/// Launches through ghostty-dalamud's GhosttyDalamud.v1.Post gate with "window pull run CMD", the same as
/// typing "/term window pull run CMD". Only gate registration is visible from here: whether ghostty-agent is
/// running with --windows wayland is not, so a launch ghostty accepts can still produce no window.
/// </summary>
public sealed class GhosttyPostBackend : ILaunchBackend
{
    private readonly ICallGateSubscriber<string> status;
    private readonly ICallGateSubscriber<string, object> post;

    public GhosttyPostBackend(IDalamudPluginInterface pi)
    {
        status = pi.GetIpcSubscriber<string>(IpcContract.GhosttyStatus);
        post = pi.GetIpcSubscriber<string, object>(IpcContract.GhosttyPost);
    }

    public string Name => "ghostty-dalamud (Post)";

    public string MissingMessage => "needs ghostty-dalamud (loaded, with ghostty-agent running --windows wayland)";

    public bool Available
    {
        get
        {
            try
            {
                return post.HasAction;
            }
            catch
            {
                return false;
            }
        }
    }

    public string? StatusText()
    {
        try
        {
            return status.HasFunction ? status.InvokeFunc() : null;
        }
        catch
        {
            return null;
        }
    }

    public void Launch(string shellCommand) => post.InvokeAction(LaunchPlan.PostLine(shellCommand));

    /// <summary>Posts a /term command line (without "/term "), e.g. "new" or "ask QUESTION". Framework thread; may throw.</summary>
    public void Post(string line) => post.InvokeAction(line);
}
