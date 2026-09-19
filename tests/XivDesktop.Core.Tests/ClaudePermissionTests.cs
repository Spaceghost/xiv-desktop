using System.Text.Json;
using System.Text.Json.Nodes;
using XivDesktop.Core.Claude;

namespace XivDesktop.Core.Tests;

public class ClaudePermissionTests
{
    /// <summary>The <c>tools/call</c> body recorded from claude 2.1.278 asking to run Write.</summary>
    private static string RecordedCall()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "claude", "permission-call.json"));

    /// <summary>A bare JSON-RPC request with no parameters.</summary>
    private static string Request(int id, string method)
        => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = new JsonObject() }.ToJsonString();

    [Fact]
    public void AnswersTheRecordedPermissionCallInTheShapeClaudeParses()
    {
        PermissionRequest? seen = null;
        var server = new McpServer(request =>
        {
            seen = request;
            return new PermissionDecision(true);
        });

        var reply = server.Handle(RecordedCall(), "widget")!;
        Assert.NotNull(seen);
        Assert.Equal("Write", seen!.ToolName);
        Assert.Equal("toolu_01QGyeCAiZscz9uK3WSuujeC", seen.ToolUseId);
        Assert.Equal("widget", seen.SessionKey);
        Assert.Equal("/home/player/code/widget/probe.txt", seen.Target);

        using var doc = JsonDocument.Parse(reply);
        var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        using var decision = JsonDocument.Parse(text);
        Assert.Equal("allow", decision.RootElement.GetProperty("behavior").GetString());
        // updatedInput is the arguments the tool then runs with: unchanged unless the dialog edited them.
        Assert.Equal("hello", decision.RootElement.GetProperty("updatedInput").GetProperty("content").GetString());
    }

    [Fact]
    public void DenyCarriesTheReasonBackToTheModel()
    {
        var server = new McpServer(_ => new PermissionDecision(false, "The player said no."));
        var reply = server.Handle(RecordedCall())!;
        using var doc = JsonDocument.Parse(reply);
        var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        using var decision = JsonDocument.Parse(text);
        Assert.Equal("deny", decision.RootElement.GetProperty("behavior").GetString());
        Assert.Equal("The player said no.", decision.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void ServesTheHandshakeBothWaysTheClientTriesIt()
    {
        var server = new McpServer(_ => new PermissionDecision(true));
        foreach (var method in new[] { "server/discover", "initialize" })
        {
            var reply = server.Handle(Request(1, method))!;
            using var doc = JsonDocument.Parse(reply);
            var result = doc.RootElement.GetProperty("result");
            Assert.Equal(McpServer.ProtocolVersion, result.GetProperty("protocolVersion").GetString());
            Assert.Equal("xivdesktop", result.GetProperty("serverInfo").GetProperty("name").GetString());
        }

        Assert.Null(server.Handle("""{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}"""));

        var tools = server.Handle("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""")!;
        using var listed = JsonDocument.Parse(tools);
        var tool = listed.RootElement.GetProperty("result").GetProperty("tools")[0];
        Assert.Equal("permission_prompt", tool.GetProperty("name").GetString());
        Assert.Equal("mcp__xivdesktop__permission_prompt", ClaudeCommand.PermissionTool);
    }

    [Fact]
    public void MalformedBodiesBecomeJsonRpcErrorsRatherThanExceptions()
    {
        var server = new McpServer(_ => new PermissionDecision(true));
        Assert.Contains("-32700", server.Handle("not json")!);
        Assert.Contains("-32600", server.Handle("[]")!);
        Assert.Contains("-32601", server.Handle("""{"jsonrpc":"2.0","id":1,"method":"nope"}""")!);
        Assert.Contains("unknown tool", server.Handle("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"other"}}""")!);
    }

    [Fact]
    public void EachSessionHasItsOwnSecretAndNoOtherCallerIsServed()
    {
        var known = new Dictionary<string, string> { ["s3cret"] = "widget", ["other"] = "docs" };
        var server = new McpServer(
            _ => new PermissionDecision(true),
            header => header != null && known.TryGetValue(header, out var session) ? session : null);

        Assert.Equal("widget", server.SessionFor("s3cret"));
        Assert.Equal("docs", server.SessionFor("other"));
        Assert.Null(server.SessionFor("guess"));
        Assert.Null(server.SessionFor(null));
        Assert.False(server.Authorised("guess"));
        Assert.True(server.Authorised("s3cret"));
    }

    [Fact]
    public void ThePromptIsTaggedWithTheSessionThatAskedForIt()
    {
        PermissionRequest? seen = null;
        var server = new McpServer(request =>
        {
            seen = request;
            return new PermissionDecision(true);
        });
        server.Handle(RecordedCall(), "docs");
        Assert.Equal("docs", seen!.SessionKey);
    }

    [Fact]
    public void AnAskingThreadWaitsUntilThePlayerChooses()
    {
        var rules = new PermissionRules();
        var queue = new PermissionQueue(rules);
        var request = new PermissionRequest { ToolName = "Bash", InputJson = """{"command":"rm -rf /"}""", SessionKey = "s" };

        Assert.Null(queue.Submit(request, out var ticket));
        Assert.Equal("Bash", queue.Current!.ToolName);

        var answered = Task.Run(() => queue.Await(ticket));
        Assert.False(answered.Wait(100));
        queue.Answer(ticket, PermissionChoice.Deny, "nope");
        Assert.True(answered.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(answered.Result.Allow);
        Assert.Equal("nope", answered.Result.Message);
        Assert.Null(queue.Current);
    }

    [Fact]
    public void AllowForThisSessionSkipsTheDialogNextTime()
    {
        var rules = new PermissionRules();
        var queue = new PermissionQueue(rules);
        var request = new PermissionRequest { ToolName = "Read", SessionKey = "s" };

        Assert.Null(queue.Submit(request, out var ticket));
        queue.Answer(ticket, PermissionChoice.Session);

        Assert.True(queue.Submit(request, out _)!.Allow);                      // same session: no dialog
        Assert.Null(queue.Submit(request with { SessionKey = "other" }, out _)); // another session still asks
        Assert.Empty(rules.Always);
    }

    [Fact]
    public void AlwaysIsRememberedAcrossSessionsAndIsTheOnlyThingPersisted()
    {
        var rules = new PermissionRules();
        var queue = new PermissionQueue(rules);
        Assert.Null(queue.Submit(new PermissionRequest { ToolName = "Read", SessionKey = "s" }, out var ticket));
        queue.Answer(ticket, PermissionChoice.Always);
        Assert.Equal(["Read"], rules.Always);

        var later = new PermissionQueue(new PermissionRules(rules.Always));
        Assert.True(later.Submit(new PermissionRequest { ToolName = "Read", SessionKey = "fresh" }, out _)!.Allow);
        Assert.Null(later.Submit(new PermissionRequest { ToolName = "Write", SessionKey = "fresh" }, out _));
    }

    [Fact]
    public void NothingIsAllowedByDefault()
    {
        var queue = new PermissionQueue(new PermissionRules());
        foreach (var tool in new[] { "Read", "Bash", "Write", "WebFetch" })
            Assert.Null(queue.Submit(new PermissionRequest { ToolName = tool, SessionKey = "s" }, out _));
    }

    [Fact]
    public void PromptsQueueBehindTheOneOnScreen()
    {
        var queue = new PermissionQueue(new PermissionRules());
        queue.Submit(new PermissionRequest { ToolName = "Read", SessionKey = "s" }, out var first);
        queue.Submit(new PermissionRequest { ToolName = "Bash", SessionKey = "s" }, out _);
        Assert.Equal("Read", queue.Current!.ToolName);
        Assert.Equal(1, queue.Waiting);

        queue.Answer(first, PermissionChoice.Once);
        Assert.Equal("Bash", queue.Current!.ToolName);
        Assert.Equal(0, queue.Waiting);
    }

    [Fact]
    public void AnUnansweredPromptIsDeniedRatherThanLeftHanging()
    {
        var queue = new PermissionQueue(new PermissionRules()) { Timeout = TimeSpan.FromMilliseconds(50) };
        queue.Submit(new PermissionRequest { ToolName = "Bash", SessionKey = "s" }, out var ticket);
        var decision = queue.Await(ticket);
        Assert.False(decision.Allow);
        Assert.Contains("Nobody answered", decision.Message);
    }

    [Fact]
    public void ASessionGoingAwayDeniesEverythingItWasAsking()
    {
        var queue = new PermissionQueue(new PermissionRules());
        queue.Submit(new PermissionRequest { ToolName = "Read", SessionKey = "gone" }, out var a);
        queue.Submit(new PermissionRequest { ToolName = "Bash", SessionKey = "gone" }, out var b);
        var waiting = Task.Run(() => (queue.Await(a), queue.Await(b)));
        queue.CancelSession("gone", "the session ended");
        Assert.True(waiting.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(waiting.Result.Item1.Allow);
        Assert.False(waiting.Result.Item2.Allow);
        Assert.Null(queue.Current);
    }

    [Fact]
    public void TheDialogListsTheToolsArguments()
    {
        var request = new PermissionRequest
        {
            ToolName = "Edit",
            InputJson = """{"file_path":"/home/player/x.cs","old_string":"a\nb","new_string":"c"}""",
        };
        var rows = request.Arguments();
        Assert.Equal("file_path", rows[0].Name);
        Assert.Equal("/home/player/x.cs", rows[0].Value);
        Assert.Contains("⏎", rows[1].Value); // newlines are shown, not swallowed
        Assert.Equal(ToolKind.Edit, request.Kind);
    }
}
