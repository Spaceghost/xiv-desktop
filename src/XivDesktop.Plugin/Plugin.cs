using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivDesktop.Core.Arcade;
using XivDesktop.Plugin.Arcade;
using XivDesktop.Plugin.Claude;
using XivDesktop.Plugin.Ask;
using XivDesktop.Plugin.Ipc;
using XivDesktop.Plugin.Services;
using XivDesktop.Plugin.Windows;

namespace XivDesktop.Plugin;

/// <summary>
/// Dalamud entry point: configuration, catalog scan, launch backend, launcher window, /desktop and IPC.
/// Nothing here may throw into Dalamud after construction.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public const string Command = "/desktop";

    /// <summary>The Claude panel: <c>/claude</c>, <c>/claude &lt;prompt&gt;</c>, new, list, resume, look.</summary>
    public const string ClaudeCommandName = "/claude";

    public const string ArcadeCommandName = "/arcade";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly ICommandManager commands;
    private readonly IChatGui chat;

    private readonly List<IDisposable> disposables = [];
    private readonly WindowSystem windowSystem = new("XivDesktop");
    private readonly DesktopService desktop = null!;
    private readonly SessionService session = null!;
    private readonly LauncherWindow launcher = null!;
    private readonly PaletteWindow palette = null!;
    private readonly SettingsWindow settings = null!;
    private readonly ClaudeWindow claudePanel = null!;
    private readonly ClaudePermissionWindow claudePermission = null!;
    private readonly ClaudeService claude = null!;
    private readonly ArcadeService arcade = null!;
    private readonly ArcadeWindow arcadeWindow = null!;
    private bool arcadeCommandRegistered;
    private readonly INotificationManager notifications;
    private readonly AskModule? ask;
    private bool commandRegistered;
    private bool claudeCommandRegistered;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        IFramework framework,
        ICommandManager commands,
        IChatGui chat,
        ITextureProvider textures,
        IKeyState keys,
        INotificationManager notifications,
        IDataManager data,
        IClientState clientState,
        ICondition condition,
        IObjectTable objects)
    {
        this.notifications = notifications;
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.framework = framework;
        this.commands = commands;
        this.chat = chat;

        try
        {
            var config = Configuration.Load(pluginInterface);
            var catalog = Track(new CatalogService(pluginInterface, log, () => config.HomeOverride));
            // GhosttyDalamud.v1.Call when registered; it falls back to the Post gate for launching.
            var ghostty = Track(new GhosttyCallBackend(pluginInterface, framework, log));
            desktop = new DesktopService(pluginInterface, framework, log, config, catalog, ghostty);
            session = Track(new SessionService(ghostty, ghostty.Post, desktop, config, framework, notifications, log));
            var runner = new CommandRunner(desktop, session, commands, log);

            launcher = new LauncherWindow(desktop, textures, config);
            palette = new PaletteWindow(runner, desktop, session, textures, pluginInterface, config);
            runner.TogglePalette = palette.Toggle;
            runner.ToggleGrid = launcher.Toggle;
            var keybinds = Track(new KeybindService(framework, keys, log, config, action => OnKey(runner, action), () => ghostty.KeyboardFocus is { Id: > 0 }));
            ghostty.AgentAppsChanged += catalog.SetAgentApps;
            settings = new SettingsWindow(config, desktop, session, keybinds);

            // Claude in game: the agent connection, the permission server, the panel and the NPC bridge.
            claude = Track(new ClaudeService(
                log,
                framework,
                config,
                () => pluginInterface.SavePluginConfig(config),
                catalog.CurrentPaths(),
                () => catalog.CurrentPaths().LinuxHome));
            claude.UseAvatar(Track(new ClaudeAvatars(framework, objects, log, () => config.ClaudeNpc)));
            claudePanel = new ClaudeWindow(claude, config, () => pluginInterface.SavePluginConfig(config));
            claudePermission = new ClaudePermissionWindow(claude);
            runner.Claude = claude;
            runner.ShowClaude = claudePanel.Show;
            settings.Claude = claude;

            // The arcade: the player's own classic games through the host helper, as game panels.
            arcade = Track(new ArcadeService(framework, log, catalog.CurrentPaths, ghostty, claude.Transport));
            arcadeWindow = new ArcadeWindow(arcade, textures, catalog.CurrentPaths);
            arcade.Message += OnArcadeMessage;
            runner.Arcade = arcade;
            runner.ShowArcade = () => arcadeWindow.Show();

            windowSystem.AddWindow(launcher);
            windowSystem.AddWindow(arcadeWindow);
            windowSystem.AddWindow(palette);
            windowSystem.AddWindow(settings);
            windowSystem.AddWindow(claudePanel);
            windowSystem.AddWindow(claudePermission);
            pluginInterface.UiBuilder.Draw += DrawUi;
            pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
            pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;

            commandRegistered = commands.AddHandler(Command, new CommandInfo(OnCommand)
            {
                HelpMessage = "Toggle the launcher palette. \"/desktop apps\" (app grid), \"/desktop settings\", \"/desktop ws <1-9>\", \"/desktop windows\", \"/desktop launch <app>\", \"/desktop reload\", \"/desktop status\", \"/desktop <query>\".",
            });

            claudeCommandRegistered = commands.AddHandler(ClaudeCommandName, new CommandInfo(OnClaudeCommand)
            {
                HelpMessage = "Open the Claude panel. \"/claude <prompt>\" asks, \"/claude new [name]\", \"/claude list\", "
                    + "\"/claude resume [name]\", \"/claude <name> <prompt>\", \"/claude stop\", \"/claude end [name]\", \"/claude look [name] [seed]\".",
            });
            arcadeCommandRegistered = commands.AddHandler(ArcadeCommandName, new CommandInfo(OnArcadeCommand)
            {
                HelpMessage = "Your own classic games as game panels, saves kept in step across machines. " + ArcadeText.Help,
            });

            // Ask an NPC: /ask, XivDesktop.v1.Ask, the summoned speaker and its dialogue (Ask/).
            ask = Track(new AskModule(pluginInterface, framework, commands, chat, log, data, clientState, condition, objects, textures,
                windowSystem, config, () => ghostty.Post.Available, line => ghostty.Post.Post(line)));
            settings.AskTab = new AskSettings(ask, config, () => pluginInterface.SavePluginConfig(config)).Draw;

            Track(new DesktopIpc(pluginInterface, framework, log, desktop, session, runner, text => palette.Toggle(text)));

            catalog.Reload();
        }
        catch
        {
            // Dalamud does not call Dispose when the constructor throws; release what was acquired.
            DisposeCore();
            throw;
        }
    }

    private T Track<T>(T disposable)
        where T : IDisposable
    {
        disposables.Add(disposable);
        return disposable;
    }

    private void DrawUi()
    {
        try
        {
            claudePermission.Sync();
            windowSystem.Draw();

            // "Always the in-game cursor": keep Dalamud from swapping it while over our windows.
            var over = false;
            foreach (var w in windowSystem.Windows)
                over |= w.IsOpen && w.IsHovered;
            XivDesktop.Shared.GameCursor.Update("windows", over);
        }
        catch (Exception ex)
        {
            log.Error(ex, "XivDesktop UI draw failed");
        }
    }

    private void OpenMainUi() => palette.Toggle("");

    private void OpenConfigUi() => settings.Toggle();

    private void OnKey(CommandRunner runner, string action)
    {
        var result = runner.RunKey(action);
        if (!result.StartsWith("error", StringComparison.Ordinal))
            return;
        try
        {
            notifications.AddNotification(new Notification
            {
                Title = "XivDesktop",
                Content = result[7..],
                Type = NotificationType.Warning,
                InitialDuration = TimeSpan.FromSeconds(3),
            });
        }
        catch
        {
            // Notifications unavailable; the result is logged by the session.
        }
    }

    private void OnCommand(string command, string arguments)
    {
        try
        {
            var args = arguments.Trim();
            var space = args.IndexOf(' ');
            var verb = (space < 0 ? args : args[..space]).ToLowerInvariant();
            var rest = space < 0 ? "" : args[(space + 1)..].Trim();
            switch (verb)
            {
                case "":
                    palette.Toggle("");
                    break;
                case "apps" or "grid":
                    launcher.Toggle(rest);
                    break;
                case "settings" or "config":
                    settings.Toggle();
                    break;
                case "ws" or "workspace":
                    Print(int.TryParse(rest, out var n) ? session.SwitchWorkspace(n) : $"workspace {session.CurrentWorkspace}; usage: {Command} ws <1-9>");
                    break;
                case "windows":
                    PrintWindows();
                    break;
                case "launch" or "run":
                    if (rest.Length == 0)
                        Print($"usage: {Command} launch <app id or search>");
                    else
                        Print(desktop.Launch(rest));
                    break;
                case "reload" or "rescan":
                    desktop.Catalog.Reload();
                    Print("rescanning applications…");
                    break;
                case "status":
                    Print(desktop.Status().Summary);
                    break;
                case "ask":
                    ask?.Run(rest);
                    break;
                case "help":
                    Print($"{Command} toggles the launcher palette; {Command} apps (app grid); {Command} settings; {Command} ws <1-9>; {Command} windows; {Command} launch <app>; {Command} reload; {Command} status; {Command} <text> opens the palette with a query.");
                    break;
                default:
                    palette.Toggle(args);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "{Command} failed", Command);
        }
    }

    /// <summary>
    /// <c>/claude</c>. A bare command opens or focuses the panel; a verb is taken only when it is the
    /// whole first word and is not the name of an open session, so "/claude new the parser" still asks
    /// for a new session while "/claude widget fix the parser" talks to the session called widget.
    /// </summary>
    private void OnClaudeCommand(string command, string arguments)
    {
        try
        {
            var args = arguments.Trim();
            if (args.Length == 0)
            {
                Print(claude.Focus());
                claudePanel.Show();
                return;
            }

            var space = args.IndexOf(' ');
            var verb = (space < 0 ? args : args[..space]).ToLowerInvariant();
            var rest = space < 0 ? "" : args[(space + 1)..].Trim();
            var known = claude.Sessions.Any(s => string.Equals(s.Key, verb, StringComparison.OrdinalIgnoreCase));

            switch (verb)
            {
                case "new" when !known:
                    Print(claude.New(rest, ""));
                    claudePanel.Show();
                    return;
                case "list" or "sessions" when !known:
                    PrintClaudeSessions();
                    return;
                case "resume" when !known:
                    if (rest.Length == 0)
                        PrintClaudeSessions();
                    else
                        Print(claude.Resume(rest));
                    claudePanel.Show();
                    return;
                case "stop" when !known:
                    Print(claude.Stop(rest.Length > 0 ? rest : null));
                    return;
                case "end" or "dismiss" when !known:
                    Print(claude.End(rest.Length > 0 ? rest : null));
                    return;
                case "look" when !known:
                    var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var name = parts.Length > 0 && !uint.TryParse(parts[0], out _) ? parts[0] : null;
                    var seed = parts.Length > 0 && uint.TryParse(parts[^1], out var s) ? s : 0u;
                    Print(claude.Look(name, seed));
                    return;
                case "status" when !known:
                    Print(claude.Status());
                    return;
                case "help" when !known:
                    Print($"{ClaudeCommandName} opens the panel; {ClaudeCommandName} <prompt> asks; new, list, resume, stop, end, look, status.");
                    return;
            }

            // "/claude <name> <prompt>" when the first word names an open session; otherwise the whole
            // line is the prompt for the session on screen.
            Print(known && rest.Length > 0 ? claude.Send(verb, rest) : claude.Send(null, args));
            claudePanel.Show();
        }
        catch (Exception ex)
        {
            log.Error(ex, "{Command} failed", ClaudeCommandName);
        }
    }

    private void OnArcadeCommand(string command, string arguments)
    {
        try
        {
            var request = ArcadeRequest.Parse(arguments);
            switch (request.Verb)
            {
                case ArcadeVerb.Open:
                    arcadeWindow.Show();
                    return;
                case ArcadeVerb.Help:
                    Print(ArcadeText.Help + " " + ArcadeText.Legal);
                    return;
                case ArcadeVerb.Setup:
                    arcadeWindow.ShowSetup();
                    break;
                case ArcadeVerb.List:
                    PrintArcadeList();
                    return;
            }

            var result = arcade.Run(request);
            Print(result == "ok" ? request.Verb switch
            {
                ArcadeVerb.Sync => "syncing saves… " + arcade.State.Sync.Label,
                ArcadeVerb.Setup => "checking folders, emulator and games…",
                ArcadeVerb.Rescan => "rescanning your games folder…",
                _ => "asked the host; the result follows",
            }
            : result);
            if (request.Verb == ArcadeVerb.Setup)
            {
                foreach (var c in arcade.State.Checks)
                    Print($"[{(c.Ok ? "x" : " ")}] {c.Title}: {c.Detail}");
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "{Command} failed", ArcadeCommandName);
        }
    }

    private void PrintArcadeList()
    {
        var state = arcade.State;
        if (state.Games.Count == 0)
        {
            Print("no games yet. Drop your own game files into " + (state.GamesRoot.Length > 0 ? state.GamesRoot : "your games folder") + ". " + ArcadeText.Legal);
            return;
        }

        foreach (var group in state.Games.GroupBy(g => g.System))
            Print(state.SystemName(group.Key) + ": " + string.Join("; ", group.Select(g => g.Title + (g.Ready ? "" : " (core missing)"))));
        Print("saves: " + state.Sync.Label);
    }

    private void OnArcadeMessage(string message)
    {
        Print("arcade: " + message);
        arcadeWindow.Flash(message);
    }

    private void PrintClaudeSessions()
    {
        var open = claude.Sessions;
        var remembered = claude.Remembered;
        if (open.Count == 0 && remembered.Count == 0)
        {
            Print($"no Claude sessions; {ClaudeCommandName} new");
            return;
        }

        if (open.Count > 0)
        {
            Print("open: " + string.Join("; ", open.Select(s =>
                $"{s.Key} [{(s.Live ? s.Conversation.Activity.ToString().ToLowerInvariant() : "ended")}] {s.Look.Nameplate}")));
        }

        if (remembered.Count > 0)
            Print("remembered: " + string.Join("; ", remembered.Take(8).Select(r => $"{r.Key} ({r.Summary(DateTimeOffset.UtcNow)})")));
    }

    private void PrintWindows()
    {
        var list = session.OpenPanels();
        if (!session.Windows.WindowsAvailable)
        {
            Print("the window list needs ghostty-dalamud's GhosttyDalamud.v1.Call IPC");
            return;
        }

        Print(list.Count == 0
            ? $"no window panels (workspace {session.CurrentWorkspace})"
            : $"workspace {session.CurrentWorkspace}: " + string.Join("; ", list.Select(w => $"#{w.Id} {w.DisplayName} [{w.State} {w.Kind}, ws {session.WorkspaceOf(w.Id)}]")));
    }

    private void Print(string message)
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            try
            {
                chat.Print(message, "XivDesktop");
            }
            catch
            {
                // Chat unavailable (e.g. title screen).
            }
        });
    }

    public void Dispose() => DisposeCore();

    private void DisposeCore()
    {
        if (commandRegistered)
        {
            commands.RemoveHandler(Command);
            commandRegistered = false;
        }

        if (arcadeCommandRegistered)
        {
            commands.RemoveHandler(ArcadeCommandName);
            arcadeCommandRegistered = false;
        }

        if (arcade != null)
            arcade.Message -= OnArcadeMessage;

        if (claudeCommandRegistered)
        {
            commands.RemoveHandler(ClaudeCommandName);
            claudeCommandRegistered = false;
        }

        pluginInterface.UiBuilder.Draw -= DrawUi;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        windowSystem.RemoveAllWindows();
        XivDesktop.Shared.GameCursor.Release();

        for (var i = disposables.Count - 1; i >= 0; i--)
        {
            try
            {
                disposables[i].Dispose();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "XivDesktop: dispose failed");
            }
        }

        disposables.Clear();
    }
}
