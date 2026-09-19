using System.Text.Json;
using XivDesktop.Core.Input;
using XivDesktop.Core.Palette;
using XivDesktop.Core.Windows;

namespace XivDesktop.Core.Tests;

public class PanelTests
{
    // The documented panel.list shape (ghostty-dalamud, branch panel-ipc).
    private const string Fixture = """
        {"ok": true, "result": {"rev": 42, "panels": [
          {"id": 3, "kind": "terminal", "title": "btop", "profile": "bash", "view": "pin", "focused": false, "order": 2, "running": true},
          {"id": 12, "kind": "window", "title": "Mappy", "app": "mappy", "view": "pet", "focused": true, "order": 1, "icon": "mappy"},
          {"id": 7, "kind": "chat", "title": "", "view": "hud", "focused": false},
          {"id": 9, "kind": "adopted", "title": "Discord", "app": "discord", "view": "hidden", "focused": false, "order": 3},
          {"kind": "terminal", "title": "no id"}
        ]}}
        """;

    [Fact]
    public void ParsesTheDocumentedPanelList()
    {
        var s = Panels.ParseList(Fixture)!;
        Assert.True(s.Native);
        Assert.Equal(42, s.Rev);
        Assert.Equal([12L, 3, 9, 7], s.Panels.Select(p => p.Id)); // by order, unordered last
        var mappy = s.Panels[0];
        Assert.Equal(("window", "pet", true, "mappy", 1), (mappy.Kind, mappy.View, mappy.Focused, mappy.Icon, mappy.Order!.Value));
        Assert.True(s.Panels[1].Running);
        Assert.Equal("bash", s.Panels[1].Profile);
        Assert.Equal("chat 7", s.Panels[3].DisplayName);
        Assert.True(s.Panels[2].IsHidden);
    }

    [Theory]
    [InlineData("""{"ok":false,"error":"unknown method: panel.list"}""")]
    [InlineData("garbage")]
    public void OlderGhosttyGivesNull(string json) => Assert.Null(Panels.ParseList(json));

    [Fact]
    public void FallsBackToWindowList()
    {
        var w = new WindowListSnapshot
        {
            Rev = 5,
            Windows =
            [
                new WindowPanel { Id = 1, Title = "a", State = "live", Kind = "pet" },
                new WindowPanel { Id = 2, Title = "b", State = "live", Kind = "pin", Hidden = true },
                new WindowPanel { Id = 3, Title = "c", State = "ended", Kind = "pin" },
            ],
        };
        var s = Panels.FromWindows(w);
        Assert.False(s.Native);
        Assert.Equal([("window", "pet"), ("window", "hidden")], s.Panels.Select(p => (p.Kind, p.View)));
    }

    [Fact]
    public void BuildsPanelRequests()
    {
        string M(string j) => JsonDocument.Parse(j).RootElement.GetProperty("method").GetString()!;
        JsonElement P(string j) => JsonDocument.Parse(j).RootElement.GetProperty("params");
        Assert.Equal("panel.focus", M(Panels.Focus(3)));
        Assert.False(P(Panels.Focus(3)).TryGetProperty("fly", out _));
        Assert.True(P(Panels.Focus(3, fly: true)).GetProperty("fly").GetBoolean());
        Assert.Equal("panel.toggle_pet", M(Panels.TogglePet(3)));
        Assert.Equal("panel.minimize", M(Panels.Minimize(3)));
        Assert.Equal("panel.close", M(Panels.Close(3)));
        Assert.Equal("hud", P(Panels.Place(3, "hud")).GetProperty("pin").GetString());
        Assert.Equal("left", P(Panels.Order(3, "left")).GetProperty("to").GetString());
        Assert.Equal(2, P(Panels.Order(3, "2")).GetProperty("to").GetInt32());
    }

    private static readonly PaletteContext Ctx = new()
    {
        Apps =
        [
            new AppInfo { Id = "mappy.desktop", Name = "Mappy", Command = "mappy" },
            new AppInfo { Id = "firefox.desktop", Name = "Firefox", Command = "firefox" },
        ],
        Favourites = ["firefox.desktop"],
        Panels = Panels.ParseList(Fixture),
        WorkspaceOf = id => id == 12 ? 2 : 0,
    };

    [Fact]
    public void PanelsComeFirstOnAnEmptyQueryAndAppsBelow()
    {
        var r = PaletteEngine.Build(Ctx, "");
        Assert.Equal([PaletteProvider.Panel, PaletteProvider.Panel, PaletteProvider.Panel, PaletteProvider.Panel], r.Take(4).Select(i => i.Provider));
        Assert.Equal("Mappy", r[0].Title); // focused, and first in ghostty's order
        Assert.Equal("Discord", r[3].Title); // hidden sinks
        Assert.Equal("Window", r[0].BadgeText);
        Assert.Contains("workspace 2", r[0].Subtitle);
        Assert.Contains(r.Skip(4), i => i.Provider == PaletteProvider.App && i.Title == "Firefox");
        Assert.Equal(new PaletteCommand(PaletteCommand.Focus, Id: 12), r[0].Command);
    }

    [Fact]
    public void WindowsPrefixListsOnlyPanelsFuzzily()
    {
        var r = PaletteEngine.Build(Ctx, "w:");
        Assert.All(r, i => Assert.Equal(PaletteProvider.Panel, i.Provider));
        Assert.Equal(4, r.Count);
        var bt = PaletteEngine.Build(Ctx, "w:btp");
        Assert.Equal("btop", Assert.Single(bt).Title);
        Assert.Equal("Terminal", bt[0].BadgeText);
        Assert.Equal(3L, PaletteEngine.Build(Ctx, "w:bash").Single().WindowId); // matched by profile
        Assert.Equal(7L, PaletteEngine.Build(Ctx, "w:hud").Single().WindowId);  // matched by view
    }

    [Fact]
    public void TypedQueriesRankTheOpenPanelAboveTheApp()
    {
        var r = PaletteEngine.Build(Ctx, "mappy");
        Assert.Equal(PaletteProvider.Panel, r[0].Provider);
        Assert.Equal(PaletteProvider.App, r[1].Provider);
    }

    [Fact]
    public void RowActionsAndTheirChords()
    {
        var mappy = PaletteEngine.Build(Ctx, "")[0];
        var acts = PaletteEngine.RowActions(mappy);
        Assert.Equal(["Focus", "Pin", "HUD", "Minimize", "Close", "Left", "Right", "First", "Last"], acts.Select(a => a.Label));
        Assert.Equal(["Enter", "Ctrl+P", "Ctrl+H", "Ctrl+M", "Ctrl+W", "Alt+Left", "Alt+Right", "Alt+Home", "Alt+End"], acts.Select(a => a.Hint));
        Assert.Equal(new PaletteCommand(PaletteCommand.TogglePet, Id: 12), PaletteEngine.ActionFor(mappy, KeyChord.Parse("Ctrl+P"))!.Command);
        Assert.Equal(new PaletteCommand(PaletteCommand.Order, "first", 12), PaletteEngine.ActionFor(mappy, KeyChord.Parse("Alt+Home"))!.Command);
        Assert.Equal(new PaletteCommand(PaletteCommand.Place, "hud", 12), PaletteEngine.ActionFor(mappy, KeyChord.Parse("Ctrl+H"))!.Command);
        Assert.Null(PaletteEngine.ActionFor(mappy, KeyChord.Parse("Ctrl+Q")));

        var app = PaletteEngine.Build(Ctx, "a:firefox")[0];
        Assert.Equal("Launch", Assert.Single(PaletteEngine.RowActions(app)).Label);
        Assert.Null(PaletteEngine.ActionFor(app, KeyChord.Parse("Ctrl+W")));
    }

    [Fact]
    public void WithoutPanelListWindowRowsGetTheSameActions()
    {
        var ctx = Ctx with { Panels = null, Windows = [new WindowPanel { Id = 5, Title = "x", State = "live", Kind = "pet" }] };
        var row = PaletteEngine.Build(ctx, "w:")[0];
        Assert.Equal(PaletteProvider.Window, row.Provider);
        Assert.Equal(9, PaletteEngine.RowActions(row).Count);
    }
}
