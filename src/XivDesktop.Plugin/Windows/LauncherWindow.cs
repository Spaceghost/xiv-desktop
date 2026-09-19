using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using XivDesktop.Core;
using XivDesktop.Plugin.Services;

namespace XivDesktop.Plugin.Windows;

/// <summary>
/// The app grid (/desktop apps; the palette is /desktop): search box (focused on open), favourites and recents rows, and an icon grid.
/// Arrow keys move the selection, Enter launches it, right-click toggles a favourite. Icon textures are
/// requested only for cells that are on screen; Dalamud's shared texture cache drops unused ones.
/// </summary>
public sealed class LauncherWindow : Window
{
    private const float CellPadding = 6f;

    private readonly DesktopService desktop;
    private readonly ITextureProvider textures;
    private readonly Configuration config;

    private string search = "";
    private string lastSearch = "\0";
    private AppCatalog? lastCatalog;
    private int lastFavouriteCount = -1;
    private List<AppInfo> results = [];
    private int selected;
    private bool focusSearch;
    private bool scrollToSelected;
    private int columns = 1;
    private string? flash;
    private DateTime flashUntil;

    public LauncherWindow(DesktopService desktop, ITextureProvider textures, Configuration config)
        : base("XivDesktop apps###XivDesktopLauncher")
    {
        this.desktop = desktop;
        this.textures = textures;
        this.config = config;
        Size = new Vector2(620, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(360, 260), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

    /// <summary>Opens (or closes) the window; opening with text pre-fills the search box.</summary>
    public void Toggle(string initialSearch)
    {
        if (IsOpen && initialSearch.Length == 0)
        {
            IsOpen = false;
            return;
        }

        search = initialSearch;
        IsOpen = true;
        BringToFront();
    }

    public override void OnOpen()
    {
        focusSearch = true;
        selected = 0;
        lastSearch = "\0";
    }

    public override void Draw()
    {
        var available = desktop.Backend.Available;
        var catalog = desktop.Catalog.Catalog;

        if (!available)
            ImGui.TextColored(ImGuiColors.DalamudOrange, desktop.Backend.MissingMessage);

        // Search box. EnterReturnsTrue: Enter launches the selection instead of being swallowed.
        if (focusSearch)
        {
            ImGui.SetKeyboardFocusHere();
            focusSearch = false;
        }

        ImGui.SetNextItemWidth(-1);
        var enter = ImGui.InputTextWithHint("##xivdesktop-search", "Search apps (name, keyword, category)…", ref search, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        var searchActive = ImGui.IsItemActive();
        if (enter)
            focusSearch = true; // keep typing after a launch attempt

        Refresh(catalog);
        HandleKeys(searchActive);
        if (enter && results.Count > 0)
            LaunchSelected(available);

        if (search.Length == 0)
        {
            DrawRow("Favourites", UserLists.Resolve(desktop.Favourites, catalog), available);
            DrawRow("Recent", UserLists.Resolve(desktop.Recents, catalog), available);
        }

        var footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        if (ImGui.BeginChild("##xivdesktop-grid", new Vector2(0, -footer), false))
            DrawGrid(available);
        ImGui.EndChild();

        DrawFooter(catalog);
    }

    private void Refresh(AppCatalog catalog)
    {
        var favCount = desktop.Favourites.Count;
        if (search == lastSearch && ReferenceEquals(catalog, lastCatalog) && favCount == lastFavouriteCount)
            return;
        var searchChanged = search != lastSearch;
        lastSearch = search;
        lastCatalog = catalog;
        lastFavouriteCount = favCount;
        results = AppSearch.Search(catalog.Apps, search, desktop.Favourites.ToList(), desktop.Recents);
        if (searchChanged)
            selected = 0;
        selected = Math.Clamp(selected, 0, Math.Max(0, results.Count - 1));
    }

    private void HandleKeys(bool searchActive)
    {
        if (results.Count == 0 || !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return;
        var before = selected;
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
            selected = Math.Min(results.Count - 1, selected + columns);
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
            selected = Math.Max(0, selected - columns);

        // Left/Right move the text cursor while typing; they move the selection when the box is empty or unfocused.
        if (!searchActive || search.Length == 0)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
                selected = Math.Min(results.Count - 1, selected + 1);
            if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
                selected = Math.Max(0, selected - 1);
        }

        if (!searchActive && (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)))
            LaunchSelected(desktop.Backend.Available);
        if (ImGui.IsKeyPressed(ImGuiKey.Escape) && !searchActive)
            IsOpen = false;
        scrollToSelected |= before != selected;
    }

    private void LaunchSelected(bool available)
    {
        if (selected >= 0 && selected < results.Count)
            Launch(results[selected], available);
    }

    private void Launch(AppInfo app, bool available)
    {
        if (!available)
        {
            Flash(desktop.Backend.MissingMessage);
            return;
        }

        var result = desktop.Launch(app);
        Flash(result);
        if (result.StartsWith("ok", StringComparison.Ordinal) && config.CloseOnLaunch)
            IsOpen = false;
    }

    private void Flash(string text)
    {
        flash = text;
        flashUntil = DateTime.UtcNow.AddSeconds(6);
    }

    private void DrawRow(string label, List<AppInfo> apps, bool available)
    {
        if (apps.Count == 0)
            return;
        ImGui.TextDisabled(label);
        var size = new Vector2(ImGuiHelpers.GlobalScale * 28);
        for (var i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            if (i > 0)
                ImGui.SameLine();
            ImGui.PushID($"{label}:{app.Id}");
            var pos = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton("##chip", size))
                Launch(app, available);
            var hovered = ImGui.IsItemHovered();
            DrawIcon(ImGui.GetWindowDrawList(), app, pos, size.X, dimmed: !CanLaunch(app, available));
            if (hovered)
                Tooltip(app);
            ContextMenu(app, available);
            ImGui.PopID();
        }
    }

    private void DrawGrid(bool available)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var iconSize = config.IconSize * scale;
        var cellW = Math.Max(iconSize + (CellPadding * 2 * scale), 92 * scale);
        var textH = ImGui.GetTextLineHeight();
        var cellH = iconSize + textH + (CellPadding * 3 * scale);
        var avail = ImGui.GetContentRegionAvail().X;
        columns = Math.Max(1, (int)(avail / cellW));

        if (results.Count == 0)
        {
            ImGui.TextDisabled(desktop.Catalog.Scanning ? "Scanning applications…" : search.Length > 0 ? "No app matches." : "No applications found.");
            return;
        }

        var draw = ImGui.GetWindowDrawList();
        for (var i = 0; i < results.Count; i++)
        {
            var app = results[i];
            if (i % columns != 0)
                ImGui.SameLine(0, 0);
            ImGui.PushID(app.Id);
            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(cellW, cellH);
            var clicked = ImGui.InvisibleButton("##cell", new Vector2(cellW, cellH));
            var hovered = ImGui.IsItemHovered();
            if (i == selected && scrollToSelected)
            {
                ImGui.SetScrollHereY(0.5f);
                scrollToSelected = false;
            }

            // Only cells on screen touch the texture cache, so icons load lazily as they scroll into view.
            if (ImGui.IsRectVisible(min, max))
            {
                var launchable = CanLaunch(app, available);
                if (i == selected || hovered)
                    draw.AddRectFilled(min, max, ImGui.GetColorU32(i == selected ? ImGuiCol.HeaderActive : ImGuiCol.HeaderHovered), 6 * scale);
                var iconPos = new Vector2(min.X + ((cellW - iconSize) / 2), min.Y + (CellPadding * scale));
                DrawIcon(draw, app, iconPos, iconSize, dimmed: !launchable);
                var label = Fit(app.Name, cellW - (4 * scale));
                var textW = ImGui.CalcTextSize(label).X;
                var textPos = new Vector2(min.X + ((cellW - textW) / 2), iconPos.Y + iconSize + (CellPadding * scale));
                draw.AddText(textPos, ImGui.GetColorU32(launchable ? ImGuiCol.Text : ImGuiCol.TextDisabled), label);
            }

            if (clicked)
            {
                selected = i;
                Launch(app, available);
            }

            if (hovered)
                Tooltip(app);
            ContextMenu(app, available);
            ImGui.PopID();
        }
    }

    private void DrawFooter(AppCatalog catalog)
    {
        if (ImGui.Button("Reload"))
            desktop.Catalog.Reload();
        ImGui.SameLine();
        var status = desktop.Catalog.Scanning
            ? "scanning…"
            : $"{catalog.Apps.Count} apps · {catalog.Stats.IconsPng} icons · {results.Count} shown";
        if (flash != null && DateTime.UtcNow < flashUntil)
        {
            var color = flash.StartsWith("ok", StringComparison.Ordinal) ? ImGuiColors.HealerGreen : ImGuiColors.DalamudOrange;
            ImGui.TextColored(color, flash);
        }
        else
        {
            ImGui.TextDisabled(status);
        }
    }

    private void Tooltip(AppInfo app)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(app.Name + (desktop.IsFavourite(app.Id) ? "  ★" : ""));
        if (app.GenericName.Length > 0 && app.GenericName != app.Name)
            ImGui.TextDisabled(app.GenericName);
        if (app.Comment.Length > 0)
            ImGui.TextUnformatted(app.Comment);
        ImGui.Separator();
        ImGui.TextDisabled(app.Id);
        ImGui.TextDisabled("run: " + app.Command);
        if (app.Terminal)
            ImGui.TextColored(ImGuiColors.DalamudOrange, "Terminal=true: not launched by XivDesktop v0.");
        ImGui.TextDisabled("Right-click: favourite, copy command");
        ImGui.EndTooltip();
    }

    private void ContextMenu(AppInfo app, bool available)
    {
        if (!ImGui.BeginPopupContextItem("##ctx"))
            return;
        if (ImGui.MenuItem("Launch", "", false, CanLaunch(app, available)))
            Launch(app, available);
        if (ImGui.MenuItem(desktop.IsFavourite(app.Id) ? "Remove from favourites" : "Add to favourites"))
            desktop.ToggleFavourite(app.Id);
        if (ImGui.MenuItem("Copy command"))
            ImGui.SetClipboardText(app.Command);
        ImGui.EndPopup();
    }

    private static bool CanLaunch(AppInfo app, bool available) => available && !app.Terminal;

    private void DrawIcon(ImDrawListPtr draw, AppInfo app, Vector2 pos, float size, bool dimmed)
        => IconDrawer.App(textures, draw, app, pos, size, dimmed);

    private static string Fit(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
            return text;
        for (var n = text.Length - 1; n > 1; n--)
        {
            var candidate = text[..n].TrimEnd() + "…";
            if (ImGui.CalcTextSize(candidate).X <= width)
                return candidate;
        }

        return "…";
    }
}
