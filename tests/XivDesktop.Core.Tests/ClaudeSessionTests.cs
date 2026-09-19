using System.Text.Json;
using XivDesktop.Core.Claude;

namespace XivDesktop.Core.Tests;

public class ClaudeSessionTests
{
    /// <summary>The argv[1] options object, parsed back out.</summary>
    private static JsonElement OptionsOf(ClaudeLaunch launch)
    {
        var argv = ClaudeCommand.Argv(launch);
        Assert.Equal("@claude", argv[0]);
        Assert.Equal(2, argv.Count);
        return JsonDocument.Parse(argv[1]).RootElement.Clone();
    }

    private static List<string> ArgsOf(JsonElement options)
        => options.TryGetProperty("args", out var args)
            ? args.EnumerateArray().Select(a => a.GetString()!).ToList()
            : [];

    [Fact]
    public void AsksTheAgentsClaudeRunnerRatherThanHardcodingFlags()
    {
        var options = OptionsOf(new ClaudeLaunch
        {
            WorkingDirectory = "/home/player/code/widget",
            Model = "opus",
            Name = "widget",
            McpUrl = "http://127.0.0.1:8791/mcp",
            McpAuth = "s3cret",
        });

        // The base command line belongs to the agent; only the session's own choices are sent.
        Assert.Equal("opus", options.GetProperty("model").GetString());
        Assert.Equal("/home/player/code/widget", options.GetProperty("cwd").GetString());
        Assert.Equal("host", options.GetProperty("permission_prompts").GetString());
        Assert.False(options.TryGetProperty("permission_mode", out _));

        var args = ArgsOf(options);
        Assert.Equal("widget", args[args.IndexOf("--name") + 1]);
        Assert.Equal("mcp__xivdesktop__permission_prompt", args[args.IndexOf("--permission-prompt-tool") + 1]);
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
        Assert.DoesNotContain("--allow-dangerously-skip-permissions", args);
    }

    [Fact]
    public void TheMcpConfigIsPassedAsOneInlineServerEntry()
    {
        var options = OptionsOf(new ClaudeLaunch
        {
            WorkingDirectory = "/home/player",
            McpUrl = "http://127.0.0.1:8791/mcp",
            McpAuth = "s3cret",
        });
        var configs = options.GetProperty("mcp_config");
        Assert.Equal(1, configs.GetArrayLength());
        Assert.Equal(ClaudeCommand.McpConfig("http://127.0.0.1:8791/mcp", "s3cret"), configs[0].GetString());
    }

    [Fact]
    public void TheMcpConfigNamesOneHttpServerWithTheSessionSecret()
    {
        var json = ClaudeCommand.McpConfig("http://127.0.0.1:8791/mcp", "s3cret");
        using var doc = JsonDocument.Parse(json);
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("xivdesktop");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:8791/mcp", server.GetProperty("url").GetString());
        Assert.Equal("s3cret", server.GetProperty("headers").GetProperty(ClaudeCommand.AuthHeader).GetString());
    }

    [Fact]
    public void TheFallbackRouteDeniesWhatItCannotAskAbout()
    {
        var options = OptionsOf(new ClaudeLaunch
        {
            WorkingDirectory = "/home/player",
            Route = PermissionRoute.Mode,
            PermissionMode = "acceptEdits",
        });
        Assert.Equal("acceptEdits", options.GetProperty("permission_mode").GetString());
        // "none" means anything that would still have prompted is refused, not quietly allowed.
        Assert.Equal("none", options.GetProperty("permission_prompts").GetString());
        Assert.DoesNotContain("--permission-prompt-tool", ArgsOf(options));
        Assert.False(options.TryGetProperty("mcp_config", out _));

        // No MCP url means the prompt tool cannot work: fall back rather than run unprompted.
        var noUrl = OptionsOf(new ClaudeLaunch { WorkingDirectory = "/home/player" });
        Assert.Equal("manual", noUrl.GetProperty("permission_mode").GetString());
        Assert.Equal("none", noUrl.GetProperty("permission_prompts").GetString());
    }

    [Fact]
    public void ResumeAndForkAreOnlyAddedWhenAskedFor()
    {
        var resumed = OptionsOf(new ClaudeLaunch
        {
            WorkingDirectory = "/home/player",
            SessionId = "ignored-while-resuming",
            ResumeSessionId = "2a030fa7-ec2b-4b8b-a9e3-556e31823f2c",
            ForkSession = true,
        });
        Assert.Equal("2a030fa7-ec2b-4b8b-a9e3-556e31823f2c", resumed.GetProperty("resume").GetString());
        Assert.False(resumed.TryGetProperty("session_id", out _));
        Assert.Contains("--fork-session", ArgsOf(resumed));

        var fresh = OptionsOf(new ClaudeLaunch { WorkingDirectory = "/home/player", SessionId = "fixed-id" });
        Assert.Equal("fixed-id", fresh.GetProperty("session_id").GetString());
        Assert.False(fresh.TryGetProperty("resume", out _));
        Assert.DoesNotContain("--fork-session", ArgsOf(fresh));
    }

    [Fact]
    public void APromptIsOneArgvElementAndOneStdinLineNeverAShellString()
    {
        var line = ClaudeCommand.UserMessage("rm -rf / ; echo \"pwned\"");
        Assert.EndsWith("\n", line);
        Assert.Single(line.TrimEnd('\n').Split('\n'));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("user", doc.RootElement.GetProperty("type").GetString());
        var content = doc.RootElement.GetProperty("message").GetProperty("content")[0];
        Assert.Equal("rm -rf / ; echo \"pwned\"", content.GetProperty("text").GetString());
    }

    [Fact]
    public void RemembersSessionsNewestFirstAndDropsTheOldest()
    {
        var now = DateTimeOffset.UnixEpoch;
        var list = new List<SessionRecord>();
        for (var i = 0; i < SessionHistory.Max + 5; i++)
            list = SessionHistory.Touch(list, new SessionRecord { Name = "s" + i, SessionId = "id" + i }, now.AddMinutes(i));
        Assert.Equal(SessionHistory.Max, list.Count);
        Assert.Equal("s" + (SessionHistory.Max + 4), list[0].Name);
    }

    [Fact]
    public void TouchingAKnownSessionMovesItToTheFrontInsteadOfDuplicatingIt()
    {
        var now = DateTimeOffset.UnixEpoch;
        var a = new SessionRecord { Name = "widget", SessionId = "id-a" };
        var b = new SessionRecord { Name = "other", SessionId = "id-b" };
        var list = SessionHistory.Touch(SessionHistory.Touch([], a, now), b, now.AddMinutes(1));
        list = SessionHistory.Touch(list, a, now.AddMinutes(2));
        Assert.Equal(2, list.Count);
        Assert.Equal("widget", list[0].Name);
    }

    [Fact]
    public void FindsASessionByNameIdOrIdPrefix()
    {
        var records = new List<SessionRecord>
        {
            new() { Name = "widget", SessionId = "2a030fa7-ec2b-4b8b-a9e3-556e31823f2c" },
            new() { Name = "docs", SessionId = "9f000000-0000-0000-0000-000000000000" },
        };
        Assert.Equal("widget", SessionHistory.Find(records, "WIDGET")!.Name);
        Assert.Equal("docs", SessionHistory.Find(records, "9f000000-0000-0000-0000-000000000000")!.Name);
        Assert.Equal("widget", SessionHistory.Find(records, "2a030fa7")!.Name);
        Assert.Null(SessionHistory.Find(records, "nothing"));
        Assert.Null(SessionHistory.Find(records, "  "));
    }

    [Fact]
    public void NamesNewSessionsWithoutCollidingWithTheOpenOnes()
    {
        var records = new List<SessionRecord> { new() { Name = "claude" }, new() { Name = "claude-2" } };
        Assert.Equal("claude-3", SessionHistory.UniqueName(records, "claude"));
        Assert.Equal("widget", SessionHistory.UniqueName(records, "widget"));
        Assert.Equal("claude-3", SessionHistory.UniqueName(records, "  "));
    }

    [Fact]
    public void SummarisesARememberedSessionForThePicker()
    {
        var record = new SessionRecord
        {
            Name = "widget",
            Model = "claude-opus-4-5-20251101",
            Cwd = "/home/player/code/widget",
            LastActivity = DateTimeOffset.UnixEpoch,
        };
        var summary = record.Summary(DateTimeOffset.UnixEpoch.AddHours(2));
        Assert.Contains("opus-4-5", summary);
        Assert.Contains("/home/player/code/widget", summary);
        Assert.Contains("2h ago", summary);
        Assert.Equal("widget", record.Key);
        Assert.Equal("id-only", new SessionRecord { SessionId = "id-only" }.Key);
    }

    [Fact]
    public void ListsTheHomeDirectoryAndTheRepositoriesUnderIt()
    {
        var tree = new Dictionary<string, string[]>
        {
            ["/home/player"] = ["/home/player/code", "/home/player/.config", "/home/player/Steam"],
            ["/home/player/code"] = ["/home/player/code/widget", "/home/player/code/scratch", "/home/player/code/node_modules"],
            ["/home/player/code/scratch"] = ["/home/player/code/scratch/deep"],
            ["/home/player/code/scratch/deep"] = [],
        };
        var files = new HashSet<string> { "/home/player/code/widget/.git", "/home/player/code/scratch/deep/.git" };

        var choices = RepoPicker.Scan("/home/player", d => tree.TryGetValue(d, out var c) ? c : [], files.Contains);

        Assert.Equal("~", choices[0].Display);
        var repos = choices.Skip(1).Select(c => c.Display).ToList();
        Assert.Equal(["~/code/scratch/deep", "~/code/widget"], repos);
        Assert.All(choices.Skip(1), c => Assert.True(c.IsRepo));
    }

    [Fact]
    public void FallsBackToHomeWhenTheConfiguredDirectoryIsGone()
    {
        Assert.Equal("/home/player/code", RepoPicker.Resolve("~/code", "/home/player", p => p == "/home/player/code"));
        Assert.Equal("/home/player", RepoPicker.Resolve("/gone", "/home/player", _ => false));
        Assert.Equal("/home/player", RepoPicker.Resolve("", "/home/player", _ => true));
        Assert.Equal("/home/player", RepoPicker.Resolve("~", "/home/player", _ => true));
    }
}
