namespace XivDesktop.Core.Tests;

public class ParserTests
{
    [Fact]
    public void ReadsMainGroupAndIgnoresActions()
    {
        var e = Samples.Parse("basic.desktop");
        Assert.Equal("Application", e.Type);
        Assert.Equal("Text Editor", e.Name);
        Assert.Equal("View and edit text files", e.Comment);
        Assert.Equal(["Text", "Plaintext", "Write"], e.Keywords);
        Assert.Equal(["GNOME", "GTK", "Utility", "TextEditor"], e.Categories);
        Assert.Equal("gnome-text-editor %U", e.Exec); // the [Desktop Action] Exec must not leak in
        Assert.Equal("org.gnome.TextEditor", e.Icon);
        Assert.False(e.Terminal);
    }

    [Fact]
    public void PrefersUnlocalizedName()
    {
        var e = Samples.Parse("basic.desktop");
        Assert.Equal("Text Editor", e.Name); // not "Texteditor"
    }

    [Fact]
    public void FallsBackToEnglishLocalizationWhenNoPlainKey()
    {
        var e = Samples.Parse("localized-only.desktop");
        Assert.Equal("Calculator", e.Name);
        Assert.Equal("Sums", e.Comment);
        Assert.Equal(["math", "sum"], e.Keywords);
    }

    [Fact]
    public void UnescapesStringsAndLists()
    {
        var e = Samples.Parse("escapes.desktop");
        Assert.Equal("Escapes and\tTabs", e.Name);
        Assert.Equal("line1\nline2", e.Comment);
        Assert.Equal(["semi;colon", "plain"], e.Keywords);
    }

    [Fact]
    public void BooleansAndShowIn()
    {
        Assert.True(Samples.Parse("nodisplay.desktop").NoDisplay);
        Assert.True(Samples.Parse("terminal.desktop").Terminal);
        Assert.Equal(["GNOME", "Unity"], Samples.Parse("onlyshowin.desktop").OnlyShowIn);
    }

    [Fact]
    public void NoMainGroupReturnsNull()
    {
        Assert.Null(DesktopEntryParser.Parse("[Other]\nName=x\n", "x.desktop"));
        Assert.Null(DesktopEntryParser.Parse("", "x.desktop"));
    }

    [Fact]
    public void ToleratesCrLfSpacesAroundEqualsAndDuplicateKeys()
    {
        var e = DesktopEntryParser.Parse("[Desktop Entry]\r\nName = First\r\nName=Second\r\nType=Application\r\n", "x.desktop")!;
        Assert.Equal("First", e.Name);
        Assert.Equal("Application", e.Type);
    }
}
