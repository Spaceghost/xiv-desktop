using System.Text.Json;
using XivDesktop.Core.Windows;

namespace XivDesktop.Core.Tests;

public class GhosttyWireTests
{
    // The documented window.list reply (ghostty-dalamud docs/IPC.md).
    private const string ListReply = """
        {"ok": true, "result": {
          "rev": 7,
          "windows": [
            {"id": 12, "sid": 5, "title": "Yad Window", "app": "yad", "w": 640, "h": 400,
             "state": "live", "kind": "pet", "focused": false}
          ],
          "requests": [
            {"request": 1726732800001, "method": "window.open", "ok": true, "result": {"id": 12}},
            {"request": 1726732800002, "method": "window.place", "ok": false, "error": "pin: usage: …"}
          ]
        }}
        """;

    [Fact]
    public void ParsesTheDocumentedWindowList()
    {
        var s = GhosttyWire.ParseWindowList(ListReply)!;
        Assert.Equal(7, s.Rev);
        var w = Assert.Single(s.Windows);
        Assert.Equal(12, w.Id);
        Assert.Equal(5, w.Sid);
        Assert.Equal("Yad Window", w.Title);
        Assert.Equal("yad", w.App);
        Assert.Equal((640, 400), (w.W, w.H));
        Assert.True(w.IsLive);
        Assert.True(w.IsPet);
        Assert.False(w.Focused);

        Assert.Equal(2, s.Requests.Count);
        Assert.Equal(1726732800001, s.Requests[0].Request);
        Assert.True(s.Requests[0].Ok);
        Assert.Equal(12, s.Requests[0].PanelId);
        Assert.False(s.Requests[1].Ok);
        Assert.Equal("window.place", s.Requests[1].Method);
        Assert.StartsWith("pin: usage", s.Requests[1].Error);
    }

    [Fact]
    public void PeekRevReadsOnlyTheRevision()
    {
        Assert.Equal(7, GhosttyWire.PeekRev(GhosttyWire.ParseReply(ListReply)));
        Assert.Null(GhosttyWire.PeekRev(GhosttyWire.ParseReply("""{"ok":false,"error":"x"}""")));
    }

    [Theory]
    [InlineData("""{"ok":false,"error":"ghostty is starting; ask again after the next frame"}""", "ghostty is starting; ask again after the next frame")]
    [InlineData("""{"ok":false}""", "ghostty refused the call")]
    [InlineData("not json", null)]
    [InlineData("", "empty reply")]
    [InlineData("[1,2]", "reply is not an object")]
    public void FailedRepliesNeverThrow(string json, string? error)
    {
        var r = GhosttyWire.ParseReply(json);
        Assert.False(r.Ok);
        if (error != null)
            Assert.Equal(error, r.Error);
        else
            Assert.StartsWith("malformed reply", r.Error);
        Assert.Null(GhosttyWire.ParseWindowList(json));
    }

    [Fact]
    public void SkipsMalformedEntriesAndDefaultsMissingFields()
    {
        var s = GhosttyWire.ParseWindowList("""
            {"ok":true,"result":{"rev":3,"windows":[{"title":"no id"},7,{"id":4,"state":"pending"}],"requests":[{"ok":true}]}}
            """)!;
        var w = Assert.Single(s.Windows);
        Assert.Equal(4, w.Id);
        Assert.Equal("", w.Title);
        Assert.Equal("window 4", w.DisplayName);
        Assert.Empty(s.Requests);
    }

    [Fact]
    public void QueuedReplyGivesTheRequestId()
    {
        var r = GhosttyWire.ParseReply("""{"ok": true, "result": {"queued": true, "request": 1726732800001}}""");
        Assert.Equal(1726732800001, GhosttyWire.ParseQueued(r));
    }

    [Fact]
    public void ParsesAgentStatus()
    {
        var a = GhosttyWire.ParseAgentStatus("""{"ok": true, "result": {"connected": true, "version": 3, "windows_ok": true, "agent": "127.0.0.1:7777"}}""")!;
        Assert.Equal(new AgentStatus(true, 3, true, "127.0.0.1:7777"), a);
    }

    [Fact]
    public void RequestsCarryMethodParamsAndCaller()
    {
        using var open = JsonDocument.Parse(GhosttyWire.Open(run: "yad --calendar", pin: " here "));
        Assert.Equal("window.open", open.RootElement.GetProperty("method").GetString());
        Assert.Equal("yad --calendar", open.RootElement.GetProperty("params").GetProperty("run").GetString());
        Assert.Equal("here", open.RootElement.GetProperty("params").GetProperty("pin").GetString());
        Assert.Equal("XivDesktop", open.RootElement.GetProperty("caller").GetString());

        // run wins; match and wid are not sent alongside it (the gate takes at most one).
        using var one = JsonDocument.Parse(GhosttyWire.Open(run: "a", match: "b", wid: 3));
        Assert.False(one.RootElement.GetProperty("params").TryGetProperty("match", out _));
        Assert.False(one.RootElement.GetProperty("params").TryGetProperty("wid", out _));

        using var place = JsonDocument.Parse(GhosttyWire.Place(12, "orbit 3.5"));
        Assert.Equal(12, place.RootElement.GetProperty("params").GetProperty("id").GetInt64());
        Assert.Equal("orbit 3.5", place.RootElement.GetProperty("params").GetProperty("pin").GetString());

        using var list = JsonDocument.Parse(GhosttyWire.List());
        Assert.False(list.RootElement.TryGetProperty("params", out _));
    }

    [Fact]
    public void DiffReportsOpenedAndEnded()
    {
        var pending = new WindowPanel { Id = 1, App = "yad", State = "pending", Kind = "pet" };
        var live = pending with { State = "live", Title = "Calendar" };
        var other = new WindowPanel { Id = 2, Title = "foot", State = "live", Kind = "pin" };
        var s0 = new WindowListSnapshot { Rev = 1, Windows = [pending, other] };
        var s1 = new WindowListSnapshot { Rev = 2, Windows = [live, other] };
        var s2 = new WindowListSnapshot { Rev = 3, Windows = [live with { State = "ended", Title = "" }] };

        var e1 = WindowDiff.Diff(s0, s1);
        Assert.Equal(WindowEventKind.Opened, Assert.Single(e1).Kind);

        var e2 = WindowDiff.Diff(s1, s2);
        Assert.Equal(2, e2.Count);
        Assert.All(e2, e => Assert.Equal(WindowEventKind.Ended, e.Kind));
        Assert.Contains(e2, e => e.Window.Title == "Calendar"); // the ended panel keeps its last title
        Assert.Contains(e2, e => e.Window.Id == 2);             // vanished without "ended"

        Assert.Empty(WindowDiff.Diff(s2, s2));
        Assert.Empty(WindowDiff.Diff(s2, WindowListSnapshot.Empty)); // already ended: no second event
    }
}

public class WindowAppsTests
{
    private static readonly AppInfo[] Apps =
    [
        new() { Id = "org.gnome.TextEditor.desktop", Name = "Text Editor", Command = "gnome-text-editor" },
        new() { Id = "yad-calendar.desktop", Name = "Calendar", Command = "/usr/bin/yad --calendar" },
        new() { Id = "org.mozilla.firefox.desktop", Name = "Firefox", Command = "'/usr/bin/flatpak' run org.mozilla.firefox" },
        new() { Id = "foot.desktop", Name = "Foot", Command = "foot" },
    ];

    [Theory]
    [InlineData("yad", "", "yad-calendar.desktop")]
    [InlineData("gnome-text-editor", "", "org.gnome.TextEditor.desktop")]
    [InlineData("firefox", "", "org.mozilla.firefox.desktop")]
    [InlineData("", "notes.txt - Text Editor", "org.gnome.TextEditor.desktop")]
    [InlineData("", "Foot", "foot.desktop")]
    [InlineData("unknown", "whatever", null)]
    [InlineData("flatpak", "Mozilla Firefox - Firefox", "org.mozilla.firefox.desktop")]
    [InlineData("flatpak", "", null)]
    public void GuessesTheApp(string app, string title, string? id)
        => Assert.Equal(id, WindowApps.Find(Apps, new WindowPanel { Id = 1, App = app, Title = title })?.Id);

    [Fact]
    public void RememberedLaunchWins()
        => Assert.Equal("foot.desktop", WindowApps.Find(Apps, new WindowPanel { App = "yad" }, "foot.desktop")!.Id);

    [Theory]
    [InlineData("/usr/bin/yad --calendar", "yad")]
    [InlineData("'/opt/My App/run' --x", "run")]
    [InlineData("  foot ", "foot")]
    [InlineData("", "")]
    public void ProgramOfACommand(string cmd, string prog) => Assert.Equal(prog, WindowApps.Program(cmd));
}

public class GhosttyWireV11Tests
{
    [Fact]
    public void ParsesTheNewWindowFields()
    {
        var s = GhosttyWire.ParseWindowList("""
            {"ok": true, "result": {"rev": 7, "windows": [
              {"id": 12, "sid": 5, "title": "Yad Window", "app": "yad", "w": 640, "h": 400,
               "state": "live", "kind": "pet", "anchor": "pet", "hidden": false, "focused": false,
               "agent": "default", "key": "yad-1"},
              {"id": 13, "state": "live", "kind": "pin", "hidden": true}], "requests": []}}
            """)!;
        Assert.Equal(("pet", false, "default", "yad-1"), (s.Windows[0].Anchor, s.Windows[0].Hidden, s.Windows[0].Agent, s.Windows[0].Key));
        Assert.True(s.Windows[1].Hidden);
        Assert.Null(GhosttyWire.ParseWindowList("""{"ok":true,"result":{"rev":1,"windows":[{"id":1}]}}""")!.Windows[0].Hidden);
    }

    [Fact]
    public void ParsesAgentAppsFocusAndStatus()
    {
        var apps = GhosttyWire.ParseAgentApps("""{"ok": true, "result": [{"id": "foot", "name": "Foot", "icon": "foot", "categories": "System;TerminalEmulator;"}, {"name": "no id"}, {"id": "x"}]}""");
        Assert.Equal(2, apps.Count);
        Assert.Equal(new[] { "System", "TerminalEmulator" }, apps[0].Categories);
        Assert.Equal("x", apps[1].Name);
        Assert.Empty(GhosttyWire.ParseAgentApps("""{"ok":true,"result":[]}"""));

        Assert.Equal(new FocusInfo(12, "window"), GhosttyWire.ParseFocus("""{"ok": true, "result": {"id": 12, "kind": "window"}}"""));
        Assert.Equal(0, GhosttyWire.ParseFocus("""{"ok": true, "result": {"id": 0}}""")!.Id);
        Assert.Equal(2, GhosttyWire.ParseAgentStatus("""{"ok": true, "result": {"connected": true, "version": 3, "windows_ok": true, "agent": "a", "window_lists": 2}}""")!.WindowLists);
    }

    [Fact]
    public void BuildsTheNewRequests()
    {
        using var hide = JsonDocument.Parse(GhosttyWire.Hide(12, true));
        Assert.Equal("window.hide", hide.RootElement.GetProperty("method").GetString());
        Assert.True(hide.RootElement.GetProperty("params").GetProperty("hidden").GetBoolean());
        using var term = JsonDocument.Parse(GhosttyWire.TerminalNew(null, "pet"));
        Assert.False(term.RootElement.GetProperty("params").TryGetProperty("profile", out _));
        Assert.Equal("pet", term.RootElement.GetProperty("params").GetProperty("pin").GetString());
        using var cyc = JsonDocument.Parse(GhosttyWire.FocusCycle(-1));
        Assert.Equal("prev", cyc.RootElement.GetProperty("params").GetProperty("dir").GetString());
        using var tp = JsonDocument.Parse(GhosttyWire.TogglePet(3));
        Assert.Equal("window.toggle_pet", tp.RootElement.GetProperty("method").GetString());
        using var open = JsonDocument.Parse(GhosttyWire.Open(match: GhosttyWire.AppMatch("foot")));
        Assert.Equal("app:foot", open.RootElement.GetProperty("params").GetProperty("match").GetString());
    }

    [Fact]
    public void AgentCatalogIsThePrimaryListWithStableIds()
    {
        var scanned = new AppCatalog(
            [new AppInfo { Id = "foot.desktop", Name = "Foot", Keywords = ["shell"], IconPath = "Z:\\scan\\foot.png", Command = "foot" }],
            new CatalogStats(), [], DateTimeOffset.MinValue);
        var apps = new List<AgentApp>
        {
            new("foot", "Foot", "", ["System"]),
            new("org.gnome.TextEditor.desktop", "Text Editor", "/home/u/.cache/ghostty-agent/icons/org.gnome.TextEditor.png", []),
            new("zed", "Zed", "zed", []),
            new("foot", "dup", "", []),
        };
        var c = AgentCatalog.Build(apps, AgentCatalog.IconMapper(HostPaths.WineMap, null), scanned);
        Assert.Equal(["Foot", "Text Editor", "Zed"], c.Apps.Select(a => a.Name));
        var foot = c.Find("foot")!;
        Assert.Equal(("foot.desktop", "foot", "Z:\\scan\\foot.png"), (foot.Id, foot.AgentId, foot.IconPath));
        Assert.Equal(["shell"], foot.Keywords);
        Assert.Equal("Z:\\home\\u\\.cache\\ghostty-agent\\icons\\org.gnome.TextEditor.png", c.Find("org.gnome.TextEditor")!.IconPath);
        Assert.Equal("org.gnome.TextEditor", c.Find("org.gnome.TextEditor")!.AgentId);
        Assert.Null(c.Find("zed")!.IconPath);
        Assert.Equal("app:zed", c.Find("zed")!.Command);

        // Agent apps never go through a shell command.
        Assert.Null(LaunchPlan.For(foot).Command);
        Assert.Equal(new LaunchTarget(LaunchKind.AgentApp, "foot"), LaunchPlan.Plan(foot).Target);
    }

    [Fact]
    public void TerminalAppsPlanAsTypedCommands()
    {
        var htop = new AppInfo { Id = "htop.desktop", Name = "htop", Command = "htop", Terminal = true };
        Assert.Equal(new LaunchTarget(LaunchKind.Terminal, "htop"), LaunchPlan.Plan(htop).Target);
        Assert.Null(LaunchPlan.For(htop).Command); // the Post fallback still refuses them
        Assert.Equal(new LaunchTarget(LaunchKind.Run, "foot"), LaunchPlan.Plan(new AppInfo { Id = "f", Name = "f", Command = "foot" }).Target);
        Assert.Equal(@"send #7 grep 'a\\b' x", LaunchPlan.SendLine(7, @"grep 'a\b' x"));
    }
}
