using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivDesktop.Core;

namespace XivDesktop.Plugin.Services;

/// <summary>
/// Owns the current <see cref="AppCatalog"/> snapshot. Scans run on the thread pool (reading Z:\ through Wine
/// is slow enough to hitch a frame); readers on any thread get the latest immutable snapshot.
/// </summary>
public sealed class CatalogService : IDisposable
{
    private readonly IPluginLog log;
    private readonly Func<string> homeOverride;
    private readonly string configDirectory;
    private readonly object gate = new();
    private CancellationTokenSource? scanCts;
    private volatile AppCatalog catalog = AppCatalog.Empty;   // the directory scan
    private volatile AppCatalog? agentCatalog;                // ghostty-agent's list, when it sends one
    private volatile bool scanning;
    private int agentBuild;

    public CatalogService(IDalamudPluginInterface pi, IPluginLog log, Func<string> homeOverride)
    {
        this.log = log;
        this.homeOverride = homeOverride;
        configDirectory = pi.ConfigDirectory.FullName;
    }

    /// <summary>
    /// ghostty-agent's app list when it has one (it reads the host's .desktop files, which a sandboxed game
    /// cannot see), else the Z:\ directory scan.
    /// </summary>
    public AppCatalog Catalog => agentCatalog is { Apps.Count: > 0 } a ? a : catalog;

    /// <summary>The directory scan alone.</summary>
    public AppCatalog Scanned => catalog;

    /// <summary>"agent" or "scan": where <see cref="Catalog"/> comes from.</summary>
    public string Source => agentCatalog is { Apps.Count: > 0 } ? "ghostty-agent" : "directory scan";

    /// <summary>
    /// Replaces the agent's part of the catalog. Icon lookups touch Z:\, so the build runs on the thread pool;
    /// an empty list falls back to the directory scan.
    /// </summary>
    public void SetAgentApps(IReadOnlyList<XivDesktop.Core.Windows.AgentApp> apps)
    {
        var build = Interlocked.Increment(ref agentBuild);
        if (apps.Count == 0)
        {
            agentCatalog = null;
            RaiseChanged();
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var paths = Paths();
                var next = AgentCatalog.Build(apps, AgentCatalog.IconMapper(paths.ToLocal, new IconResolver(paths)), catalog);
                if (build != Volatile.Read(ref agentBuild))
                    return;
                agentCatalog = next;
                log.Information("XivDesktop: {Apps} apps from ghostty-agent ({Png} with icons)", next.Apps.Count, next.Stats.IconsPng);
                RaiseChanged();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "XivDesktop: building the agent catalog failed");
            }
        });
    }

    private HostPaths Paths()
    {
        var home = homeOverride();
        if (string.IsNullOrWhiteSpace(home))
            home = HostPaths.GuessHome(Environment.GetEnvironmentVariable("HOME"), Environment.GetEnvironmentVariable("WINEHOMEDIR"), configDirectory);
        return OperatingSystem.IsWindows() ? HostPaths.Wine(home) : HostPaths.Native(home);
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "XivDesktop: catalog Changed handler failed");
        }
    }

    public bool Scanning => scanning;

    public string LastError { get; private set; } = "";

    /// <summary>Linux home used for the last scan ("" when it could not be determined).</summary>
    public string LinuxHome { get; private set; } = "";

    /// <summary>Raised on the thread pool after a scan finishes.</summary>
    public event Action? Changed;

    public void Reload()
    {
        CancellationToken token;
        lock (gate)
        {
            scanCts?.Cancel();
            scanCts?.Dispose();
            scanCts = new CancellationTokenSource();
            token = scanCts.Token;
            scanning = true;
        }

        _ = Task.Run(() => ScanCore(token), token);
    }

    private void ScanCore(CancellationToken token)
    {
        try
        {
            var home = homeOverride();
            if (string.IsNullOrWhiteSpace(home))
                home = HostPaths.GuessHome(Environment.GetEnvironmentVariable("HOME"), Environment.GetEnvironmentVariable("WINEHOMEDIR"), configDirectory);
            LinuxHome = home;
            var paths = OperatingSystem.IsWindows() ? HostPaths.Wine(home) : HostPaths.Native(home);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var next = AppCatalog.Scan(paths, token);
            if (token.IsCancellationRequested)
                return;
            catalog = next;
            LastError = next.Errors.Count > 0 ? next.Errors[0] : "";
            var s = next.Stats;
            log.Information(
                "XivDesktop: {Apps} apps from {Files} .desktop files in {Ms} ms (home {Home}); icons {Png} png, {Svg} svg-only, {Missing} missing",
                next.Apps.Count, s.FilesRead, sw.ElapsedMilliseconds, home.Length > 0 ? "found" : "unknown", s.IconsPng, s.IconsSvgOnly, s.IconsMissing);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            log.Error(ex, "XivDesktop: catalog scan failed");
        }
        finally
        {
            if (!token.IsCancellationRequested)
                scanning = false;
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "XivDesktop: catalog Changed handler failed");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            scanCts?.Cancel();
            scanCts?.Dispose();
            scanCts = null;
        }
    }
}
