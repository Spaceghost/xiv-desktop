using System.Text.Json;
using XivDesktop.Core.Claude;

namespace XivDesktop.Core.Tests;

public class ClaudeConversationTests
{
    /// <summary>
    /// A recorded <c>claude -p --output-format stream-json --verbose --include-partial-messages</c> run
    /// (claude 2.1.278), with the paths replaced. The whole panel is built from this.
    /// </summary>
    private static string[] Fixture()
        => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Samples", "claude", "session.jsonl"));

    private static Conversation Replay()
    {
        var conversation = new Conversation();
        conversation.Prompt("read hello.txt and add two lines");
        foreach (var line in Fixture())
            conversation.Feed(line);
        return conversation;
    }

    [Fact]
    public void ReadsTheSessionHeaderOffTheInitEvent()
    {
        var c = Replay();
        Assert.Equal("2a030fa7-ec2b-4b8b-a9e3-556e31823f2c", c.SessionId);
        Assert.Equal("claude-haiku-4-5-20251001", c.Model);
        Assert.Equal("/home/player/code/widget", c.Cwd);
        Assert.Equal("default", c.PermissionMode);
    }

    [Fact]
    public void StreamedDeltasBecomeOneReplyAndAreNotRepeatedByTheFinalBlock()
    {
        var c = Replay();
        var replies = c.Items.Where(i => i.Kind == ItemKind.Reply).ToList();
        Assert.Equal("Hey there, friend.", replies[0].Text);
        Assert.Equal(2, replies.Count); // the streamed one, and the closing "Read the file…"
    }

    [Fact]
    public void ToolUsesBecomeCardsWithAnIconTargetAndResultLine()
    {
        var c = Replay();
        var cards = c.Items.Where(i => i.Kind == ItemKind.Tool).ToList();
        Assert.Equal(2, cards.Count);

        Assert.Equal(ToolKind.Read, cards[0].Tool);
        Assert.Equal("/home/player/code/widget/hello.txt", cards[0].Target);
        Assert.Equal(ToolStatus.Done, cards[0].Status);
        Assert.Equal("3 lines", cards[0].ResultLine);

        Assert.Equal(ToolKind.Edit, cards[1].Tool);
        Assert.Equal("/home/player/code/widget/src/main.rs", cards[1].Target);
        Assert.Equal("+4 −2", cards[1].ResultLine);
        Assert.Equal(ToolStatus.Done, cards[1].Status);
    }

    [Fact]
    public void KeepsTheTotalsFromTheResultEvent()
    {
        var c = Replay();
        Assert.Equal(78406, c.Totals.InputTokens); // 26 + 12743 + 65637
        Assert.Equal(488, c.Totals.OutputTokens);
        Assert.Equal(SessionActivity.Waiting, c.Activity);
        Assert.Contains("78.4k in", c.Totals.Summary());
        Assert.Contains("$0.0345", c.Totals.Summary());
    }

    [Fact]
    public void TheRawToggleShowsExactlyWhatTheJobPrinted()
    {
        var c = Replay();
        var fixture = Fixture();
        Assert.Equal(fixture.Length, c.Raw.Count);
        Assert.Equal(string.Join("\n", fixture), c.RawText());
        // The raw text is the terminal's text: untouched JSON, not the rendered cards.
        Assert.StartsWith("{\"type\":\"system\",\"subtype\":\"init\"", c.RawText());
    }

    [Fact]
    public void AllowedRateLimitsAndTokenCountsAreNotShownAsNoise()
    {
        var c = Replay();
        Assert.DoesNotContain(c.Items, i => i.Kind == ItemKind.Notice && i.Text.Contains("thinking_tokens"));
        Assert.DoesNotContain(c.Items, i => i.Kind == ItemKind.Notice && i.Text.Contains("rate limit"));
    }

    [Fact]
    public void SurvivesGarbageOnStdout()
    {
        var c = new Conversation();
        c.Feed("not json at all");
        c.Feed("{\"type\":");
        c.Feed("");
        Assert.Equal(SessionActivity.Idle, c.Activity);
        Assert.Equal(3, c.Raw.Count);
    }

    [Fact]
    public void StderrBecomesAWarningNotice()
    {
        var c = new Conversation();
        c.FeedError("claude: command not found");
        var notice = Assert.Single(c.Items);
        Assert.Equal(ItemKind.Notice, notice.Kind);
        Assert.True(notice.IsError);
    }

    [Fact]
    public void AnUnfinishedToolCardIsMarkedWhenTheJobDies()
    {
        var c = new Conversation();
        c.Apply(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolUseId = "t1", ToolName = "Bash", ToolInput = """{"command":"sleep 100"}""" });
        Assert.Equal(1, c.Pending);
        c.Broken("the agent went away");
        Assert.Equal(ToolStatus.Failed, c.Items[0].Status);
        Assert.Equal("interrupted", c.Items[0].ResultLine);
        Assert.Equal(SessionActivity.Broken, c.Activity);
    }

    [Fact]
    public void ACardWaitingOnAPermissionPromptSaysSo()
    {
        var c = new Conversation();
        c.Apply(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolUseId = "t1", ToolName = "Write", ToolInput = """{"file_path":"/tmp/x"}""" });
        c.Asking("t1", true);
        Assert.Equal(ToolStatus.Asking, c.Items[0].Status);
        Assert.Equal(SessionActivity.Asking, c.Activity);

        c.Denied("t1", "The player said no.");
        Assert.Equal(ToolStatus.Denied, c.Items[0].Status);
        Assert.Equal("The player said no.", c.Items[0].ResultLine);
    }

    [Fact]
    public void DropsTheOldestRowsRatherThanGrowingForever()
    {
        var c = new Conversation();
        for (var i = 0; i < Conversation.MaxItems + 50; i++)
            c.Notice("line " + i);
        Assert.Equal(Conversation.MaxItems, c.Items.Count);
        Assert.Equal("line 50", c.Items[0].Text);   // the 50 oldest went
        Assert.Equal("line 449", c.Items[^1].Text);
    }

    [Theory]
    [InlineData("Bash", """{"command":"just build"}""", ToolKind.Run, "just build")]
    [InlineData("Grep", """{"pattern":"TODO","path":"src"}""", ToolKind.Search, "TODO in src")]
    [InlineData("WebFetch", """{"url":"https://example.invalid"}""", ToolKind.Web, "https://example.invalid")]
    [InlineData("mcp__xivdesktop__permission_prompt", """{"tool_name":"Write"}""", ToolKind.Mcp, "Write")]
    [InlineData("Task", """{"description":"find the bug"}""", ToolKind.Task, "find the bug")]
    public void NamesEachKindOfToolCall(string tool, string input, ToolKind kind, string target)
    {
        Assert.Equal(kind, ToolCards.KindOf(tool));
        Assert.Equal(target, ToolCards.Target(tool, input));
        Assert.NotEqual("", ToolCards.Glyph(kind));
    }

    [Fact]
    public void SummarisesCommandsSearchesAndFailures()
    {
        Assert.Equal("exit 0 · 2 lines", ToolCards.ResultLine("Bash", "{}", "a\nb", isError: false));
        Assert.Equal("exit 0", ToolCards.ResultLine("Bash", "{}", "", isError: false));
        Assert.Equal("14 matches", ToolCards.ResultLine("Grep", "{}", string.Join("\n", Enumerable.Repeat("hit", 14)), isError: false));
        Assert.Equal("no matches", ToolCards.ResultLine("Grep", "{}", "", isError: false));
        Assert.StartsWith("failed:", ToolCards.ResultLine("Read", "{}", "File does not exist.\nmore", isError: true));
        Assert.Equal("+1 −0", ToolCards.ResultLine("Write", """{"content":"one line"}""", "ok", isError: false));
    }

    [Fact]
    public void CollapsesLongOutputAndPointsAtTheRawToggle()
    {
        var detail = ToolCards.Detail(string.Join("\n", Enumerable.Range(0, 40).Select(i => "line " + i)));
        Assert.Equal(7, detail.Count);
        Assert.Contains("34 more lines (raw)", detail[^1]);
        Assert.Empty(ToolCards.Detail(""));
    }

    [Fact]
    public void AMultiBlockAssistantMessageBecomesSeveralEventsInOrder()
    {
        const string line = """
            {"type":"assistant","session_id":"s","message":{"model":"m","role":"assistant","content":[
              {"type":"thinking","thinking":"hmm"},
              {"type":"text","text":"here goes"},
              {"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]}}
            """;
        var events = StreamJson.Parse(line);
        Assert.Equal([ClaudeEventKind.Thinking, ClaudeEventKind.Text, ClaudeEventKind.ToolUse], events.Select(e => e.Kind));
        Assert.Equal("t1", events[2].ToolUseId);
        Assert.Equal("""{"command":"ls"}""", JsonDocument.Parse(events[2].ToolInput).RootElement.GetRawText());
    }
}
