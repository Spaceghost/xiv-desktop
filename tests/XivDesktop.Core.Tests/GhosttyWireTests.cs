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
