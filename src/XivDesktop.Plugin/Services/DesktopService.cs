using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivDesktop.Core;
using XivDesktop.Shared;

namespace XivDesktop.Plugin.Services;

/// <summary>
/// The launcher's operations, shared by the window, /desktop, and IPC: resolve, launch, favourites,
/// recents, status. Safe to call from any thread; configuration writes and backend calls are marshalled
/// to the framework thread.
/// </summary>
public sealed class DesktopService
{
    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Configuration config;

    public DesktopService(IDalamudPluginInterface pi, IFramework framework, IPluginLog log, Configuration config, CatalogService catalog, ILaunchBackend backend)
    {
        this.pi = pi;
        this.framework = framework;
        this.log = log;
        this.config = config;
        Catalog = catalog;
        Backend = backend;
    }

    public CatalogService Catalog { get; }

    public ILaunchBackend Backend { get; }

    public IReadOnlyList<string> Favourites => config.Favourites;

    public IReadOnlyList<string> Recents => config.Recents;

    /// <summary>Panel id → desktop-file id, for panels this plugin launched through the Call gate.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<long, string> LaunchedApps { get; } = new();

    public string? LastLaunch { get; private set; }

    public string? LastError { get; private set; }

    public bool IsFavourite(string id) => UserLists.Contains(config.Favourites, id);

    /// <summary>Resolves and launches. Returns "ok: ..." or "error: ..."; "ok" means the command was handed to the backend.</summary>
    public string Launch(string idOrQuery)
    {
        var catalog = Catalog.Catalog;
        if (catalog.Apps.Count == 0)
            return Fail(Catalog.Scanning ? "the app catalog is still scanning" : "the app catalog is empty (try /desktop reload)");
        var app = catalog.Resolve(idOrQuery, config.Favourites, config.Recents);
        return app == null ? Fail($"no app matches \"{idOrQuery.Trim()}\"") : Launch(app);
    }

    public string Launch(AppInfo app)
    {
        var (command, error) = LaunchPlan.For(app);
        if (command == null)
            return Fail(error ?? "cannot launch");
        if (!Backend.Available)
            return Fail(Backend.MissingMessage);

        var task = framework.RunOnFrameworkThread(() =>
        {
            if (Backend is IWindowManager { WindowsAvailable: true } windows)
            {
                // Through the Call gate the new panel's id comes back, so its icon is known for sure.
                var result = windows.Open(run: command, onPanel: panel => LaunchedApps[panel] = app.Id);
                if (result.StartsWith("error", StringComparison.Ordinal))
                    throw new InvalidOperationException(result[7..]);
            }
            else
            {
                Backend.Launch(command);
            }

            config.Recents = UserLists.PushRecent(config.Recents, app.Id);
            Save();
            LastLaunch = $"{app.Name} ({app.Id}) at {DateTime.Now:HH:mm:ss}";
            LastError = null;
            log.Information("XivDesktop: launched {Id}: {Command}", app.Id, command);
        });

        // On the framework thread the work ran inline, so a backend failure is already visible.
        if (task.IsFaulted)
            return Fail($"{Backend.Name} rejected the launch: {task.Exception?.GetBaseException().Message}");
        return $"ok: launched {app.Name} ({app.Id})";
    }

    /// <summary>Toggles the favourite flag and returns the new state.</summary>
    public bool ToggleFavourite(string id)
    {
        var now = !IsFavourite(id);
        _ = framework.RunOnFrameworkThread(() =>
        {
            config.Favourites = UserLists.ToggleFavourite(config.Favourites, id);
            Save();
        });
        return now;
    }

    public void ForgetRecents()
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            config.Recents = [];
            Save();
        });
    }

    public List<AppSummary> Summaries()
    {
        var fav = config.Favourites;
        var rec = config.Recents;
        return Catalog.Catalog.Apps.Select(a =>
        {
            var r = -1;
            for (var i = 0; i < rec.Count; i++)
            {
                if (string.Equals(rec[i], a.Id, StringComparison.OrdinalIgnoreCase))
                {
                    r = i;
                    break;
                }
            }

            return new AppSummary
            {
                Id = a.Id,
                Name = a.Name,
                Generic = a.GenericName,
                Categories = [.. a.Categories],
                Favourite = UserLists.Contains(fav, a.Id),
                Keywords = [.. a.Keywords],
                Recent = r >= 0 ? r : null,
                Terminal = a.Terminal,
                Launchable = LaunchPlan.For(a).Command != null,
            };
        }).ToList();
    }

    public StatusPayload Status()
    {
        var catalog = Catalog.Catalog;
        var available = Backend.Available;
        var summary = !available
            ? Backend.MissingMessage
            : Catalog.Scanning && catalog.Apps.Count == 0
                ? "scanning applications…"
                : $"{catalog.Apps.Count} apps; launching through {Backend.Name}";
        return new StatusPayload
        {
            Ghostty = available,
            GhosttyStatus = available ? Backend.StatusText() : null,
            Apps = catalog.Apps.Count,
            Scanning = Catalog.Scanning,
            ScannedAt = catalog.ScannedAt == DateTimeOffset.MinValue ? null : catalog.ScannedAt,
            LastLaunch = LastLaunch,
            LastError = LastError ?? (Catalog.LastError.Length > 0 ? Catalog.LastError : null),
            Summary = summary,
        };
    }

    public void Save()
    {
        try
        {
            pi.SavePluginConfig(config);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "XivDesktop: saving configuration failed");
        }
    }

    private string Fail(string message)
    {
        LastError = message;
        return "error: " + message;
    }
}
