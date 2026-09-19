using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
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
    private readonly INotificationManager notifications;
    private readonly AskModule? ask;
    private bool commandRegistered;

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
            windowSystem.AddWindow(launcher);
            windowSystem.AddWindow(palette);
            windowSystem.AddWindow(settings);
            pluginInterface.UiBuilder.Draw += DrawUi;
            pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
            pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;

            commandRegistered = commands.AddHandler(Command, new CommandInfo(OnCommand)
            {
                HelpMessage = "Toggle the launcher palette. \"/desktop apps\" (app grid), \"/desktop settings\", \"/desktop ws <1-9>\", \"/desktop windows\", \"/desktop launch <app>\", \"/desktop reload\", \"/desktop status\", \"/desktop <query>\".",
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
            windowSystem.Draw();
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

        pluginInterface.UiBuilder.Draw -= DrawUi;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        windowSystem.RemoveAllWindows();

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
