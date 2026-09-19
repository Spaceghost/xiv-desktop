using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivDesktop.Core.Windows;

/// <summary>A window panel as ghostty-dalamud's <c>window.list</c> reports it (docs/IPC.md in ghostty-dalamud).</summary>
public sealed record WindowPanel
{
    /// <summary>Panel id; every other method takes it.</summary>
    public long Id { get; init; }

    /// <summary>The agent's stream id (0 while pending).</summary>
    public long Sid { get; init; }

    /// <summary>The window's title once the agent opened it, else "".</summary>
    public string Title { get; init; } = "";

    /// <summary>What was asked for: the program of a run command, the match text, or "".</summary>
    public string App { get; init; } = "";

    public int W { get; init; }

    public int H { get; init; }

    /// <summary>pending, live or ended.</summary>
    public string State { get; init; } = "";

    /// <summary>pet, pin, full or tab.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The panel has the keyboard.</summary>
    public bool Focused { get; init; }

    public bool IsLive => State == "live";

    public bool IsEnded => State == "ended";

    public bool IsPet => Kind == "pet";

    /// <summary>Title if known, else the app, else "window N".</summary>
    public string DisplayName => Title.Length > 0 ? Title : App.Length > 0 ? App : $"window {Id}";
}

/// <summary>One entry of <c>window.list</c>'s <c>requests</c>: the outcome of a queued change.</summary>
public sealed record WindowRequestResult
{
    public long Request { get; init; }

    public string Method { get; init; } = "";

    public bool Ok { get; init; }

    public string? Error { get; init; }

    /// <summary>For window.open: the new panel id, when it was created.</summary>
    public long? PanelId { get; init; }
}

/// <summary>A parsed <c>window.list</c> result.</summary>
public sealed record WindowListSnapshot
{
    public static readonly WindowListSnapshot Empty = new();

    /// <summary>Changes whenever windows or requests change; compare for equality only.</summary>
    public long Rev { get; init; } = -1;

    public IReadOnlyList<WindowPanel> Windows { get; init; } = [];

    public IReadOnlyList<WindowRequestResult> Requests { get; init; } = [];

    public WindowPanel? Find(long id)
    {
        foreach (var w in Windows)
        {
            if (w.Id == id)
                return w;
        }

        return null;
    }
}

/// <summary>A parsed <c>agent.status</c> result.</summary>
public sealed record AgentStatus(bool Connected, int Version, bool WindowsOk, string Agent);

/// <summary>Reply envelope: <c>{"ok": true, "result": …}</c> or <c>{"ok": false, "error": "…"}</c>.</summary>
public sealed record CallReply(bool Ok, string? Error, JsonElement Result)
{
    public static CallReply Fail(string error) => new(false, error, default);
}

/// <summary>
/// Builds requests for, and parses replies from, <c>GhosttyDalamud.v1.Call</c>. Pure: no Dalamud types.
/// Every parser is total: malformed input gives a failed reply or null, never an exception.
/// </summary>
public static class GhosttyWire
{
    public const string Caller = "XivDesktop";

    public static string Request(string method, JsonObject? parameters = null)
    {
        var o = new JsonObject { ["method"] = method };
        if (parameters != null)
            o["params"] = parameters;
        o["caller"] = Caller;
        return o.ToJsonString();
    }

    public static string List() => Request("window.list");

    public static string AgentStatusRequest() => Request("agent.status");

    /// <summary>window.open with exactly one of run/match/wid (or none), and an optional pin.</summary>
    public static string Open(string? run = null, string? match = null, long? wid = null, string? pin = null)
    {
        var p = new JsonObject();
        if (!string.IsNullOrEmpty(run))
            p["run"] = run;
        else if (!string.IsNullOrEmpty(match))
            p["match"] = match;
        else if (wid is { } w)
            p["wid"] = w;
        if (!string.IsNullOrWhiteSpace(pin))
            p["pin"] = pin.Trim();
        return Request("window.open", p);
    }

    public static string Close(long id) => Request("window.close", new JsonObject { ["id"] = id });

    public static string Focus(long id) => Request("window.focus", new JsonObject { ["id"] = id });

    public static string Place(long id, string pin) => Request("window.place", new JsonObject { ["id"] = id, ["pin"] = pin.Trim() });

    public static CallReply ParseReply(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return CallReply.Fail("empty reply");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return CallReply.Fail("reply is not an object");
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            if (!ok)
            {
                var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                return CallReply.Fail(string.IsNullOrEmpty(error) ? "ghostty refused the call" : error!);
            }

            var result = root.TryGetProperty("result", out var r) ? r.Clone() : default;
            return new CallReply(true, null, result);
        }
        catch (JsonException ex)
        {
            return CallReply.Fail("malformed reply: " + ex.Message);
        }
    }

    /// <summary>The request id of a queued change (<c>{"queued": true, "request": N}</c>), or null.</summary>
    public static long? ParseQueued(CallReply reply)
    {
        if (!reply.Ok || reply.Result.ValueKind != JsonValueKind.Object)
            return null;
        return reply.Result.TryGetProperty("request", out var r) && r.TryGetInt64(out var id) ? id : null;
    }

    /// <summary>Only the <c>rev</c> of a window.list reply, for the cheap poll; null when the reply failed.</summary>
    public static long? PeekRev(CallReply reply)
    {
        if (!reply.Ok || reply.Result.ValueKind != JsonValueKind.Object)
            return null;
        return reply.Result.TryGetProperty("rev", out var r) && r.TryGetInt64(out var rev) ? rev : null;
    }

    public static WindowListSnapshot? ParseWindowList(string? json) => ParseWindowList(ParseReply(json));

    public static WindowListSnapshot? ParseWindowList(CallReply reply)
    {
        if (!reply.Ok || reply.Result.ValueKind != JsonValueKind.Object)
            return null;
        var r = reply.Result;
        var windows = new List<WindowPanel>();
        if (r.TryGetProperty("windows", out var ws) && ws.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in ws.EnumerateArray())
            {
                if (w.ValueKind != JsonValueKind.Object || Long(w, "id") is not { } id)
                    continue;
                windows.Add(new WindowPanel
                {
                    Id = id,
                    Sid = Long(w, "sid") ?? 0,
                    Title = Str(w, "title"),
                    App = Str(w, "app"),
                    W = (int)(Long(w, "w") ?? 0),
                    H = (int)(Long(w, "h") ?? 0),
                    State = Str(w, "state"),
                    Kind = Str(w, "kind"),
                    Focused = w.TryGetProperty("focused", out var f) && f.ValueKind == JsonValueKind.True,
                });
            }
        }

        var requests = new List<WindowRequestResult>();
        if (r.TryGetProperty("requests", out var rs) && rs.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in rs.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.Object || Long(q, "request") is not { } rid)
                    continue;
                long? panel = null;
                if (q.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object)
                    panel = Long(res, "id");
                requests.Add(new WindowRequestResult
                {
                    Request = rid,
                    Method = Str(q, "method"),
                    Ok = q.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                    Error = q.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
                    PanelId = panel,
                });
            }
        }

        return new WindowListSnapshot { Rev = Long(r, "rev") ?? 0, Windows = windows, Requests = requests };
    }

    public static AgentStatus? ParseAgentStatus(string? json)
    {
        var reply = ParseReply(json);
        if (!reply.Ok || reply.Result.ValueKind != JsonValueKind.Object)
            return null;
        var r = reply.Result;
        return new AgentStatus(
            r.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True,
            (int)(Long(r, "version") ?? 0),
            r.TryGetProperty("windows_ok", out var w) && w.ValueKind == JsonValueKind.True,
            Str(r, "agent"));
    }

    private static long? Long(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static string Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
