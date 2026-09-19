using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivDesktop.Core.Claude;

/// <summary>
/// The JSON-RPC half of XivDesktop's MCP server: it serves exactly one tool,
/// <c>permission_prompt</c>, which Claude Code calls through <c>--permission-prompt-tool</c>.
/// Pure: the transport (an HTTP listener on the loopback address) lives in the plugin.
/// </summary>
/// <remarks>
/// The contract is not published, so it was confirmed against <c>claude 2.1.278</c> by serving the tool
/// and recording the traffic:
/// <list type="bullet">
/// <item>the client posts <c>server/discover</c> (MCP 2026-07-28) and falls back to <c>initialize</c>
/// (2025-11-25) when that is not answered, then <c>notifications/initialized</c> and <c>tools/list</c>;</item>
/// <item><c>tools/call</c> arrives with
/// <c>{"name":"permission_prompt","arguments":{"tool_name":…,"input":{…},"tool_use_id":"toolu_…"}}</c>;</item>
/// <item>the reply must be one text content block holding
/// <c>{"behavior":"allow","updatedInput":{…}}</c> or <c>{"behavior":"deny","message":"…"}</c>. An allow
/// runs the tool with <c>updatedInput</c>; a deny is reported to the model and recorded in the result
/// line's <c>permission_denials</c>.</item>
/// </list>
/// </remarks>
public sealed class McpServer
{
    public const string ProtocolVersion = "2025-11-25";

    private readonly Func<PermissionRequest, PermissionDecision> ask;
    private readonly Func<string?, string?> resolve;

    /// <param name="ask">Puts the prompt in front of the player and blocks until it is answered.</param>
    /// <param name="resolve">
    /// Turns the <see cref="ClaudeCommand.AuthHeader"/> a request carried into the session it belongs to,
    /// or null when the secret is not one XivDesktop handed out. Each session gets its own secret, so one
    /// session's job can never answer another's prompts, and nothing else on the machine can answer any.
    /// </param>
    public McpServer(Func<PermissionRequest, PermissionDecision> ask, Func<string?, string?>? resolve = null)
    {
        this.ask = ask;
        this.resolve = resolve ?? (_ => "");
    }

    /// <summary>The session a request's secret belongs to, or null when it is not ours.</summary>
    public string? SessionFor(string? header) => resolve(header);

    public bool Authorised(string? header) => resolve(header) != null;

    /// <summary>
    /// Handles one JSON-RPC request body. Returns null for a notification (the transport answers 202) and
    /// otherwise the response body. Total: a malformed body becomes a JSON-RPC error, never an exception.
    /// </summary>
    public string? Handle(string body, string sessionKey = "")
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "parse error");
        }

        if (node is not JsonObject request)
            return Error(null, -32600, "invalid request");
        var id = request["id"];
        var method = request["method"]?.GetValue<string>() ?? "";

        switch (method)
        {
            case "server/discover":
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = ClaudeCommand.McpServerName,
                        ["title"] = "XivDesktop",
                        ["version"] = "1",
                    },
                });

            case "ping":
                return Result(id, new JsonObject());

            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = new JsonArray(ToolDefinition()) });

            case "tools/call":
                return Call(id, request["params"] as JsonObject, sessionKey);

            default:
                if (method.StartsWith("notifications/", StringComparison.Ordinal))
                    return null;
                return id is null ? null : Error(id, -32601, "unknown method: " + method);
        }
    }

    private string Call(JsonNode? id, JsonObject? parameters, string sessionKey)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? "";
        if (name != ClaudeCommand.PermissionToolName)
            return ToolError(id, "unknown tool: " + name);
        var arguments = parameters?["arguments"] as JsonObject;
        var toolName = arguments?["tool_name"]?.GetValue<string>() ?? "";
        if (toolName.Length == 0)
            return ToolError(id, "tool_name is required");
        var input = arguments?["input"];
        var inputJson = input?.ToJsonString() ?? "{}";
        var toolUseId = arguments?["tool_use_id"]?.GetValue<string>()
            ?? (parameters?["_meta"] as JsonObject)?["claudecode/toolUseId"]?.GetValue<string>()
            ?? "";

        var request = new PermissionRequest
        {
            ToolName = toolName,
            InputJson = inputJson,
            ToolUseId = toolUseId,
            SessionKey = sessionKey,
        };

        PermissionDecision decision;
        try
        {
            decision = ask(request);
        }
        catch (Exception ex)
        {
            decision = new PermissionDecision(false, "XivDesktop could not ask: " + ex.GetType().Name);
        }

        return Result(id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = decision.ToToolResultText(inputJson),
            }),
        });
    }

    private static JsonObject ToolDefinition() => new()
    {
        ["name"] = ClaudeCommand.PermissionToolName,
        ["title"] = "Ask in game",
        ["description"] = "Asks the player, in Final Fantasy XIV, whether this tool may run. Returns an allow or deny decision.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["tool_name"] = new JsonObject { ["type"] = "string", ["description"] = "The tool being requested." },
                ["input"] = new JsonObject { ["type"] = "object", ["description"] = "The tool's arguments." },
                ["tool_use_id"] = new JsonObject { ["type"] = "string", ["description"] = "The tool_use block's id." },
            },
            ["required"] = new JsonArray("tool_name", "input"),
        },
    };

    private static string Result(JsonNode? id, JsonObject result) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    }.ToJsonString();

    private static string ToolError(JsonNode? id, string message) => Result(id, new JsonObject
    {
        ["isError"] = true,
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
    });

    private static string Error(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    }.ToJsonString();
}
