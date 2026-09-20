using System.Reflection;
using Dalamud.Plugin.Services;
using XivDesktop.Core;
using XivDesktop.Core.Arcade;
using XivDesktop.Plugin.Claude;
using XivDesktop.Plugin.Services;

namespace XivDesktop.Plugin.Arcade;

/// <summary>
/// The plugin's half of /arcade. It owns no game or save logic: it drops the bundled host helper
/// (tools/xiv-arcade) next to the helper's own state, asks the host to run it, and reads the state.json the
/// helper writes. Maintenance commands go to ghostty-agent as jobs when it has them; a game launch always goes
/// through the launch backend, because only a process started inside the agent's compositor becomes a panel.
/// While the window is open it watches the games folder (directory timestamps, off the framework thread) and
/// asks for a rescan when something changed, so the library fills in by itself.
/// </summary>
public sealed class ArcadeService : IDisposable
{
    private const string HelperResource = "xiv-arcade";
    private const long StatePollMs = 1000;
    private const long FolderPollMs = 2500;
    private const long IdleRefreshMs = 20_000;

    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Func<HostPaths> paths;
    private readonly ILaunchBackend backend;
    private readonly IJobTransport? jobs;
    private readonly HashSet<uint> myJobs = [];
    private readonly object gate = new();

    private volatile ArcadeState state = ArcadeState.Empty;
    private DateTime stateStamp;
    private string folderSignature = "";
    private long lastStatePoll = long.MinValue / 2;
    private long lastFolderPoll = long.MinValue / 2;
    private long lastRefresh = long.MinValue / 2;
    private long fastUntil;
    private int busy;
    private bool helperReady;
    private string lastMessage = "";

    public ArcadeService(IFramework framework, IPluginLog log, Func<HostPaths> paths, ILaunchBackend backend, IJobTransport? jobs)
    {
        this.framework = framework;
        this.log = log;
        this.paths = paths;
        this.backend = backend;
        this.jobs = jobs;
        framework.Update += OnUpdate;
        if (jobs != null)
            jobs.Ended += OnJobEnded;
    }

    public ArcadeState State => state;

    /// <summary>The window is open: poll faster and watch the games folder.</summary>
    public bool Watching { get; set; }

    /// <summary>Raised on the framework thread when the helper reports something new (a refusal, a result).</summary>
    public event Action<string>? Message;

    public bool HostReachable => backend.Available || jobs is { Ready: true };

    public string HostProblem => backend.Available ? "" : backend.MissingMessage;

    /// <summary>Where the helper lives on the Linux host, beside its own state.</summary>
    public string HelperPath => ConfigDir + "/xiv-arcade";

    private string ConfigDir => paths().LinuxHome + "/.config/xiv-arcade";

    public string Run(ArcadeRequest request)
    {
        switch (request.Verb)
        {
            case ArcadeVerb.Play:
                var game = ArcadeCommands.Pick(state.Games, request.Arg) ?? state.Game(request.Arg);
                if (game == null)
                    return state.Games.Count == 0 ? "error: the library is empty. " + ArcadeText.Legal : $"error: no game matches \"{request.Arg}\"";
                return Play(game);
            case ArcadeVerb.Last:
                return state.Game(state.Last) is { } last ? Play(last) : "error: nothing has been played yet";
            case ArcadeVerb.LaunchBox or ArcadeVerb.ImportSaves or ArcadeVerb.DryRun when request.Arg.Length == 0:
                return "error: that needs a folder or a name after it";
            default:
                return ArcadeCommands.HelperCall(request) is { } call ? Helper(call.Verb, call.Args, launch: false) : "error: unknown arcade command";
        }
    }

    public string Play(ArcadeGame game)
    {
        if (!backend.Available)
            return "error: " + backend.MissingMessage;
        if (!game.Ready)
            return $"error: no emulator core for {state.SystemName(game.System)} yet. " + (state.Systems.FirstOrDefault(s => s.Id == game.System)?.Hint ?? "");
        var result = Helper("launch", [game.Id], launch: true);
        return result.StartsWith("ok", StringComparison.Ordinal)
            ? $"ok: {game.Title} · checking saves, then starting" + (state.Sync.CanLaunch ? "" : " (waiting for saves to finish syncing)")
            : result;
    }

    /// <summary>Hands a helper command to the host. "ok" means it was handed over; the outcome arrives in state.json.</summary>
    private string Helper(string verb, string[] args, bool launch)
    {
        if (paths().LinuxHome.Length == 0)
            return "error: the Linux home directory is unknown (set it in /desktop settings)";
        try
        {
            EnsureHelper();
            if (!launch && jobs is { Ready: true })
            {
                jobs.Open(paths().LinuxHome, ArcadeCommands.Argv(HelperPath, verb, args), [], (job, error) =>
                {
                    if (job == 0)
                        log.Warning("XivDesktop arcade: the agent refused the helper job: {Error}", error);
                    else
                    {
                        lock (gate)
                        {
                            myJobs.Add(job);
                        }
                    }
                });
            }
            else
            {
                if (!backend.Available)
                    return "error: " + backend.MissingMessage;
                var line = ArcadeCommands.Line(HelperPath, verb, args);
                if (line == null)
                    return "error: that name or path cannot be passed to the host";
                backend.Launch(line);
            }

            lastStatePoll = long.MinValue / 2;
            fastUntil = Environment.TickCount64 + 45_000; // the answer lands in state.json; look often until it has
            return "ok";
        }
        catch (Exception ex)
        {
            log.Warning(ex, "XivDesktop arcade: {Verb} failed", verb);
            return "error: " + ex.Message;
        }
    }

    /// <summary>Writes the bundled helper to the host when it is missing or differs from this build's copy.</summary>
    private void EnsureHelper()
    {
        if (helperReady)
            return;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(HelperResource)
            ?? throw new InvalidOperationException("the arcade helper is missing from this build");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var local = paths().ToLocal(HelperPath);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        if (!File.Exists(local) || !File.ReadAllBytes(local).AsSpan().SequenceEqual(bytes))
            File.WriteAllBytes(local, bytes);
        helperReady = true;
    }

    /// <summary>Asks the helper to look again (setup is idempotent: folders, READMEs, scan, state).</summary>
    public string Refresh()
    {
        lastRefresh = Environment.TickCount64;
        return Helper("scan", [], launch: false);
    }

    private void OnUpdate(IFramework fw)
    {
        var now = Environment.TickCount64;
        if (Watching && now - lastRefresh > IdleRefreshMs && HostReachable)
            Refresh();
        if (now - lastStatePoll < (Watching || now < fastUntil ? StatePollMs : IdleRefreshMs))
            return;
        lastStatePoll = now;
        var watchFolder = Watching && now - lastFolderPoll >= FolderPollMs;
        if (watchFolder)
            lastFolderPoll = now;
        if (Interlocked.Exchange(ref busy, 1) == 1)
            return;
        _ = Task.Run(() => Poll(watchFolder));
    }

    private void Poll(bool watchFolder)
    {
        try
        {
            var p = paths();
            if (p.LinuxHome.Length == 0)
                return;
            var file = p.ToLocal(ConfigDir + "/state.json");
            if (File.Exists(file))
            {
                var stamp = File.GetLastWriteTimeUtc(file);
                if (stamp != stateStamp)
                {
                    stateStamp = stamp;
                    var next = ArcadeState.Parse(File.ReadAllText(file));
                    if (next.Loaded)
                    {
                        state = next;
                        if (next.Message.Length > 0 && next.Message != lastMessage)
                            _ = framework.RunOnFrameworkThread(() => Message?.Invoke(next.Message));
                        lastMessage = next.Message;
                    }
                }
            }

            if (!watchFolder || state.GamesRoot.Length == 0)
                return;
            var signature = FolderSignature(p.ToLocal(state.GamesRoot));
            var changed = folderSignature.Length > 0 && signature != folderSignature;
            folderSignature = signature;
            if (changed)
                _ = framework.RunOnFrameworkThread(() => Refresh());
        }
        catch (Exception ex)
        {
            log.Debug(ex, "XivDesktop arcade: poll failed");
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
        }
    }

    /// <summary>Directory timestamps three levels deep: adding or removing a file changes its folder's.</summary>
    private static string FolderSignature(string root)
    {
        if (!Directory.Exists(root))
            return "missing";
        long ticks = 0;
        var count = 0;
        var level = new List<string> { root };
        for (var depth = 0; depth < 3 && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var dir in level)
            {
                ticks = unchecked((ticks * 31) + Directory.GetLastWriteTimeUtc(dir).Ticks);
                count++;
                if (count > 400)
                    break;
                try
                {
                    next.AddRange(Directory.EnumerateDirectories(dir));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            level = next;
        }

        return $"{count}:{ticks}";
    }

    private void OnJobEnded(JobEnded ended)
    {
        lock (gate)
        {
            if (!myJobs.Remove(ended.Job))
                return;
        }

        jobs?.Close(ended.Job);
        lastStatePoll = long.MinValue / 2;
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        if (jobs != null)
            jobs.Ended -= OnJobEnded;
    }
}
