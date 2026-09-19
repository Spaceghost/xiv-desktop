// Umbra service that talks to the XivDesktop Dalamud plugin exclusively through Dalamud IPC
// (XivDesktop.v1.*). Every call is guarded: nothing here throws into Umbra or into XivDesktop.
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Umbra.Common;
using XivDesktop.Core;
using XivDesktop.Shared;

namespace Umbra.XivDesktop;

public enum DesktopLinkState
{
    /// <summary>XivDesktop is not loaded (gates not registered).</summary>
    Missing,

    /// <summary>XivDesktop is loaded but ghostty-dalamud is not, so nothing can launch.</summary>
    NoGhostty,

    /// <summary>Ready to launch.</summary>
    Ready,
}

[Service]
public sealed class XivDesktopClient : IDisposable
{
    private const long StatusIntervalMs = 1000;
    private const long AppsIntervalMs = 5000;

    private readonly ICallGateSubscriber<string>? _listApps;
    private readonly ICallGateSubscriber<string, string>? _launch;
    private readonly ICallGateSubscriber<string>? _status;
    private readonly ICallGateSubscriber<string, object>? _toggleLauncher;
    private readonly ICallGateSubscriber<string, bool>? _toggleFavourite;

    private long _lastStatusMs = long.MinValue / 2;
    private long _lastAppsMs = long.MinValue / 2;
    private int _appsDirty = 1;
    private string? _lastLoggedProblem;
    private bool _disposed;

    public XivDesktopClient()
    {
        try
        {
            IDalamudPluginInterface pi = Framework.DalamudPlugin;
            _listApps = pi.GetIpcSubscriber<string>(IpcContract.ListApps);
            _launch = pi.GetIpcSubscriber<string, string>(IpcContract.Launch);
            _status = pi.GetIpcSubscriber<string>(IpcContract.Status);
            _toggleLauncher = pi.GetIpcSubscriber<string, object>(IpcContract.ToggleLauncher);
            _toggleFavourite = pi.GetIpcSubscriber<string, bool>(IpcContract.ToggleFavourite);
        }
        catch (Exception ex)
        {
            LogOnce("XivDesktop IPC setup failed: " + ex.Message);
        }
    }

    public DesktopLinkState State { get; private set; } = DesktopLinkState.Missing;

    public StatusPayload? Status { get; private set; }

    /// <summary>Latest app list (as AppInfo so the shared search code can rank it). Replaced atomically.</summary>
    public IReadOnlyList<AppInfo> Apps { get; private set; } = [];

    public IReadOnlyList<AppSummary> Summaries { get; private set; } = [];

    /// <summary>Fetch the app list on the next tick (popups call this when they open).</summary>
    public void Invalidate() => Interlocked.Exchange(ref _appsDirty, 1);

    [OnTick]
    private void Tick()
    {
        if (_disposed) return;
        try
        {
            var now = Environment.TickCount64;
            if (now - _lastStatusMs >= StatusIntervalMs)
            {
                _lastStatusMs = now;
                RefreshStatus();
            }

            if (State != DesktopLinkState.Missing && (Interlocked.Exchange(ref _appsDirty, 0) == 1 || now - _lastAppsMs >= AppsIntervalMs))
            {
                _lastAppsMs = now;
                RefreshApps();
            }
        }
        catch (Exception ex)
        {
            LogOnce("XivDesktop widget refresh failed: " + ex.Message);
        }
    }

    private void RefreshStatus()
    {
        if (_status is null || !_status.HasFunction)
        {
            State = DesktopLinkState.Missing;
            Status = null;
            return;
        }

        Status = IpcContract.ParseStatus(_status.InvokeFunc());
        State = Status?.Ghostty == true ? DesktopLinkState.Ready : DesktopLinkState.NoGhostty;
    }

    private void RefreshApps()
    {
        if (_listApps is null || !_listApps.HasFunction) return;
        var summaries = IpcContract.ParseApps(_listApps.InvokeFunc());
        Summaries = summaries;
        Apps = summaries.Select(ToAppInfo).ToList();
    }

    internal static AppInfo ToAppInfo(AppSummary s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        GenericName = s.Generic,
        Keywords = s.Keywords,
        Categories = s.Categories,
        Terminal = s.Terminal,
    };

    /// <summary>Launches by id or query. Returns the plugin's "ok: ..."/"error: ..." text.</summary>
    public string Launch(string idOrQuery)
    {
        if (_launch is null || !_launch.HasFunction) return "error: XivDesktop is not loaded";
        try
        {
            var result = _launch.InvokeFunc(idOrQuery);
            Invalidate(); // recents changed
            return result;
        }
        catch (Exception ex)
        {
            LogOnce("XivDesktop.Launch failed: " + ex.Message);
            return "error: " + ex.Message;
        }
    }

    public bool ToggleFavourite(string id)
    {
        if (_toggleFavourite is null || !_toggleFavourite.HasFunction) return false;
        try
        {
            var now = _toggleFavourite.InvokeFunc(id);
            Invalidate();
            return now;
        }
        catch (Exception ex)
        {
            LogOnce("XivDesktop.ToggleFavourite failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Opens the plugin's full /desktop window, optionally with a search.</summary>
    public bool OpenLauncher(string search = "")
    {
        if (_toggleLauncher is null || !_toggleLauncher.HasAction) return false;
        try
        {
            _toggleLauncher.InvokeAction(search);
            return true;
        }
        catch (Exception ex)
        {
            LogOnce("XivDesktop.ToggleLauncher failed: " + ex.Message);
            return false;
        }
    }

    private void LogOnce(string message)
    {
        if (message == _lastLoggedProblem) return;
        _lastLoggedProblem = message;
        Logger.Warning("[Umbra.XivDesktop] " + message);
    }

    public void Dispose() => _disposed = true;
}
