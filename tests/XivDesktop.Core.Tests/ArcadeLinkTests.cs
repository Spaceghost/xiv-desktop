using XivDesktop.Core.Palette;

namespace XivDesktop.Core.Tests;

/// <summary>The optional XivArcade mod, as the palette sees it over IPC.</summary>
public sealed class ArcadeLinkTests
{
    private const string Reply = """[{"id":"aaa","title":"Final Fantasy VII","subtitle":"PlayStation · 1997 · 3 discs","ready":true,"score":1000},{"id":"bbb","title":"Final Fantasy IX","subtitle":"PlayStation","ready":false,"score":480.5}]""";

    [Fact]
    public void ParsesTheSearchReply()
    {
        var hits = ArcadeLink.Parse(Reply);
        Assert.Equal(new ArcadeHit("aaa", "Final Fantasy VII", "PlayStation · 1997 · 3 discs", true, 1000), hits[0]);
        Assert.Equal(("bbb", false, 480.5), (hits[1].Id, hits[1].Ready, hits[1].Score));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("{}")]
    [InlineData("[1,null,{\"id\":\"\"},{\"title\":\"x\"},{\"id\":7,\"title\":\"x\"}]")]
    public void AnythingElseIsNoGames(string? json) => Assert.Empty(ArcadeLink.Parse(json));

    [Fact]
    public void PaletteShowsGamesOnlyWhenXivArcadeAnswersAndSomethingIsTyped()
    {
        var asked = new List<string>();
        var ctx = new PaletteContext
        {
            ArcadeSearch = q =>
            {
                asked.Add(q);
                return ArcadeLink.Parse(Reply);
            },
        };
        Assert.DoesNotContain(PaletteEngine.Build(ctx, ""), i => i.Command.Kind == PaletteCommand.Arcade);
        Assert.Empty(asked);

        var rows = PaletteEngine.Build(ctx, "ff7").Where(i => i.Command.Kind == PaletteCommand.Arcade).ToList();
        Assert.Equal(["ff7"], asked);
        Assert.Equal(("Final Fantasy VII", "aaa", "Arcade", true), (rows[0].Title, rows[0].Command.Arg, rows[0].Badge, rows[0].Enabled));
        Assert.False(rows[1].Enabled);
        Assert.Contains("XivArcade", rows[1].Reason, StringComparison.Ordinal);

        Assert.DoesNotContain(PaletteEngine.Build(new PaletteContext(), "ff7"), i => i.Command.Kind == PaletteCommand.Arcade);
    }
}
