using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivDesktop.Core;
using XivDesktop.Core.Palette;
using XivDesktop.Plugin.Services;

namespace XivDesktop.Plugin.Windows;

/// <summary>
/// The launcher palette (Super+D, /desktop): one query box, ranked rows from every provider, keyboard
/// first. Up/Down or Ctrl+J/K select, Enter runs, Esc closes; a click runs a row. It opens centred, away
/// from the mouse, takes keyboard focus, and (by default) closes when it loses focus. Its look (rounded,
/// translucent, one accent) is pushed in PreDraw and popped in PostDraw, so no other window is restyled.
/// </summary>
public sealed class PaletteWindow : Window
{
    private const int MaxRows = PaletteEngine.DefaultMax;

    // One accent (a soft violet) and per-provider badge colours; everything else follows the ImGui theme.
    private static readonly Vector4 Accent = new(0.60f, 0.50f, 1.00f, 1f);
    private static readonly Vector4 Background = new(0.07f, 0.075f, 0.095f, 0.90f);

    private readonly CommandRunner runner;
    private readonly DesktopService desktop;
    private readonly SessionService session;
    private readonly ITextureProvider textures;
    private readonly IDalamudPluginInterface pi;
    private readonly Configuration config;

    private string query = "";
    private string lastQuery = "\0";
    private AppCatalog? lastCatalog;
    private long lastRev = long.MinValue;
    private int lastWorkspace;
    private int lastFavourites = -1;
    private List<PaletteItem> results = [];
    private int selected;
    private bool focusInput;
    private bool scrollToSelected;
    private int framesOpen;
    private string? flash;
    private DateTime flashUntil;
    private int pushedVars;
    private int pushedColors;

    public PaletteWindow(CommandRunner runner, DesktopService desktop, SessionService session, ITextureProvider textures, IDalamudPluginInterface pi, Configuration config)
        : base("XivDesktop###XivDesktopPalette",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings)
    {
        this.runner = runner;
        this.desktop = desktop;
        this.session = session;
        this.textures = textures;
        this.pi = pi;
        this.config = config;
        RespectCloseHotkey = false;
        ShowCloseButton = false;
        AllowPinning = false;
        AllowClickthrough = false;
        DisableWindowSounds = true;
        SizeCondition = ImGuiCond.Always;
        PositionCondition = ImGuiCond.Always;
    }

    public void Toggle(string initialQuery)
    {
        if (IsOpen && initialQuery.Length == 0)
        {
            IsOpen = false;
            return;
        }

        query = initialQuery;
        IsOpen = true;
    }

    public override void OnOpen()
    {
        focusInput = true;
        selected = 0;
        framesOpen = 0;
        lastQuery = "\0";
        flash = null;
        Place();
    }

    /// <summary>Centred horizontally, in the upper part of the screen; lower down when the mouse sits where it would open.</summary>
    private void Place()
    {
        var vp = ImGui.GetMainViewport();
        var scale = ImGuiHelpers.GlobalScale;
        var w = Math.Min(680 * scale, vp.WorkSize.X - (32 * scale));
        var h = Math.Min(460 * scale, vp.WorkSize.Y - (32 * scale));
        var x = vp.WorkPos.X + ((vp.WorkSize.X - w) / 2);
        var y = vp.WorkPos.Y + (vp.WorkSize.Y * 0.18f);
        var mouse = ImGui.GetMousePos();
        var margin = 24 * scale;
        if (mouse.X >= x - margin && mouse.X <= x + w + margin && mouse.Y >= y - margin && mouse.Y <= y + h + margin)
        {
            // Under the cursor: move to whichever half of the screen the cursor is not in.
            y = mouse.Y < vp.WorkPos.Y + (vp.WorkSize.Y / 2)
                ? Math.Min(vp.WorkPos.Y + vp.WorkSize.Y - h - margin, mouse.Y + margin * 2)
                : Math.Max(vp.WorkPos.Y + margin, mouse.Y - h - (margin * 2));
        }

        Size = new Vector2(w, h) / scale; // Window multiplies by the global scale
        Position = new Vector2(x, y);
    }

    public override void PreDraw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 16 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 14) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 10 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(14, 11) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 8) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 6 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 3 * scale);
        pushedVars = 8;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Background);
        ImGui.PushStyleColor(ImGuiCol.Border, Accent with { W = 0.35f });
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(1, 1, 1, 0.06f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(1, 1, 1, 0.08f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(1, 1, 1, 0.09f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, new Vector4(1, 1, 1, 0.15f));
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, Accent with { W = 0.35f });
        ImGui.PushStyleColor(ImGuiCol.NavHighlight, Vector4.Zero);
        pushedColors = 10;
        if (framesOpen == 0)
            ImGui.SetNextWindowFocus();
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(pushedColors);
        ImGui.PopStyleVar(pushedVars);
        pushedColors = pushedVars = 0;
    }

    public override void Draw()
    {
        try
        {
            DrawCore();
        }
        catch (Exception ex)
        {
            flash = "error: " + ex.Message;
            flashUntil = DateTime.UtcNow.AddSeconds(6);
        }
    }

    private void DrawCore()
    {
        framesOpen++;
        var scale = ImGuiHelpers.GlobalScale;

        if (focusInput)
        {
            ImGui.SetKeyboardFocusHere();
            focusInput = false;
        }

        ImGui.SetNextItemWidth(-1);
        var enter = ImGui.InputTextWithHint("##xivdesktop-palette", "Apps, windows, actions, 2+2, ask …    a:  w:  =  >", ref query, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        var inputActive = ImGui.IsItemActive();
        if (!inputActive && !enter && framesOpen > 1 && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !ImGui.IsAnyItemActive())
            focusInput = true; // typing always goes to the query

        Refresh();
        if (HandleKeys() || enter)
            RunSelected();

        ImGui.Dummy(new Vector2(0, 2 * scale));
        var footer = ImGui.GetTextLineHeightWithSpacing() + (4 * scale);
        if (ImGui.BeginChild("##xivdesktop-palette-rows", new Vector2(0, -footer), false))
            DrawRows(scale);
        ImGui.EndChild();
        DrawFooter();

        if (config.PaletteCloseOnFocusLoss && framesOpen > 6 && !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            IsOpen = false;
    }

    private void Refresh()
    {
        var catalog = desktop.Catalog.Catalog;
        var rev = session.Windows.Snapshot.Rev;
        var ws = session.CurrentWorkspace;
        var favs = desktop.Favourites.Count;
        if (query == lastQuery && ReferenceEquals(catalog, lastCatalog) && rev == lastRev && ws == lastWorkspace && favs == lastFavourites)
            return;
        var changed = query != lastQuery;
        lastQuery = query;
        lastCatalog = catalog;
        lastRev = rev;
        lastWorkspace = ws;
        lastFavourites = favs;
        results = PaletteEngine.Build(runner.Context(), query, MaxRows);
        if (changed)
            selected = 0;
        selected = Math.Clamp(selected, 0, Math.Max(0, results.Count - 1));
    }

    /// <summary>Returns true when Enter was pressed outside the input (the input reports its own Enter).</summary>
    private bool HandleKeys()
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return false;
        var io = ImGui.GetIO();
        var before = selected;
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow) || (io.KeyCtrl && (ImGui.IsKeyPressed(ImGuiKey.J) || ImGui.IsKeyPressed(ImGuiKey.N))) || (ImGui.IsKeyPressed(ImGuiKey.Tab) && !io.KeyShift))
            selected = Math.Min(results.Count - 1, selected + 1);
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow) || (io.KeyCtrl && (ImGui.IsKeyPressed(ImGuiKey.K) || ImGui.IsKeyPressed(ImGuiKey.P))) || (ImGui.IsKeyPressed(ImGuiKey.Tab) && io.KeyShift))
            selected = Math.Max(0, selected - 1);
        if (ImGui.IsKeyPressed(ImGuiKey.PageDown))
            selected = Math.Min(results.Count - 1, selected + 8);
        if (ImGui.IsKeyPressed(ImGuiKey.PageUp))
            selected = Math.Max(0, selected - 8);
        selected = Math.Max(0, selected);
        scrollToSelected |= before != selected;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            IsOpen = false;
            return false;
        }

        return ImGui.IsKeyPressed(ImGuiKey.KeypadEnter) || (!ImGui.IsAnyItemActive() && ImGui.IsKeyPressed(ImGuiKey.Enter));
    }

    private void RunSelected()
    {
        focusInput = true;
        if (selected >= 0 && selected < results.Count)
            Run(results[selected]);
    }

    private void Run(PaletteItem item)
    {
        if (!item.Enabled)
        {
            Flash("error: " + (item.Reason ?? "not available"));
            return;
        }

        var result = runner.Run(item.Command);
        if (result.StartsWith("ok", StringComparison.Ordinal))
        {
            IsOpen = false;
            return;
        }

        Flash(result);
    }

    private void Flash(string text)
    {
        flash = text;
        flashUntil = DateTime.UtcNow.AddSeconds(6);
    }

    private void DrawRows(float scale)
    {
        if (results.Count == 0)
        {
            ImGui.Dummy(new Vector2(0, 8 * scale));
            ImGui.TextDisabled(desktop.Catalog.Scanning ? "  Scanning applications…" : query.Trim().Length > 0 ? "  Nothing matches." : "  No applications found.");
            return;
        }

        var draw = ImGui.GetWindowDrawList();
        var icon = 34 * scale;
        var padX = 10 * scale;
        var line = ImGui.GetTextLineHeight();
        var rowH = Math.Max(icon, (line * 2) + (2 * scale)) + (12 * scale);
        var width = ImGui.GetContentRegionAvail().X;
        var textCol = ImGui.GetColorU32(ImGuiCol.Text);
        var dimCol = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        var accentCol = ImGui.GetColorU32(Accent);

        for (var i = 0; i < results.Count; i++)
        {
            var item = results[i];
            ImGui.PushID(i);
            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(width, rowH);
            var clicked = ImGui.InvisibleButton("##row", new Vector2(width, rowH));
            var hovered = ImGui.IsItemHovered();
            if (hovered && ImGui.GetIO().MouseDelta != Vector2.Zero)
                selected = i;
            if (i == selected && scrollToSelected)
            {
                ImGui.SetScrollHereY(0.5f);
                scrollToSelected = false;
            }

            if (ImGui.IsRectVisible(min, max))
            {
                if (i == selected)
                {
                    draw.AddRectFilled(min, max, ImGui.GetColorU32(Accent with { W = 0.20f }), 10 * scale);
                    draw.AddRectFilled(min + new Vector2(0, 8 * scale), new Vector2(min.X + (3 * scale), max.Y - (8 * scale)), accentCol, 2 * scale);
                }
                else if (hovered)
                {
                    draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.05f)), 10 * scale);
                }

                var iconPos = new Vector2(min.X + padX, min.Y + ((rowH - icon) / 2));
                DrawIcon(draw, item, iconPos, icon);

                // Badge on the right.
                var badge = item.Badge;
                var badgeSize = ImGui.CalcTextSize(badge) + (new Vector2(14, 4) * scale);
                var badgeMin = new Vector2(max.X - padX - badgeSize.X, min.Y + ((rowH - badgeSize.Y) / 2));
                var bc = BadgeColor(item.Provider);
                draw.AddRectFilled(badgeMin, badgeMin + badgeSize, ImGui.GetColorU32(bc with { W = 0.18f }), badgeSize.Y / 2);
                draw.AddText(badgeMin + (new Vector2(7, 2) * scale), ImGui.GetColorU32(bc), badge);

                var textX = iconPos.X + icon + (12 * scale);
                var textW = badgeMin.X - textX - (8 * scale);
                var titleY = min.Y + ((rowH - (line * 2) - (2 * scale)) / 2);
                DrawHighlighted(draw, new Vector2(textX, titleY), Fit(item.Title, textW), item.Highlights, item.Enabled ? textCol : dimCol, accentCol);
                var sub = item.Enabled || item.Reason == null ? item.Subtitle : (item.Subtitle.Length > 0 ? item.Subtitle + " · " : "") + item.Reason;
                if (sub.Length > 0)
                    draw.AddText(new Vector2(textX, titleY + line + (2 * scale)), dimCol, Fit(sub, textW));
            }

            if (clicked)
            {
                selected = i;
                Run(item);
            }

            ImGui.PopID();
        }
    }

    private void DrawIcon(ImDrawListPtr draw, PaletteItem item, Vector2 pos, float size)
    {
        var ui = pi.UiBuilder;
        var glyphCol = ImGui.GetColorU32(new Vector4(1, 1, 1, item.Enabled ? 0.92f : 0.45f));
        switch (item.Provider)
        {
            case PaletteProvider.App when item.AppId != null && desktop.Catalog.Catalog.Find(item.AppId) is { } app:
                IconDrawer.App(textures, draw, app, pos, size, dimmed: !item.Enabled);
                return;
            case PaletteProvider.Window when item.WindowId is { } id && session.Windows.Snapshot.Find(id) is { } w:
                if (session.AppFor(w) is { } wapp)
                {
                    IconDrawer.App(textures, draw, wapp, pos, size, dimmed: false);
                    // A small corner mark says "open window", not "app".
                    var dot = size * 0.16f;
                    draw.AddCircleFilled(pos + new Vector2(size - dot, size - dot), dot, ImGui.GetColorU32(BadgeColor(PaletteProvider.Window)));
                    return;
                }

                IconDrawer.Glyph(ui, draw, w.IsPet ? FontAwesomeIcon.Paw : FontAwesomeIcon.Thumbtack, ImGui.GetColorU32(BadgeColor(PaletteProvider.Window) with { W = 0.30f }), glyphCol, pos, size);
                return;
        }

        var glyph = item.Provider switch
        {
            PaletteProvider.Calc => FontAwesomeIcon.Calculator,
            PaletteProvider.Command => item.Command.Arg.StartsWith("ask", StringComparison.Ordinal) ? FontAwesomeIcon.CommentDots : FontAwesomeIcon.Terminal,
            PaletteProvider.Action => item.Command.Kind switch
            {
                PaletteCommand.Close => FontAwesomeIcon.Times,
                PaletteCommand.Place => item.Command.Arg == "pet" ? FontAwesomeIcon.Paw : FontAwesomeIcon.Thumbtack,
                PaletteCommand.Workspace => FontAwesomeIcon.LayerGroup,
                PaletteCommand.Move => FontAwesomeIcon.ArrowRight,
                PaletteCommand.Reload => FontAwesomeIcon.SyncAlt,
                PaletteCommand.Grid => FontAwesomeIcon.Th,
                _ => FontAwesomeIcon.Bolt,
            },
            _ => FontAwesomeIcon.Question,
        };
        IconDrawer.Glyph(ui, draw, glyph, ImGui.GetColorU32(BadgeColor(item.Provider) with { W = 0.28f }), glyphCol, pos, size);
    }

    /// <summary>Draws <paramref name="text"/> with the characters at <paramref name="marks"/> in the accent colour.</summary>
    private static void DrawHighlighted(ImDrawListPtr draw, Vector2 pos, string text, int[] marks, uint normal, uint accent)
    {
        if (marks.Length == 0)
        {
            draw.AddText(pos, normal, text);
            return;
        }

        var set = new HashSet<int>(marks);
        var x = pos.X;
        var start = 0;
        while (start < text.Length)
        {
            var hl = set.Contains(start);
            var end = start + 1;
            while (end < text.Length && set.Contains(end) == hl)
                end++;
            var run = text[start..end];
            draw.AddText(new Vector2(x, pos.Y), hl ? accent : normal, run);
            if (hl)
            {
                // Underline matched runs too, so the highlight reads without relying on colour alone.
                var w = ImGui.CalcTextSize(run).X;
                var y = pos.Y + ImGui.GetTextLineHeight();
                draw.AddLine(new Vector2(x, y), new Vector2(x + w, y), accent, 1f);
            }

            x += ImGui.CalcTextSize(run).X;
            start = end;
        }
    }

    private void DrawFooter()
    {
        if (flash != null && DateTime.UtcNow < flashUntil)
        {
            ImGui.TextColored(flash.StartsWith("ok", StringComparison.Ordinal) ? new Vector4(0.5f, 0.9f, 0.6f, 1) : new Vector4(1f, 0.65f, 0.35f, 1), flash);
            return;
        }

        var ws = session.Windows.WindowsAvailable ? $"workspace {session.CurrentWorkspace}  ·  " : "";
        ImGui.TextDisabled($"{ws}↑↓ select  ·  Enter run  ·  Esc close  ·  a: apps  w: windows  = calc  > commands");
    }

    private static Vector4 BadgeColor(PaletteProvider p) => p switch
    {
        PaletteProvider.App => new Vector4(0.40f, 0.66f, 1.00f, 1f),
        PaletteProvider.Window => new Vector4(0.40f, 0.85f, 0.60f, 1f),
        PaletteProvider.Action => new Vector4(1.00f, 0.74f, 0.35f, 1f),
        PaletteProvider.Calc => new Vector4(1.00f, 0.50f, 0.72f, 1f),
        _ => Accent,
    };

    private static string Fit(string text, float width)
    {
        if (width <= 0)
            return "";
        if (ImGui.CalcTextSize(text).X <= width)
            return text;
        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid] + "…").X <= width)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo == 0 ? "…" : text[..lo].TrimEnd() + "…";
    }
}
