namespace XivDesktop.Core.Tests;

public class ExecTests
{
    [Theory]
    [InlineData("gnome-text-editor %U", "gnome-text-editor")]
    [InlineData("app %f", "app")]
    [InlineData("app %F --flag", "app --flag")]
    [InlineData("app --url=%u", "app --url=")]
    [InlineData("app %i %c %k", "app")]
    [InlineData("app %d %D %n %N %v %m", "app")]
    [InlineData("echo 100%%", "echo 100%")]
    [InlineData("env FOO=bar app", "env FOO=bar app")]
    public void StripsFieldCodes(string exec, string expected) => Assert.Equal(expected, ExecLine.ToShellCommand(exec));

    [Fact]
    public void FlatpakFileForwardingMarkersAreDropped()
    {
        var e = Samples.Parse("flatpak.desktop");
        Assert.Equal(
            "/usr/bin/flatpak run --branch=stable --arch=x86_64 --command=run.sh --file-forwarding com.logseq.Logseq",
            ExecLine.ToShellCommand(e.Exec));
    }

    [Fact]
    public void QuotedArgumentsFollowTheSpecAndAreRequotedForSh()
    {
        var e = Samples.Parse("quoting.desktop");
        var args = ExecLine.Split(e.Exec)!;
        Assert.Equal(["/opt/My App/bin/app", "--title", "it's \"quoted\"", "--cost", "$5", @"--path=C:\dir", "%f"], args);
        Assert.Equal(
            @"'/opt/My App/bin/app' --title 'it'\''s ""quoted""' --cost '$5' '--path=C:\dir'",
            ExecLine.ToShellCommand(e.Exec));
    }

    [Fact]
    public void EscapesSample()
    {
        var e = Samples.Parse("escapes.desktop");
        Assert.Equal("echo 100%", ExecLine.ToShellCommand(e.Exec));
    }

    [Fact]
    public void UnterminatedQuoteIsRejected()
    {
        Assert.Null(ExecLine.Split("app \"oops"));
        Assert.Null(ExecLine.ToShellCommand("app \"oops"));
    }

    [Fact]
    public void EmptyAndFieldCodeOnlyAreRejected()
    {
        Assert.Null(ExecLine.ToShellCommand(""));
        Assert.Null(ExecLine.ToShellCommand("%U"));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "''")]
    [InlineData("a b", "'a b'")]
    [InlineData("$(rm -rf ~)", "'$(rm -rf ~)'")]
    [InlineData("x;y", "'x;y'")]
    [InlineData("it's", @"'it'\''s'")]
    [InlineData("`id`", "'`id`'")]
    public void ShellQuoteNeutralizesMetacharacters(string arg, string expected) => Assert.Equal(expected, ExecLine.ShellQuote(arg));

    [Fact]
    public void QuotedEmptyArgumentSurvives()
    {
        Assert.Equal("app '' x", ExecLine.ToShellCommand("app \"\" x"));
    }
}
