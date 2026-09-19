using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using XivDesktop.Core;
using XivDesktop.Core.Input;
using XivDesktop.Core.Palette;

namespace XivDesktop.Plugin.Services;

/// <summary>
/// Runs palette rows and key actions on the framework thread, through the desktop and session services.
/// Returns "ok: …" or "error: …".
/// </summary>
public sealed class CommandRunner
{
    private readonly DesktopService desktop;
    private readonly SessionService session;
    private readonly ICommandManager commands;
    private readonly IPluginLog log;

    public CommandRunner(DesktopService desktop, SessionService session, ICommandManager commands, IPluginLog log)
    {
        this.desktop = desktop;
        this.session = session;
        this.commands = commands;
        this.log = log;
    }

    /// <summary>Opens or closes the palette (argument: initial query).</summary>
    public Action<string> TogglePalette { get; set; } = _ => { };

    /// <summary>Opens or closes the app grid (argument: initial search).</summary>
    public Action<string> ToggleGrid { get; set; } = _ => { };

    public PaletteContext Context()
    {
        var target = session.Target();
        return new PaletteContext
        {
            Apps = desktop.Catalog.Catalog.Apps,
            Favourites = desktop.Favourites,
            Recents = desktop.Recents,
            Windows = session.Windows.Snapshot.Windows,
            TargetWindow = target?.Id,
            WorkspaceOf = session.WorkspaceOf,
            CurrentWorkspace = session.CurrentWorkspace,
            GhosttyAvailable = desktop.Backend.Available,
            WindowsAvailable = session.Windows.WindowsAvailable,
            CanLaunch = a => desktop.CanLaunch(a, out _),
        };
    }

    public string Run(PaletteCommand c)
    {
        try
        {
            switch (c.Kind)
            {
                case PaletteCommand.Launch:
                    return desktop.Launch(c.Arg);
                case PaletteCommand.Focus:
                    return session.Focus(c.Id);
                case PaletteCommand.Close:
                    return c.Id != 0 ? session.Close(c.Id) : session.CloseTarget();
                case PaletteCommand.Place:
                    return c.Id != 0 ? session.Place(c.Id, c.Arg) : "error: no window panel to act on";
                case PaletteCommand.Workspace:
                    return session.SwitchWorkspace(c.Number);
                case PaletteCommand.Move:
                    return c.Id != 0 ? session.Move(c.Id, c.Number) : session.MoveTarget(c.Number);
                case PaletteCommand.Reload:
                    desktop.Catalog.Reload();
                    if (session.Windows.Extended)
                        session.Windows.RefreshAgentLists();
                    return "ok: rescanning applications";
                case PaletteCommand.Copy:
                    if (c.Arg.Length == 0)
                        return "error: nothing to copy";
                    ImGui.SetClipboardText(c.Arg);
                    return $"ok: copied {c.Arg}";
                case PaletteCommand.Post:
                    return session.PostLine(c.Arg);
                case PaletteCommand.Chat:
                    return commands.ProcessCommand(c.Arg) ? $"ok: {c.Arg}" : $"error: {c.Arg.Split(' ')[0]} is not a plugin command";
                case PaletteCommand.Grid:
                    ToggleGrid("");
                    return "ok";
                default:
                    return $"error: unknown command {c.Kind}";
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "XivDesktop: {Kind} failed", c.Kind);
            return "error: " + ex.Message;
        }
    }

    /// <summary>A key action (<see cref="KeyAction"/>).</summary>
    public string RunKey(string action)
    {
        var n = KeyAction.WorkspaceNumber(action, out var move);
        if (n > 0)
            return move ? session.MoveTarget(n) : session.SwitchWorkspace(n);
        return action switch
        {
            KeyAction.Launcher => Do(() => TogglePalette("")),
            KeyAction.Terminal => session.OpenTerminal(),
            KeyAction.Close => session.CloseTarget(),
            KeyAction.TogglePet => session.TogglePet(),
            KeyAction.PinFront => session.PinFront(),
            KeyAction.FocusPrev => session.CycleFocus(-1),
            KeyAction.FocusNext => session.CycleFocus(+1),
            _ => $"error: unknown action {action}",
        };
    }

    private static string Do(Action a)
    {
        a();
        return "ok";
    }
}
