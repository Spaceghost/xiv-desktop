using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
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
    private readonly LauncherWindow launcher = null!;
    private bool commandRegistered;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        IFramework framework,
        ICommandManager commands,
        IChatGui chat,
        ITextureProvider textures)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.framework = framework;
        this.commands = commands;
        this.chat = chat;

        try
        {
            var config = Configuration.Load(pluginInterface);
            var catalog = Track(new CatalogService(pluginInterface, log, () => config.HomeOverride));
            ILaunchBackend backend = new GhosttyPostBackend(pluginInterface);
            desktop = new DesktopService(pluginInterface, framework, log, config, catalog, backend);

            launcher = new LauncherWindow(desktop, textures, config);
            windowSystem.AddWindow(launcher);
            pluginInterface.UiBuilder.Draw += DrawUi;
            pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;

            commandRegistered = commands.AddHandler(Command, new CommandInfo(OnCommand)
            {
                HelpMessage = "Toggle the app launcher. \"/desktop launch <app>\", \"/desktop reload\", \"/desktop status\", \"/desktop <search>\".",
            });

            Track(new DesktopIpc(pluginInterface, framework, log, desktop, text => launcher.Toggle(text)));

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

    private void OpenMainUi() => launcher.Toggle("");

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
                    launcher.Toggle("");
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
                case "help":
                    Print($"{Command} toggles the launcher; {Command} launch <app>; {Command} reload; {Command} status; {Command} <text> opens it with a search.");
                    break;
                default:
                    launcher.Toggle(args);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "{Command} failed", Command);
        }
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
