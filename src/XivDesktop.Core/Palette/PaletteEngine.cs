using XivDesktop.Core.Input;
using XivDesktop.Core.Windows;
using XivDesktop.Core.Workspaces;

namespace XivDesktop.Core.Palette;

public enum PaletteProvider
{
    /// <summary>Any ghostty panel from panel.list: terminals, windows, adopted windows, chat.</summary>
    Panel,
    App,
    Window,
    Action,
    Calc,
    Command,
}

public enum PaletteFilter
{
    All,
    Apps,
    Windows,
    Calc,
    Commands,
}

/// <summary>The query box split into a provider filter and the text after its prefix.</summary>
public sealed record PaletteQuery(PaletteFilter Filter, string Text)
{
    /// <summary><c>a:</c> apps, <c>w:</c> windows, <c>=</c> calculator, <c>&gt;</c> commands and actions; anything else searches all.</summary>
    public static PaletteQuery Parse(string? raw)
    {
        var t = (raw ?? "").TrimStart();
        if (t.StartsWith("a:", StringComparison.OrdinalIgnoreCase))
            return new PaletteQuery(PaletteFilter.Apps, t[2..].Trim());
        if (t.StartsWith("w:", StringComparison.OrdinalIgnoreCase))
            return new PaletteQuery(PaletteFilter.Windows, t[2..].Trim());
        if (t.StartsWith('='))
            return new PaletteQuery(PaletteFilter.Calc, t[1..].Trim());
        if (t.StartsWith('>'))
            return new PaletteQuery(PaletteFilter.Commands, t[1..].Trim());
        return new PaletteQuery(PaletteFilter.All, t.Trim());
    }
}

/// <summary>What running a palette row does. The plugin maps each kind onto its services.</summary>
public sealed record PaletteCommand(string Kind, string Arg = "", long Id = 0, int Number = 0)
{
    public const string Launch = "launch";     // Arg: desktop-file id
    public const string Focus = "focus";       // Id: panel
    public const string Close = "close";       // Id: panel
    public const string Place = "place";       // Id: panel, Arg: pin arguments
    public const string Workspace = "workspace"; // Number
    public const string Move = "move";         // Id: panel, Number: workspace
    public const string Reload = "reload";
    public const string Copy = "copy";         // Arg: text for the clipboard
    public const string Post = "post";         // Arg: a /term line for ghostty-dalamud (without "/term ")
    public const string Chat = "chat";         // Arg: a slash command line ("/xlplugins")
    public const string Grid = "grid";         // open the app grid
    public const string TogglePet = "toggle-pet"; // Id: panel
    public const string Minimize = "minimize"; // Id: panel
    public const string Order = "order";       // Id: panel, Arg: left | right | first | last | N
}

/// <summary>A keyboard action on a palette row: its label, its chord, and what it runs.</summary>
public sealed record RowAction(string Label, KeyChord Chord, PaletteCommand Command)
{
    /// <summary>Short chord text for the hint chip ("Ctrl+P", "Alt+Left", or "Enter" for the primary action).</summary>
    public string Hint => Chord.IsNone ? "Enter" : Chord.ToString();
}

public sealed record PaletteItem
{
    public required PaletteProvider Provider { get; init; }

    public required string Title { get; init; }

    public string Subtitle { get; init; } = "";

    /// <summary>Short provider label shown as a badge ("App", "Window", …).</summary>
    public string Badge => BadgeText ?? Provider switch
    {
        PaletteProvider.App => "App",
        PaletteProvider.Window => "Window",
        PaletteProvider.Action => "Action",
        PaletteProvider.Calc => "Calc",
        _ => "Command",
    };

    /// <summary>Replaces the provider badge (panel rows show their kind: Terminal, Window, Adopted, Chat).</summary>
    public string? BadgeText { get; init; }

    /// <summary>For panel rows: terminal | window | adopted | chat.</summary>
    public string? PanelKind { get; init; }

    /// <summary>For panel rows: dropdown | tab | pet | pin | hud | min | full | hidden.</summary>
    public string? PanelView { get; init; }

    public double Score { get; init; }

    /// <summary>Matched character positions in <see cref="Title"/>, for highlighting.</summary>
    public int[] Highlights { get; init; } = [];

    public required PaletteCommand Command { get; init; }

    /// <summary>For app rows: the desktop-file id (the plugin draws its icon).</summary>
    public string? AppId { get; init; }

    /// <summary>For window rows: the panel id.</summary>
    public long? WindowId { get; init; }

    /// <summary>False when the row cannot run now (no ghostty, terminal app, no focused panel); still listed.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Why the row is disabled, when it is.</summary>
    public string? Reason { get; init; }
}

/// <summary>Everything the palette ranks over. A snapshot: the engine never calls back into the plugin.</summary>
public sealed record PaletteContext
{
    public IReadOnlyList<AppInfo> Apps { get; init; } = [];

    public IReadOnlyList<string> Favourites { get; init; } = [];

    public IReadOnlyList<string> Recents { get; init; } = [];

    public IReadOnlyList<WindowPanel> Windows { get; init; } = [];

    /// <summary>ghostty's panels (panel.list), or null: then window rows come from <see cref="Windows"/>.</summary>
    public PanelListSnapshot? Panels { get; init; }

    /// <summary>The panel window actions apply to (focused, else last focused), if any.</summary>
    public long? TargetWindow { get; init; }

    public Func<long, int> WorkspaceOf { get; init; } = _ => 0;

    public int CurrentWorkspace { get; init; } = 1;

    /// <summary>Launching and window changes are possible (ghostty-dalamud present).</summary>
    public bool GhosttyAvailable { get; init; } = true;

    /// <summary>The window list is available (ghostty's Call gate, not only Post).</summary>
    public bool WindowsAvailable { get; init; } = true;

    public Func<AppInfo, bool> CanLaunch { get; init; } = a => !a.Terminal;
}

/// <summary>
/// The multi-provider palette: apps, windows, actions, calculator and commands, ranked on one scale.
/// Scores: calculator 1000 when the text is arithmetic; apps use <see cref="AppSearch"/> (scaled) plus
/// favourite and recent boosts; windows, actions and commands use <see cref="Fuzzy"/>. Pure and cheap
/// enough to run on every keystroke.
/// </summary>
public static class PaletteEngine
{
    public const int DefaultMax = 40;

    public static List<PaletteItem> Build(PaletteContext ctx, string raw, int max = DefaultMax)
    {
        var q = PaletteQuery.Parse(raw);
        var items = new List<PaletteItem>();
        var all = q.Filter == PaletteFilter.All;

        if (all || q.Filter == PaletteFilter.Calc)
            Calc(items, q.Text, forced: q.Filter == PaletteFilter.Calc);
        if (all || q.Filter == PaletteFilter.Apps)
            Apps(items, ctx, q.Text);
        if (all || q.Filter == PaletteFilter.Windows)
        {
            if (ctx.Panels != null)
                PanelRows(items, ctx, q.Text);
            else
                Windows(items, ctx, q.Text);
        }
        if (all || q.Filter == PaletteFilter.Commands)
        {
            Commands(items, ctx, q.Text, forced: q.Filter == PaletteFilter.Commands);
            if (q.Text.Length > 0 || q.Filter == PaletteFilter.Commands)
                Actions(items, ctx, q.Text);
        }

        // Stable: equal scores keep provider order (calc, apps, windows, commands, actions).
        return items.Select((it, i) => (it, i))
            .OrderByDescending(x => x.it.Score)
            .ThenBy(x => x.i)
            .Take(max)
            .Select(x => x.it)
            .ToList();
    }

    private static void Calc(List<PaletteItem> items, string text, bool forced)
    {
        if (text.Length == 0)
            return;
        var ok = Calculator.TryEvaluate(text, out var v, out var err);
        if (ok && (forced || Calculator.LooksLikeMath(text)))
        {
            var result = Calculator.Format(v);
            items.Add(new PaletteItem
            {
                Provider = PaletteProvider.Calc,
                Title = result,
                Subtitle = $"{text.Trim()} · Enter copies the result",
                Score = 1000,
                Command = new PaletteCommand(PaletteCommand.Copy, result),
            });
        }
        else if (forced)
        {
            items.Add(new PaletteItem
            {
                Provider = PaletteProvider.Calc,
                Title = "…",
                Subtitle = err,
                Score = 1000,
                Command = new PaletteCommand(PaletteCommand.Copy, ""),
                Enabled = false,
                Reason = err,
            });
        }
    }

    private static void Apps(List<PaletteItem> items, PaletteContext ctx, string text)
    {
        var words = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < ctx.Apps.Count; i++)
        {
            var app = ctx.Apps[i];
            var fav = IndexOf(ctx.Favourites, app.Id) >= 0;
            var recent = IndexOf(ctx.Recents, app.Id);
            double score;
            int[] hl = [];
            if (words.Length == 0)
            {
                // Empty query: favourites, then recents, then the rest in catalog order.
                score = fav ? 300 : recent >= 0 ? 200 - recent : 10;
            }
            else
            {
                var s = AppSearch.Score(app, words);
                if (s <= 0)
                    continue;
                score = (s / words.Length / 5) + (fav ? 15 : 0) + (recent >= 0 ? Math.Max(0, 10 - recent) : 0);
                hl = Fuzzy.Match(app.Name, text)?.Positions ?? [];
            }

            var launchable = ctx.GhosttyAvailable && ctx.CanLaunch(app);
            items.Add(new PaletteItem
            {
                Provider = PaletteProvider.App,
                Title = app.Name,
                Subtitle = app.GenericName.Length > 0 && app.GenericName != app.Name ? app.GenericName : app.Comment,
                Score = score,
                Highlights = hl,
                Command = new PaletteCommand(PaletteCommand.Launch, app.Id),
                AppId = app.Id,
                Enabled = launchable,
                Reason = launchable ? null : !ctx.GhosttyAvailable ? "needs ghostty-dalamud" : app.Terminal ? "terminal app: needs ghostty-dalamud's terminal.new" : "cannot be started here",
            });
        }
    }

    private static void Windows(List<PaletteItem> items, PaletteContext ctx, string text)
    {
        foreach (var w in ctx.Windows)
        {
            if (w.IsEnded)
                continue;
            var title = w.DisplayName;
            double score;
            int[] hl = [];
            if (text.Length == 0)
            {
                score = 250 + (w.Focused || w.Id == ctx.TargetWindow ? 5 : 0);
            }
            else
            {
                var m = Fuzzy.Match(title, text);
                var appMatch = m == null && w.App.Length > 0 ? Fuzzy.Match(w.App, text) : null;
                if (m == null && appMatch == null)
                    continue;
                score = m?.Score ?? appMatch!.Score * 0.8;
                hl = m?.Positions ?? [];
            }

            var ws = ctx.WorkspaceOf(w.Id);
            var parts = new List<string>();
            if (w.App.Length > 0 && w.App != title)
                parts.Add(w.App);
            parts.Add(w.State == "pending" ? "opening" : w.Kind);
            if (ws > 0)
                parts.Add($"workspace {ws}" + (ws == ctx.CurrentWorkspace ? "" : " (hidden)"));
            if (w.Focused)
                parts.Add("focused");
            items.Add(new PaletteItem
            {
                Provider = PaletteProvider.Window,
                Title = title,
                Subtitle = string.Join(" · ", parts),
                Score = score,
                Highlights = hl,
                Command = new PaletteCommand(PaletteCommand.Focus, Id: w.Id),
                WindowId = w.Id,
            });
        }
    }

    private static void PanelRows(List<PaletteItem> items, PaletteContext ctx, string text)
    {
        var n = 0;
        foreach (var p in ctx.Panels!.Panels)
        {
            var title = p.DisplayName;
            double score;
            int[] hl = [];
            if (text.Length == 0)
            {
                // Panels rank first on an empty query, in ghostty's order; the focused one on top.
                score = 450 - Math.Min(40, n) + (p.Focused ? 10 : 0) - (p.IsHidden ? 45 : 0);
            }
            else
            {
                var m = Fuzzy.Match(title, text);
                FuzzyMatch? other = null;
                if (m == null)
                {
                    foreach (var alt in new[] { p.App, p.Profile, p.Kind, p.View })
                    {
                        if (alt.Length > 0 && Fuzzy.Match(alt, text) is { } am && (other == null || am.Score > other.Score))
                            other = am;
                    }
                }

                if (m == null && other == null)
                    continue;

                // Title matches beat app matches of the same quality; panels edge out apps on equal fuzzy terms.
                score = (m?.Score ?? other!.Score * 0.8) + 20;
                hl = m?.Positions ?? [];
            }

            n++;
            var parts = new List<string> { p.View.Length > 0 ? p.View : "?" };
            if (p.App.Length > 0 && p.App != title)
                parts.Add(p.App);
            else if (p.Profile.Length > 0 && p.Profile != title)
                parts.Add(p.Profile);
            var ws = p.Kind == "window" ? ctx.WorkspaceOf(p.Id) : 0;
            if (ws > 0)
                parts.Add($"workspace {ws}");
            if (p.Running == true)
                parts.Add("running");
            if (p.Focused)
                parts.Add("focused");
            items.Add(new PaletteItem
            {
                Provider = PaletteProvider.Panel,
                Title = title,
                Subtitle = string.Join(" · ", parts),
                BadgeText = p.Kind.Length > 0 ? char.ToUpperInvariant(p.Kind[0]) + p.Kind[1..] : "Panel",
                PanelKind = p.Kind,
                PanelView = p.View,
                Score = score,
                Highlights = hl,
                Command = new PaletteCommand(PaletteCommand.Focus, Id: p.Id),
                WindowId = p.Id,
            });
        }
    }

    /// <summary>
    /// The keyboard actions of a row. Panel and window rows: Enter focuses, Ctrl+P pet/pin, Ctrl+H dock to
    /// the HUD, Ctrl+M minimize, Ctrl+W close, Alt+Left/Right move in the order, Alt+Home/End first/last.
    /// Other rows have only their Enter action.
    /// </summary>
    public static List<RowAction> RowActions(PaletteItem item)
    {
        var primary = new RowAction(item.Provider switch
        {
            PaletteProvider.Panel or PaletteProvider.Window => "Focus",
            PaletteProvider.App => "Launch",
            PaletteProvider.Calc => "Copy",
            _ => "Run",
        }, KeyChord.None, item.Command);
        if (item.Provider is not (PaletteProvider.Panel or PaletteProvider.Window) || item.WindowId is not { } id)
            return [primary];
        return
        [
            primary,
            new RowAction(item.PanelView == "pet" ? "Pin" : "Pet", KeyChord.Parse("Ctrl+P"), new PaletteCommand(PaletteCommand.TogglePet, Id: id)),
            new RowAction("HUD", KeyChord.Parse("Ctrl+H"), new PaletteCommand(PaletteCommand.Place, "hud", id)),
            new RowAction("Minimize", KeyChord.Parse("Ctrl+M"), new PaletteCommand(PaletteCommand.Minimize, Id: id)),
            new RowAction("Close", KeyChord.Parse("Ctrl+W"), new PaletteCommand(PaletteCommand.Close, Id: id)),
            new RowAction("Left", KeyChord.Parse("Alt+Left"), new PaletteCommand(PaletteCommand.Order, "left", id)),
            new RowAction("Right", KeyChord.Parse("Alt+Right"), new PaletteCommand(PaletteCommand.Order, "right", id)),
            new RowAction("First", KeyChord.Parse("Alt+Home"), new PaletteCommand(PaletteCommand.Order, "first", id)),
            new RowAction("Last", KeyChord.Parse("Alt+End"), new PaletteCommand(PaletteCommand.Order, "last", id)),
        ];
    }

    /// <summary>The row action bound to <paramref name="chord"/>, if any.</summary>
    public static RowAction? ActionFor(PaletteItem item, KeyChord chord)
        => chord.IsNone ? null : RowActions(item).FirstOrDefault(a => a.Chord == chord);

    private static void Commands(List<PaletteItem> items, PaletteContext ctx, string text, bool forced)
    {
        // "/anything": run it as a chat command (Dalamud-registered commands only).
        if (text.StartsWith('/') && text.Length > 1)
        {
            items.Add(new PaletteItem
            {
                Provider = PaletteProvider.Command,
                Title = text,
                Subtitle = "Run this command",
                Score = forced ? 900 : 700,
                Command = new PaletteCommand(PaletteCommand.Chat, text),
            });
        }

        // "ask <question>": the assistant terminal, as /ask does.
        var askText = text.StartsWith("/ask ", StringComparison.OrdinalIgnoreCase) ? text[5..] : text.StartsWith("ask ", StringComparison.OrdinalIgnoreCase) ? text[4..] : null;
        if (askText is { } question && question.Trim().Length > 0)
        {
            items.Add(new PaletteItem
            {
                Provider = PaletteProvider.Command,
                Title = $"Ask: {question.Trim()}",
                Subtitle = "/ask: opens ghostty's assistant terminal with the question",
                Score = 950,
                Command = new PaletteCommand(PaletteCommand.Post, "ask " + question.Trim()),
                Enabled = ctx.GhosttyAvailable,
                Reason = ctx.GhosttyAvailable ? null : "needs ghostty-dalamud",
            });
        }

        (string Title, string Sub, PaletteCommand Cmd)[] fixedCommands =
        [
            ("/term", "Show or hide the ghostty terminal", new PaletteCommand(PaletteCommand.Post, "toggle")),
            ("/term new", "New terminal tab in the dropdown", new PaletteCommand(PaletteCommand.Post, "new")),
            ("/term pin pet", "New world terminal that follows you", new PaletteCommand(PaletteCommand.Post, "pin pet")),
            ("/window pull", "Bring a desktop window into the game", new PaletteCommand(PaletteCommand.Post, "window pull")),
            ("/ask", "Ask the assistant (type: ask <question>)", new PaletteCommand(PaletteCommand.Post, "ask")),
        ];
        foreach (var (title, sub, cmd) in fixedCommands)
        {
            double score;
            int[] hl = [];
            if (text.Length == 0)
            {
                if (!forced)
                    continue;
                score = 100;
            }
            else
            {
                var m = Fuzzy.Match(title, text.TrimStart('/'));
                if (m == null)
                    continue;
                score = m.Score * 0.85;
                hl = m.Positions;
            }

            items.Add(new PaletteItem
            {
                Provider = PaletteProvider.Command,
                Title = title,
                Subtitle = sub,
                Score = score,
                Highlights = hl,
                Command = cmd,
                Enabled = ctx.GhosttyAvailable,
                Reason = ctx.GhosttyAvailable ? null : "needs ghostty-dalamud",
            });
        }
    }

    private static void Actions(List<PaletteItem> items, PaletteContext ctx, string text)
    {
        var target = ctx.TargetWindow is { } t ? ctx.Windows.FirstOrDefault(w => w.Id == t && !w.IsEnded) : null;
        var targetName = target?.DisplayName;
        var noTarget = !ctx.WindowsAvailable ? "needs ghostty-dalamud's window list (Call IPC)" : "no window panel focused";

        var actions = new List<(string Title, string Sub, PaletteCommand Cmd, bool NeedsTarget)>();
        if (target != null)
        {
            actions.Add(("Close window", targetName!, new PaletteCommand(PaletteCommand.Close, Id: target.Id), true));
            actions.Add(("Pin here", $"{targetName}: fixed in the world in front of you", new PaletteCommand(PaletteCommand.Place, "here", target.Id), true));
            actions.Add(("Make pet", $"{targetName}: follows you", new PaletteCommand(PaletteCommand.Place, "pet", target.Id), true));
        }
        else
        {
            actions.Add(("Close window", noTarget, new PaletteCommand(PaletteCommand.Close), true));
            actions.Add(("Pin here", noTarget, new PaletteCommand(PaletteCommand.Place, "here"), true));
            actions.Add(("Make pet", noTarget, new PaletteCommand(PaletteCommand.Place, "pet"), true));
        }

        for (var n = 1; n <= WorkspaceModel.Count; n++)
        {
            var count = ctx.Windows.Count(w => !w.IsEnded && ctx.WorkspaceOf(w.Id) == n);
            actions.Add(($"Workspace {n}", (n == ctx.CurrentWorkspace ? "current · " : "") + (count == 1 ? "1 window" : $"{count} windows"), new PaletteCommand(PaletteCommand.Workspace, Number: n), false));
        }

        for (var n = 1; n <= WorkspaceModel.Count; n++)
            actions.Add(($"Move to workspace {n}", targetName ?? noTarget, new PaletteCommand(PaletteCommand.Move, Id: target?.Id ?? 0, Number: n), true));

        actions.Add(("Reload apps", "Rescan the application directories", new PaletteCommand(PaletteCommand.Reload), false));
        actions.Add(("App grid", "The full app list with icons", new PaletteCommand(PaletteCommand.Grid), false));

        foreach (var (title, sub, cmd, needsTarget) in actions)
        {
            double score;
            int[] hl = [];
            if (text.Length == 0)
            {
                score = 50;
            }
            else
            {
                var m = Fuzzy.Match(title, text);
                if (m == null)
                    continue;
                score = m.Score * 0.8;
                hl = m.Positions;
            }

            var enabled = !needsTarget || (target != null && ctx.WindowsAvailable);
            if (cmd.Kind == PaletteCommand.Workspace)
                enabled = ctx.WindowsAvailable;
            items.Add(new PaletteItem
            {
                Provider = PaletteProvider.Action,
                Title = title,
                Subtitle = sub,
                Score = score,
                Highlights = hl,
                Command = cmd,
                WindowId = needsTarget ? target?.Id : null,
                Enabled = enabled,
                Reason = enabled ? null : needsTarget ? noTarget : "needs ghostty-dalamud's window list (Call IPC)",
            });
        }
    }

    private static int IndexOf(IReadOnlyList<string> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], id, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }
}
