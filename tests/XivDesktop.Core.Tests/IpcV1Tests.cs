using XivDesktop.Shared;

namespace XivDesktop.Core.Tests;

public class IpcV1Tests
{
    [Fact]
    public void WindowsPayloadRoundTripsInCamelCase()
    {
        var p = new WindowsPayload
        {
            Available = true,
            Rev = 7,
            Workspace = 2,
            Target = 12,
            Windows = [new WindowPayload { Id = 12, Title = "Yad", App = "yad", State = "live", Kind = "pet", Focused = true, Workspace = 2, AppId = "yad.desktop" }],
        };
        var json = IpcContract.Serialize(p);
        Assert.Contains("\"workspace\":2", json);
        Assert.Contains("\"appId\":\"yad.desktop\"", json);
        var back = IpcContract.ParseWindows(json)!;
        Assert.Equal(p.Windows[0], back.Windows[0]);
        Assert.Equal(12, back.Target);
        Assert.Null(IpcContract.ParseWindows("nope"));
    }

    [Fact]
    public void WindowActionParsesLooseInput()
    {
        var a = IpcContract.ParseWindowAction("""{"action":"place","id":12,"pin":"orbit 3.5"}""")!;
        Assert.Equal(("place", 12L, "orbit 3.5", 0), (a.Action, a.Id, a.Pin, a.Workspace));
        Assert.Equal(3, IpcContract.ParseWindowAction("""{"Action":"move","Workspace":3}""")!.Workspace);
        Assert.Null(IpcContract.ParseWindowAction("[]"));
    }

    [Fact]
    public void PaletteResultsParse()
    {
        var r = IpcContract.ParsePalette("""[{"provider":"calc","title":"4","subtitle":"2+2","score":1000,"enabled":true,"command":{"kind":"copy","arg":"4","id":0,"number":0}}]""");
        Assert.Equal("copy", Assert.Single(r).Command.Kind);
        Assert.Empty(IpcContract.ParsePalette("{"));
    }
}
