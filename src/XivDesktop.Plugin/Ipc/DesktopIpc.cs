using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
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

    public DesktopIpc(IDalamudPluginInterface pi, IFramework framework, IPluginLog log, DesktopService desktop, Action<string> toggleWindow)
    {
        this.log = log;
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
    }
}
