using XivDesktop.Core.Palette;

namespace XivDesktop.Core.Tests;

public class ClaudePaletteTests
{
    private static PaletteContext Context(bool available = true) => new()
    {
        ClaudeAvailable = available,
        ClaudeReason = available ? null : "ghostty-agent 3 has no jobs",
        ClaudeSessions = [("widget", "Claude — Quill · working")],
        ClaudeRecent = [("docs", "opus-4-5 · ~/code/docs · 2h ago")],
    };

    [Fact]
    public void TypingAPromptOffersToSendItToClaude()
    {
        var row = PaletteEngine.Build(Context(), "claude fix the parser").First(i => i.Command.Kind == PaletteCommand.Claude);
        Assert.Equal("Claude: fix the parser", row.Title);
        Assert.Equal("fix the parser", row.Command.Arg);
        Assert.Equal("Claude", row.Badge);
        Assert.True(row.Enabled);

        // The slash form works too, and it ranks above the fixed commands.
        Assert.Contains(PaletteEngine.Build(Context(), "/claude fix the parser"), i => i.Command.Kind == PaletteCommand.Claude);
    }

    [Fact]
    public void OpenAndRememberedSessionsEachGetARow()
    {
        var items = PaletteEngine.Build(Context(), "widget");
        var open = Assert.Single(items, i => i.Command.Kind == PaletteCommand.ClaudeSession);
        Assert.Equal("widget", open.Command.Arg);
        Assert.Contains("working", open.Subtitle);

        var resume = Assert.Single(PaletteEngine.Build(Context(), "docs"), i => i.Command.Kind == PaletteCommand.ClaudeResume);
        Assert.Equal("docs", resume.Command.Arg);
    }

    [Fact]
    public void AnOpenSessionIsStillReachableWhenTheAgentIsGone()
    {
        var items = PaletteEngine.Build(Context(available: false), "widget");
        // Focusing a panel needs nothing; starting or resuming does, and says why.
        Assert.True(Assert.Single(items, i => i.Command.Kind == PaletteCommand.ClaudeSession).Enabled);
        var resume = Assert.Single(PaletteEngine.Build(Context(available: false), "docs"), i => i.Command.Kind == PaletteCommand.ClaudeResume);
        Assert.False(resume.Enabled);
        Assert.Equal("ghostty-agent 3 has no jobs", resume.Reason);
    }

    [Fact]
    public void WithoutAQueryTheClaudeRowsOnlyAppearUnderTheCommandFilter()
    {
        Assert.DoesNotContain(PaletteEngine.Build(Context(), ""), i => i.Badge == "Claude");
        Assert.Contains(PaletteEngine.Build(Context(), ">"), i => i.Command.Kind == PaletteCommand.ClaudeSession);
    }

    [Fact]
    public void NoClaudeRowsWhenThereIsNothingToShow()
    {
        var empty = new PaletteContext();
        Assert.DoesNotContain(PaletteEngine.Build(empty, ">"), i => i.Badge == "Claude");
    }
}
