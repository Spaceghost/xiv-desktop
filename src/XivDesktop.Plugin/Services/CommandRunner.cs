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

    /// <summary>The Claude sessions, once they are wired up.</summary>
    public Claude.ClaudeService? Claude { get; set; }

    /// <summary>The arcade, once it is wired up.</summary>
    public Arcade.ArcadeService? Arcade { get; set; }

    /// <summary>Opens the arcade window.</summary>
    public Action ShowArcade { get; set; } = () => { };

    /// <summary>Brings the Claude panel up.</summary>
    public Action ShowClaude { get; set; } = () => { };

    public PaletteContext Context()
    {
        var target = session.Target();
        return new PaletteContext
        {
            Apps = desktop.Catalog.Catalog.Apps,
            Favourites = desktop.Favourites,
            Recents = desktop.Recents,
            Windows = session.Windows.Snapshot.Windows,
            Panels = session.Windows.PanelSnapshot,
            TargetWindow = target?.Id,
            WorkspaceOf = session.WorkspaceOf,
            CurrentWorkspace = session.CurrentWorkspace,
            GhosttyAvailable = desktop.Backend.Available,
            WindowsAvailable = session.Windows.WindowsAvailable,
            CanLaunch = a => desktop.CanLaunch(a, out _),
            ArcadeGames = Arcade?.State.Games ?? [],
            ArcadeSystemName = id => Arcade?.State.SystemName(id) ?? id,
            ClaudeAvailable = Claude is { Transport.Ready: true },
            ClaudeReason = Claude == null ? "the Claude panel is not loaded"
                : Claude.Transport.Ready ? null
                : Claude.Transport.Problem,
            ClaudeSessions = Claude?.Sessions
                .Select(s => (s.Key, $"{s.Look.Nameplate} · {s.Conversation.Activity.ToString().ToLowerInvariant()}"))
                .ToList() ?? [],
            ClaudeRecent = Claude?.Remembered
                .Where(r => Claude.Sessions.All(s => !string.Equals(s.Key, r.Key, StringComparison.OrdinalIgnoreCase)))
                .Take(8)
                .Select(r => (r.Key, r.Summary(DateTimeOffset.UtcNow)))
                .ToList() ?? [],
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
                    return session.FocusPanel(c.Id);
                case PaletteCommand.TogglePet:
                    return session.PanelOp("toggle_pet", c.Id);
                case PaletteCommand.Minimize:
                    return session.PanelOp("minimize", c.Id);
                case PaletteCommand.Order:
                    return session.PanelOp("order", c.Id, c.Arg);
                case PaletteCommand.Close:
                    return c.Id != 0 ? session.PanelOp("close", c.Id) : session.CloseTarget();
                case PaletteCommand.Place:
                    return c.Id != 0 ? session.PanelOp("place", c.Id, c.Arg) : "error: no window panel to act on";
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
                case PaletteCommand.Arcade:
                    if (Arcade == null)
                        return "error: the arcade is not loaded";
                    if (c.Arg.Length == 0)
                    {
                        ShowArcade();
                        return "ok";
                    }

                    return Arcade.State.Game(c.Arg) is { } game ? Arcade.Play(game) : "error: that game is no longer in the library";
                case PaletteCommand.Claude:
                    if (Claude == null)
                        return "error: the Claude panel is not loaded";
                    ShowClaude();
                    return Claude.Send(null, c.Arg);
                case PaletteCommand.ClaudeSession:
                    if (Claude == null)
                        return "error: the Claude panel is not loaded";
                    ShowClaude();
                    return Claude.Focus(c.Arg);
                case PaletteCommand.ClaudeResume:
                    if (Claude == null)
                        return "error: the Claude panel is not loaded";
                    ShowClaude();
                    return Claude.Resume(c.Arg);
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
