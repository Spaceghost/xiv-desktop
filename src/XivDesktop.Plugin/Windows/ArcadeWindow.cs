using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using XivDesktop.Core;
using XivDesktop.Core.Arcade;
using XivDesktop.Plugin.Arcade;

namespace XivDesktop.Plugin.Windows;

/// <summary>
/// The Arcade: the player's own classic games, in the launcher's visual language (dark glass, rounded tiles,
/// a search box that has the keyboard). With no games it is a three-step welcome whose checks turn green by
/// themselves, above a Final Fantasy shelf of text-only placeholders that light up as files appear. Arrows
/// move, Enter plays, F5 rescans, F6 syncs saves, Esc closes.
/// </summary>
public sealed class ArcadeWindow : Window
{
    private static readonly Vector4 Accent = new(0.60f, 0.50f, 1.00f, 1f);
    private static readonly Vector4 Background = new(0.07f, 0.075f, 0.095f, 0.92f);
    private static readonly Vector4 Good = new(0.45f, 0.86f, 0.58f, 1f);
    private static readonly Vector4 Busy = new(0.45f, 0.70f, 1.00f, 1f);
    private static readonly Vector4 Attention = new(1.00f, 0.72f, 0.35f, 1f);

    private readonly ArcadeService arcade;
    private readonly ITextureProvider textures;
    private readonly Func<HostPaths> paths;

    private readonly List<Tile> tiles = [];
    private string search = "";
    private string launchBoxPath = "";
    private int selected;
    private int columns = 1;
    private bool focusSearch;
    private bool scrollToSelected;
    private bool showSync;
    private string? flash;
    private DateTime flashUntil;
    private int pushedColors;
    private int pushedVars;

    public ArcadeWindow(ArcadeService arcade, ITextureProvider textures, Func<HostPaths> paths)
        : base("Arcade###XivDesktopArcade")
    {
        this.arcade = arcade;
        this.textures = textures;
        this.paths = paths;
        Size = new Vector2(760, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(440, 320), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

    private sealed record Tile(string Title, string Line, ArcadeGame? Game, string Missing);

    public void Show(string initialSearch = "")
    {
        search = initialSearch;
        IsOpen = true;
        BringToFront();
    }

    public void ShowSetup()
    {
        showSync = true;
        Show();
    }

    public override void OnOpen()
    {
        focusSearch = true;
        selected = 0;
        arcade.Watching = true;
        Flash(arcade.Refresh());
    }

    public override void OnClose() => arcade.Watching = false;

    public void Flash(string text)
    {
        if (text == "ok")
            return;
        flash = text;
        flashUntil = DateTime.UtcNow.AddSeconds(8);
    }

    public override void PreDraw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 16 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 10 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 8) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 6 * scale);
        pushedVars = 5;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Background);
        ImGui.PushStyleColor(ImGuiCol.Border, Accent with { W = 0.35f });
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(1, 1, 1, 0.06f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Button, Accent with { W = 0.22f });
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Accent with { W = 0.38f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Accent with { W = 0.50f });
        ImGui.PushStyleColor(ImGuiCol.NavHighlight, Vector4.Zero);
        pushedColors = 9;
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(pushedColors);
        ImGui.PopStyleVar(pushedVars);
        pushedColors = pushedVars = 0;
    }

    public override void Draw()
    {
        var state = arcade.State;
        var scale = ImGuiHelpers.GlobalScale;

        var searchActive = DrawHeader(state, scale);
        BuildTiles(state);
        HandleKeys(searchActive);

        var footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        if (ImGui.BeginChild("##arcade-body", new Vector2(0, -footer), false))
        {
            if (!arcade.HostReachable)
                ImGui.TextColored(Attention, arcade.HostProblem.Length > 0 ? arcade.HostProblem : "The host agent is not connected, so nothing can be checked or started yet.");
            if (search.Length > 0)
            {
                DrawTiles("Matches", 0, tiles.Count, scale, "Nothing in your library matches.");
            }
            else
            {
                if (!state.SetupDone)
                    DrawWelcome(state, scale);
                if (showSync || (state.SetupDone && state.Sync.Steps.Count > 0))
                    DrawSync(state, scale);
                DrawLibrary(state, scale);
            }
        }

        ImGui.EndChild();
        DrawFooter(state);
    }

    /// <summary>The search box and the save-sync pill. Returns whether the search box has the keyboard.</summary>
    private bool DrawHeader(ArcadeState state, float scale)
    {
        var sync = state.Sync;
        var pill = "●  " + (state.PlayingTitle is { } playing ? "Playing " + playing : sync.Label);
        var pillWidth = ImGui.CalcTextSize(pill).X + (24 * scale);
        if (focusSearch)
        {
            ImGui.SetKeyboardFocusHere();
            focusSearch = false;
        }

        ImGui.SetNextItemWidth(-(pillWidth + (8 * scale)));
        var before = search;
        if (ImGui.InputTextWithHint("##arcade-search", "Search your games…   Enter plays · F5 rescans · F6 syncs saves", ref search, 128, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            focusSearch = true;
            Activate();
        }

        if (before != search)
            selected = 0;
        var active = ImGui.IsItemActive();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Tone(sync));
        if (ImGui.Button(pill + "##sync", new Vector2(pillWidth, 0)))
            showSync = !showSync;
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted("Save sync: " + sync.Label);
            if (sync.Detail.Length > 0)
                ImGui.TextDisabled(sync.Detail);
            ImGui.TextDisabled("Click for details. F6 syncs now. A game never starts while its saves are still arriving.");
            ImGui.EndTooltip();
        }

        return active;
    }

    private void BuildTiles(ArcadeState state)
    {
        tiles.Clear();
        if (search.Length > 0)
        {
            foreach (var g in state.Games
                .Select(g => (Game: g, Score: ArcadeCommands.Score(g.Title, search)))
                .Where(x => x.Score != null)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Game))
            {
                tiles.Add(GameTile(state, g));
            }

            selected = Math.Clamp(selected, 0, Math.Max(0, tiles.Count - 1));
            return;
        }

        foreach (var e in state.Shelf)
        {
            var game = e.Games.Select(state.Game).Where(g => g != null).OrderByDescending(g => g!.LastPlayed ?? 0).FirstOrDefault();
            tiles.Add(new Tile(e.Title, $"{e.Platforms} · {e.Year}", game, "Not in your library yet. Drop your own copy into a console folder under " + state.GamesRoot + "."));
        }

        foreach (var (_, games) in state.EverythingElse())
        {
            foreach (var g in games)
                tiles.Add(GameTile(state, g));
        }

        selected = Math.Clamp(selected, 0, Math.Max(0, tiles.Count - 1));
    }

    private static Tile GameTile(ArcadeState state, ArcadeGame g)
        => new(g.Title, state.SystemName(g.System) + (g.Year is { } y ? $" · {y}" : "") + (g.Discs > 1 ? $" · {g.Discs} discs" : ""), g, "");

    private void HandleKeys(bool searchActive)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.F5))
            Flash(Describe(arcade.Refresh(), "Rescanning your games folder…"));
        if (ImGui.IsKeyPressed(ImGuiKey.F6))
            Flash(Describe(arcade.Run(new ArcadeRequest(ArcadeVerb.Sync)), "Syncing saves…"));
        if (ImGui.IsKeyPressed(ImGuiKey.Escape) && !ImGui.IsAnyItemActive())
            IsOpen = false;
        if (tiles.Count == 0)
            return;
        var before = selected;
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
            selected = Math.Min(tiles.Count - 1, selected + columns);
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
            selected = Math.Max(0, selected - columns);
        if (!searchActive || search.Length == 0)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
                selected = Math.Min(tiles.Count - 1, selected + 1);
            if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
                selected = Math.Max(0, selected - 1);
        }

        if (!searchActive && (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)))
            Activate();
        scrollToSelected |= before != selected;
    }

    private void Activate()
    {
        if (selected < 0 || selected >= tiles.Count)
            return;
        var tile = tiles[selected];
        Flash(tile.Game is { } game ? arcade.Play(game) : tile.Missing);
    }

    private static string Describe(string result, string ok) => result == "ok" ? ok : result;

    // Welcome -----------------------------------------------------------------------------------

    private void DrawWelcome(ArcadeState state, float scale)
    {
        ImGui.TextColored(Accent, "Welcome to the Arcade");
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted("Your own classic games, running on your Linux desktop and shown here as a game panel, with every save kept in step across your machines. Three steps; each one ticks itself off.");
        ImGui.PopTextWrapPos();

        if (!state.Loaded)
        {
            ImGui.TextDisabled(arcade.HostReachable ? "Preparing your folders…" : "Waiting for the host agent.");
        }

        for (var i = 0; i < state.Checks.Count; i++)
        {
            var c = state.Checks[i];
            DrawStep(i + 1, c.Title, c.Detail, c.Ok, c.Command, scale);
            if (c.Key == "folder")
                DrawLaunchBox(state, scale);
        }

        if (ImGui.Button("Rescan  (F5)"))
            Flash(Describe(arcade.Refresh(), "Rescanning your games folder…"));
        ImGui.SameLine();
        ImGui.TextDisabled("New files show up by themselves while this window is open.");
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled(state.Legal);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
    }

    private void DrawStep(int number, string title, string detail, bool ok, string command, float scale)
    {
        ImGui.PushID(number);
        var draw = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var r = 11 * scale;
        var centre = pos + new Vector2(r, r + (2 * scale));
        if (ok)
        {
            draw.AddCircleFilled(centre, r, ImGui.GetColorU32(Good));
            draw.AddLine(centre + new Vector2(-5, 0) * scale, centre + new Vector2(-1.5f, 4) * scale, 0xFF101010, 2.2f * scale);
            draw.AddLine(centre + new Vector2(-1.5f, 4) * scale, centre + new Vector2(5.5f, -4) * scale, 0xFF101010, 2.2f * scale);
        }
        else
        {
            draw.AddCircle(centre, r, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.35f)), 24, 1.6f * scale);
            var label = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            draw.AddText(centre - (ImGui.CalcTextSize(label) / 2), ImGui.GetColorU32(ImGuiCol.TextDisabled), label);
        }

        ImGui.Dummy(new Vector2(r * 2, r * 2));
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextColored(ok ? Good : new Vector4(1, 1, 1, 0.95f), title);
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled(detail);
        ImGui.PopTextWrapPos();
        if (!ok && command.Length > 0)
            CopyRow(command, scale);
        ImGui.EndGroup();
        ImGui.PopID();
    }

    private void DrawLaunchBox(ArcadeState state, float scale)
    {
        ImGui.Indent(30 * scale);
        ImGui.PushID("launchbox");
        if (state.LaunchBoxFound.Count > 0)
        {
            ImGui.TextColored(Good, "LaunchBox library found: " + string.Join(", ", state.LaunchBoxFound));
        }
        else
        {
            ImGui.TextDisabled("I have a LaunchBox folder: copy it into the games folder as it is, or point at it here.");
            ImGui.SetNextItemWidth(Math.Max(160 * scale, ImGui.GetContentRegionAvail().X - (110 * scale)));
            var enter = ImGui.InputTextWithHint("##lb", "/path/to/LaunchBox (a Linux path)", ref launchBoxPath, 512, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if ((ImGui.Button("Use it") || enter) && launchBoxPath.Trim().Length > 0)
                Flash(Describe(arcade.Run(new ArcadeRequest(ArcadeVerb.LaunchBox, launchBoxPath.Trim())), "Reading your LaunchBox library…"));
        }

        ImGui.PopID();
        ImGui.Unindent(30 * scale);
    }

    private void CopyRow(string command, float scale)
    {
        // The helper is not on PATH unless the player put it there; the copy that the plugin keeps always works.
        if (command.StartsWith("xiv-arcade ", StringComparison.Ordinal))
            command = "python3 " + ArcadeCommands.Quote(arcade.HelperPath) + command["xiv-arcade".Length..];

        if (ImGui.SmallButton("Copy"))
        {
            ImGui.SetClipboardText(command);
            Flash("ok: copied. Paste it into a terminal on your Linux desktop.");
        }

        ImGui.SameLine();
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(Accent with { W = 0.9f }, command);
        ImGui.PopTextWrapPos();
    }

    // Save sync ---------------------------------------------------------------------------------

    private void DrawSync(ArcadeState state, float scale)
    {
        var sync = state.Sync;
        ImGui.TextColored(Tone(sync), "Save sync · " + sync.Label);
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled(sync.Detail);
        if (state.SyncRoot.Length > 0)
            ImGui.TextDisabled("Every save and state lives in " + state.SyncRoot + ". Syncthing copies that folder between your machines, keeps old versions, and XivDesktop never starts it or pairs anything for you.");
        ImGui.PopTextWrapPos();
        for (var i = 0; i < sync.Steps.Count; i++)
            DrawStep(i + 1, sync.Steps[i].Text, "", false, sync.Steps[i].Command, scale);
        foreach (var kept in sync.Conflicts)
            ImGui.TextColored(Attention, "Both saves kept: " + kept);
        if (ImGui.Button("Sync now  (F6)"))
            Flash(Describe(arcade.Run(new ArcadeRequest(ArcadeVerb.Sync)), "Syncing saves…"));
        ImGui.Spacing();
    }

    // Library -----------------------------------------------------------------------------------

    private void DrawLibrary(ArcadeState state, float scale)
    {
        var shelf = state.Shelf.Count;
        if (shelf > 0)
        {
            var lit = state.Shelf.Count(e => e.Games.Count > 0);
            DrawTiles($"Final Fantasy  ·  {lit} of {shelf} in your library", 0, shelf, scale, "");
        }

        var index = shelf;
        foreach (var (system, games) in state.EverythingElse())
        {
            DrawTiles(index == shelf ? "Everything else  ·  " + system.Name : system.Name, index, games.Count, scale, "");
            index += games.Count;
        }
    }

    private void DrawTiles(string heading, int first, int count, float scale, string empty)
    {
        ImGui.TextDisabled(heading);
        if (count == 0)
        {
            if (empty.Length > 0)
                ImGui.TextDisabled(empty);
            return;
        }

        var cellW = 168 * scale;
        var cellH = 86 * scale;
        var gap = 8 * scale;
        columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + gap) / (cellW + gap)));
        var draw = ImGui.GetWindowDrawList();
        for (var n = 0; n < count; n++)
        {
            var i = first + n;
            var tile = tiles[i];
            if (n % columns != 0)
                ImGui.SameLine(0, gap);
            ImGui.PushID(i);
            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(cellW, cellH);
            var clicked = ImGui.InvisibleButton("##tile", new Vector2(cellW, cellH));
            var hovered = ImGui.IsItemHovered();
            if (i == selected && scrollToSelected)
            {
                ImGui.SetScrollHereY(0.5f);
                scrollToSelected = false;
            }

            if (ImGui.IsRectVisible(min, max))
                DrawTile(draw, tile, min, max, i == selected, hovered, scale);
            if (clicked)
            {
                selected = i;
                Activate();
            }

            if (hovered)
                TileTooltip(tile);
            ImGui.PopID();
        }
    }

    private void DrawTile(ImDrawListPtr draw, Tile tile, Vector2 min, Vector2 max, bool isSelected, bool hovered, float scale)
    {
        var lit = tile.Game != null;
        var ready = tile.Game is { Ready: true };
        var rounding = 10 * scale;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(lit ? Accent with { W = hovered ? 0.30f : 0.18f } : new Vector4(1, 1, 1, hovered ? 0.07f : 0.035f)), rounding);
        if (isSelected)
            draw.AddRect(min, max, ImGui.GetColorU32(Accent), rounding, ImDrawFlags.None, 2f * scale);
        else if (lit)
            draw.AddRect(min, max, ImGui.GetColorU32(Accent with { W = 0.45f }), rounding, ImDrawFlags.None, 1f);

        var pad = 10 * scale;
        var textMin = min + new Vector2(pad, pad);
        var wrap = max.X - min.X - (pad * 2);
        if (tile.Game?.Boxart is { } art)
        {
            var image = textures.GetFromFile(paths().ToLocal(art)).GetWrapOrDefault();
            if (image != null)
            {
                var h = max.Y - min.Y - (pad * 2);
                var w = Math.Min(h * image.Width / Math.Max(1, image.Height), h * 1.2f);
                draw.AddImage(image.Handle, textMin, textMin + new Vector2(w, h), Vector2.Zero, Vector2.One, ready ? 0xFFFFFFFFu : 0x80FFFFFFu);
                textMin.X += w + (8 * scale);
                wrap -= w + (8 * scale);
            }
        }

        var titleColor = ImGui.GetColorU32(lit ? new Vector4(1, 1, 1, ready ? 1f : 0.7f) : new Vector4(1, 1, 1, 0.38f));
        var font = ImGui.GetFont();
        var size = ImGui.GetFontSize();
        var y = textMin.Y;
        foreach (var line in WrapLines(tile.Title, wrap, 2))
        {
            draw.AddText(font, size, new Vector2(textMin.X, y), titleColor, line);
            y += size + (2 * scale);
        }

        var small = size * 0.82f;
        var sub = lit && !ready ? "needs its emulator core" : tile.Line;
        var subColor = ImGui.GetColorU32(lit && !ready ? Attention : new Vector4(1, 1, 1, lit ? 0.62f : 0.28f));
        if (WrapLines(sub, wrap / 0.82f, 1).FirstOrDefault() is { } subLine)
            draw.AddText(font, small, new Vector2(textMin.X, Math.Max(y, max.Y - pad - small)), subColor, subLine);
    }

    private void TileTooltip(Tile tile)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(tile.Title);
        ImGui.TextDisabled(tile.Line);
        ImGui.Separator();
        if (tile.Game is { } g)
        {
            ImGui.TextDisabled(g.Path);
            if (g.LastPlayed is { } t)
                ImGui.TextDisabled("Last played " + DateTimeOffset.FromUnixTimeSeconds((long)t).LocalDateTime.ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.CurrentCulture));
            ImGui.TextUnformatted(g.Ready ? "Enter or click to play. Saves are checked first and pushed when you close it." : "Its emulator core is not installed yet; see /arcade setup.");
        }
        else
        {
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24);
            ImGui.TextUnformatted(tile.Missing);
            ImGui.TextDisabled(ArcadeText.Legal);
            ImGui.PopTextWrapPos();
        }

        ImGui.EndTooltip();
    }

    private static IEnumerable<string> WrapLines(string text, float width, int maxLines)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = "";
        var lines = 0;
        foreach (var word in words)
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && ImGui.CalcTextSize(candidate).X > width)
            {
                lines++;
                if (lines == maxLines)
                {
                    yield return line.TrimEnd() + "…";
                    yield break;
                }

                yield return line;
                line = word;
            }
            else
            {
                line = candidate;
            }
        }

        if (line.Length > 0)
            yield return line;
    }

    private void DrawFooter(ArcadeState state)
    {
        if (ImGui.Button("Rescan"))
            Flash(Describe(arcade.Refresh(), "Rescanning your games folder…"));
        ImGui.SameLine();
        if (ImGui.Button(showSync ? "Hide save sync" : "Save sync"))
            showSync = !showSync;
        ImGui.SameLine();
        if (flash != null && DateTime.UtcNow < flashUntil)
        {
            var bad = flash.StartsWith("error", StringComparison.Ordinal);
            ImGui.TextColored(bad ? ImGuiColors.DalamudOrange : ImGuiColors.HealerGreen, bad ? flash[7..] : flash.StartsWith("ok: ", StringComparison.Ordinal) ? flash[4..] : flash);
        }
        else
        {
            ImGui.TextDisabled(state.Message.Length > 0 ? state.Message : $"{state.Games.Count} games · saves: {state.Sync.Label}");
        }

        ImGui.TextDisabled("Bring your own games: nothing is downloaded, and only folders you choose are read.");
    }

    private static Vector4 Tone(ArcadeSync sync) => sync.Tone switch
    {
        "ok" => Good,
        "busy" => Busy,
        _ => Attention,
    };
}
