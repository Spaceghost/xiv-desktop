using XivDesktop.Shared;

namespace XivDesktop.Core.Tests;

public class SearchAndListsTests
{
    private static AppInfo App(string id, string name, string generic = "", params string[] keywords)
        => new() { Id = id + ".desktop", Name = name, GenericName = generic, Keywords = keywords, Command = id };

    private static readonly AppInfo[] Apps =
    [
        App("org.gnome.TextEditor", "Text Editor", "Text Editor", "Text", "Plaintext"),
        App("firefox", "Firefox", "Web Browser", "Internet", "WWW"),
        App("org.gnome.Terminal", "Terminal", "Terminal Emulator", "shell"),
        App("thunar", "Thunar", "File Manager"),
        App("org.gnome.Calculator", "Calculator", "", "math"),
    ];

    private static List<string> Names(string q, IReadOnlyCollection<string>? fav = null, IReadOnlyList<string>? rec = null)
        => AppSearch.Search(Apps, q, fav, rec).Select(a => a.Name).ToList();

    [Fact]
    public void EmptyQueryReturnsEverythingInOrder() => Assert.Equal(Apps.Select(a => a.Name), Names(""));

    [Fact]
    public void PrefixBeatsSubsequence()
    {
        // Prefix hits rank first, the shorter name ahead.
        var r = Names("te");
        Assert.Equal(["Terminal", "Text Editor"], r.Take(2));
        Assert.Equal("Firefox", Names("fox")[0]);
        Assert.True(AppSearch.Field("Terminal", "te") > AppSearch.Field("Thunar", "te"));
    }

    [Fact]
    public void KeywordsAndGenericNamesMatch()
    {
        Assert.Equal("Firefox", Names("browser")[0]);
        Assert.Equal("Calculator", Names("math")[0]);
        Assert.Equal("Terminal", Names("shell")[0]);
    }

    [Fact]
    public void SubsequenceMatches()
    {
        Assert.Equal("Firefox", Names("ffx")[0]);
        Assert.Equal("Text Editor", Names("txed")[0]);
    }

    [Fact]
    public void AllWordsMustMatch()
    {
        Assert.Equal(["Text Editor"], Names("text edit"));
        Assert.Empty(Names("text zzz"));
    }

    [Fact]
    public void FavouritesAndRecentsBoostTies()
    {
        // "t" prefixes Text Editor, Terminal and Thunar about equally; a favourite wins.
        Assert.Equal("Thunar", Names("t", fav: ["thunar.desktop"])[0]);
        Assert.Equal("Terminal", Names("t", rec: ["org.gnome.Terminal.desktop"])[0]);
    }

    [Fact]
    public void FavouriteToggleAndRecentPush()
    {
        var fav = UserLists.ToggleFavourite([], "a");
        Assert.Equal(["a"], fav);
        Assert.Empty(UserLists.ToggleFavourite(fav, "A"));

        var rec = UserLists.PushRecent(["a", "b", "c"], "c");
        Assert.Equal(["c", "a", "b"], rec);
        var many = Enumerable.Range(0, 20).Select(i => i.ToString()).ToList();
        Assert.Equal(UserLists.MaxRecents, UserLists.PushRecent(many, "x").Count);
    }

    [Fact]
    public void LaunchPlanRefusesTerminalApps()
    {
        Assert.Equal("window pull run firefox", LaunchPlan.For(Apps[1]).Line);
        var (line, error) = LaunchPlan.For(Apps[1] with { Terminal = true });
        Assert.Null(line);
        Assert.Contains("Terminal=true", error);
        Assert.NotNull(LaunchPlan.For(Apps[1] with { Command = "a '\n'" }).Error);
    }

    [Fact]
    public void LetterTileIsStable()
    {
        Assert.Equal("T", LetterTile.Letter("  text"));
        Assert.Equal("?", LetterTile.Letter("--"));
        Assert.Equal(LetterTile.Color("firefox.desktop"), LetterTile.Color("firefox.desktop"));
        Assert.Equal(0xFF000000u, LetterTile.Color("x") & 0xFF000000u);
    }

    [Fact]
    public void IpcPayloadsRoundTrip()
    {
        var json = IpcContract.Serialize(new[] { new AppSummary { Id = "a.desktop", Name = "A", Categories = ["Utility"], Favourite = true, Recent = 0, Launchable = true } });
        Assert.Contains("\"id\":\"a.desktop\"", json);
        Assert.Contains("\"generic\":", json);
        Assert.Contains("\"favourite\":true", json);
        var back = Assert.Single(IpcContract.ParseApps(json));
        Assert.Equal("A", back.Name);
        Assert.Equal(0, back.Recent);
        Assert.Empty(IpcContract.ParseApps("not json"));
        Assert.Null(IpcContract.ParseStatus("{"));
        Assert.True(IpcContract.ParseStatus(IpcContract.Serialize(new StatusPayload { Ghostty = true, Apps = 3 }))!.Ghostty);
    }

    [Theory]
    [InlineData("/home/user", null, null, "/home/user")]
    [InlineData(@"C:\users\user", @"\??\Z:\home\user", null, "/home/user")]
    [InlineData(null, null, @"Z:\home\user\.xlcore\pluginConfigs\XivDesktop", "/home/user")]
    [InlineData(null, null, @"C:\somewhere", "")]
    public void GuessHome(string? home, string? wineHome, string? xl, string expected)
        => Assert.Equal(expected, HostPaths.GuessHome(home, wineHome, xl));
}
