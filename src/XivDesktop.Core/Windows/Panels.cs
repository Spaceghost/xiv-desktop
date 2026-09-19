using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivDesktop.Core.Windows;

/// <summary>
/// Any world or dropdown panel as ghostty-dalamud's <c>panel.list</c> reports it: terminals, window panels,
/// adopted windows and chat. Built against the documented shape (branch panel-ipc); older builds only have
/// <c>window.list</c>, see <see cref="Panels.FromWindows"/>.
/// </summary>
public sealed record PanelInfo
{
    public long Id { get; init; }

    /// <summary>terminal | window | adopted | chat.</summary>
    public string Kind { get; init; } = "";

    public string Title { get; init; } = "";

    public string App { get; init; } = "";

    /// <summary>For terminals: the profile name.</summary>
    public string Profile { get; init; } = "";

    /// <summary>dropdown | tab | pet | pin | hud | min | full | hidden.</summary>
    public string View { get; init; } = "";

    public bool Focused { get; init; }

    public int? Order { get; init; }

    public bool? Running { get; init; }

    /// <summary>An icon name or path, when ghostty sends one.</summary>
    public string Icon { get; init; } = "";

    public bool IsHidden => View == "hidden";

    public bool IsPet => View == "pet";

    public string DisplayName => Title.Length > 0 ? Title
        : App.Length > 0 ? App
        : Profile.Length > 0 ? Profile
        : $"{(Kind.Length > 0 ? Kind : "panel")} {Id}";
}

public sealed record PanelListSnapshot
{
    public long Rev { get; init; } = -1;

    public IReadOnlyList<PanelInfo> Panels { get; init; } = [];

    /// <summary>True when this came from <c>panel.list</c>, false when made from <c>window.list</c>.</summary>
    public bool Native { get; init; }
}

/// <summary>panel.* requests and the panel.list parser. Total: malformed input never throws.</summary>
public static class Panels
{
    public static readonly string[] OrderTargets = ["left", "right", "first", "last"];

    public static string List() => GhosttyWire.Request("panel.list");

    public static string Focus(long id, bool fly = false)
    {
        var p = new JsonObject { ["id"] = id };
        if (fly)
            p["fly"] = true;
        return GhosttyWire.Request("panel.focus", p);
    }

    public static string Close(long id) => GhosttyWire.Request("panel.close", new JsonObject { ["id"] = id });

    public static string Minimize(long id) => GhosttyWire.Request("panel.minimize", new JsonObject { ["id"] = id });

    public static string TogglePet(long id) => GhosttyWire.Request("panel.toggle_pet", new JsonObject { ["id"] = id });

    public static string Place(long id, string pin) => GhosttyWire.Request("panel.place", new JsonObject { ["id"] = id, ["pin"] = pin.Trim() });

    /// <summary>panel.order: "left", "right", "first", "last", or a position N.</summary>
    public static string Order(long id, string to)
    {
        var p = new JsonObject { ["id"] = id };
        p["to"] = int.TryParse(to, out var n) ? n : to;
        return GhosttyWire.Request("panel.order", p);
    }

    /// <summary>Null when the call failed (including an older ghostty without panel.list).</summary>
    public static PanelListSnapshot? ParseList(string? json)
    {
        var reply = GhosttyWire.ParseReply(json);
        if (!reply.Ok || reply.Result.ValueKind != JsonValueKind.Object)
            return null;
        var r = reply.Result;
        var list = new List<PanelInfo>();
        if (r.TryGetProperty("panels", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in ps.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object || Long(p, "id") is not { } id)
                    continue;
                list.Add(new PanelInfo
                {
                    Id = id,
                    Kind = Str(p, "kind"),
                    Title = Str(p, "title"),
                    App = Str(p, "app"),
                    Profile = Str(p, "profile"),
                    View = Str(p, "view"),
                    Focused = p.TryGetProperty("focused", out var f) && f.ValueKind == JsonValueKind.True,
                    Order = Long(p, "order") is { } o ? (int)o : null,
                    Running = p.TryGetProperty("running", out var run) && run.ValueKind is JsonValueKind.True or JsonValueKind.False ? run.ValueKind == JsonValueKind.True : null,
                    Icon = Str(p, "icon"),
                });
            }
        }

        // Ordered panels first by their order, the rest as listed.
        var ordered = list.Select((p, i) => (p, i)).OrderBy(x => x.p.Order ?? int.MaxValue).ThenBy(x => x.i).Select(x => x.p).ToList();
        return new PanelListSnapshot { Rev = Long(r, "rev") ?? 0, Panels = ordered, Native = true };
    }

    /// <summary>Panels made from window.list for an older ghostty: window panels only.</summary>
    public static PanelListSnapshot FromWindows(WindowListSnapshot s) => new()
    {
        Rev = s.Rev,
        Native = false,
        Panels = s.Windows.Where(w => !w.IsEnded).Select(w => new PanelInfo
        {
            Id = w.Id,
            Kind = "window",
            Title = w.Title,
            App = w.App,
            View = w.Hidden == true ? "hidden" : w.Kind,
            Focused = w.Focused,
        }).ToList(),
    };

    private static long? Long(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static string Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
