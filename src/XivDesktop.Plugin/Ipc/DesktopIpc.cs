using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using XivDesktop.Core.Palette;
using XivDesktop.Plugin.Services;
using XivDesktop.Shared;

namespace XivDesktop.Plugin.Ipc;

/// <summary>
/// Provider side of <see cref="IpcContract"/>. Every handler catches its own exceptions: a caller gets an
/// "error: ..." string (or an empty array), never an exception thrown across the IPC boundary.
/// </summary>
public sealed class DesktopIpc : IDisposable
{
    private readonly IPluginLog log;
    private readonly ICallGateProvider<string> listApps;
    private readonly ICallGateProvider<string, string> launch;
    private readonly ICallGateProvider<string> status;
    private readonly ICallGateProvider<string, object> toggleLauncher;
    private readonly ICallGateProvider<string, bool> toggleFavourite;
    private readonly ICallGateProvider<string> windows;
    private readonly ICallGateProvider<int, string> workspace;
    private readonly ICallGateProvider<string, string> palette;
    private readonly ICallGateProvider<string, string> windowAction;
    private readonly IFramework framework;

    public DesktopIpc(IDalamudPluginInterface pi, IFramework framework, IPluginLog log, DesktopService desktop, SessionService session, CommandRunner runner, Action<string> toggleWindow)
    {
        this.log = log;
        this.framework = framework;
        listApps = pi.GetIpcProvider<string>(IpcContract.ListApps);
        launch = pi.GetIpcProvider<string, string>(IpcContract.Launch);
        status = pi.GetIpcProvider<string>(IpcContract.Status);
        toggleLauncher = pi.GetIpcProvider<string, object>(IpcContract.ToggleLauncher);
        toggleFavourite = pi.GetIpcProvider<string, bool>(IpcContract.ToggleFavourite);

        listApps.RegisterFunc(() => Guard(() => IpcContract.Serialize(desktop.Summaries()), "[]"));
        launch.RegisterFunc(q => Guard(() => desktop.Launch(q ?? ""), "error: internal error"));
        status.RegisterFunc(() => Guard(() => IpcContract.Serialize(desktop.Status()), "{}"));
        toggleLauncher.RegisterAction(text => Guard(() =>
        {
            _ = framework.RunOnFrameworkThread(() => toggleWindow(text ?? ""));
            return 0;
        }, 0));
        toggleFavourite.RegisterFunc(id => Guard(() => !string.IsNullOrWhiteSpace(id) && desktop.ToggleFavourite(id), false));

        windows = pi.GetIpcProvider<string>(IpcContract.Windows);
        workspace = pi.GetIpcProvider<int, string>(IpcContract.Workspace);
        palette = pi.GetIpcProvider<string, string>(IpcContract.Palette);
        windowAction = pi.GetIpcProvider<string, string>(IpcContract.WindowAction);

        // The windows payload is rebuilt on the framework thread after every change; reading it is free.
        windows.RegisterFunc(() => Guard(() => session.PayloadJson, "{}"));
        workspace.RegisterFunc(n => Guard(() => OnFramework(() => n == 0 ? $"ok: workspace {session.CurrentWorkspace}" : session.SwitchWorkspace(n)), "error: internal error"));
        palette.RegisterFunc(q => Guard(() => OnFramework(() => IpcContract.Serialize(
            PaletteEngine.Build(runner.Context(), q ?? "", 10).Select(ToPayload).ToList())), "[]"));
        windowAction.RegisterFunc(json => Guard(() => OnFramework(() => WindowAction(session, json)), "error: internal error"));
    }

    private static PaletteResultPayload ToPayload(PaletteItem i) => new()
    {
        Provider = i.Provider.ToString().ToLowerInvariant(),
        Title = i.Title,
        Subtitle = i.Subtitle,
        Score = Math.Round(i.Score, 2),
        Enabled = i.Enabled,
        Reason = i.Reason,
        Command = new PaletteCommandPayload { Kind = i.Command.Kind, Arg = i.Command.Arg, Id = i.Command.Id, Number = i.Command.Number },
        AppId = i.AppId,
        WindowId = i.WindowId,
    };

    private static string WindowAction(SessionService session, string? json)
    {
        if (IpcContract.ParseWindowAction(json) is not { } a)
            return "error: expected {\"action\": \"focus|close|pet|pin|toggle|place|move\", \"id\": N, ...}";
        var id = a.Id != 0 ? a.Id : session.Target()?.Id ?? 0;
        if (id == 0)
            return "error: no window panel to act on";
        return a.Action.ToLowerInvariant() switch
        {
            "focus" => session.Focus(id),
            "close" => session.Close(id),
            "pet" => session.Place(id, "pet"),
            "toggle" => session.Windows.Snapshot.Find(id) is { } w ? session.TogglePet(w) : $"error: no window panel {id}",
            "pin" => session.Place(id, "here"),
            "place" => string.IsNullOrWhiteSpace(a.Pin) ? "error: place needs \"pin\"" : session.Place(id, a.Pin),
            "move" => session.Move(id, a.Workspace),
            _ => $"error: unknown action \"{a.Action}\"",
        };
    }

    /// <summary>Runs on the framework thread (inline when already there) and waits at most two seconds.</summary>
    private string OnFramework(Func<string> f)
    {
        var task = framework.RunOnFrameworkThread(f);
        return task.Wait(TimeSpan.FromSeconds(2)) ? task.Result : "error: timed out waiting for the framework thread";
    }

    private T Guard<T>(Func<T> f, T fallback)
    {
        try
        {
            return f();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "XivDesktop IPC call failed");
            return fallback;
        }
    }

    public void Dispose()
    {
        listApps.UnregisterFunc();
        launch.UnregisterFunc();
        status.UnregisterFunc();
        toggleLauncher.UnregisterAction();
        toggleFavourite.UnregisterFunc();
        windows.UnregisterFunc();
        workspace.UnregisterFunc();
        palette.UnregisterFunc();
        windowAction.UnregisterFunc();
    }
}
