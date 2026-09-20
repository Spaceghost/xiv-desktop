using System.Text.Json;

namespace XivDesktop.Core.Palette;

/// <summary>A game offered by the XivArcade mod over IPC: its id there, what to show, and whether it can start now.</summary>
public sealed record ArcadeHit(string Id, string Title, string Subtitle, bool Ready, double Score);

/// <summary>
/// Reads the reply of <c>XivArcade.v1.Search</c> (a JSON array). XivArcade is a separate, optional mod;
/// nothing here references it. Total: anything malformed is an empty list.
/// </summary>
public static class ArcadeLink
{
    public static IReadOnlyList<ArcadeHit> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];
            var hits = new List<ArcadeHit>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || Str(e, "id") is not { Length: > 0 } id || Str(e, "title") is not { Length: > 0 } title)
                    continue;
                hits.Add(new ArcadeHit(id, title, Str(e, "subtitle") ?? "", e.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True,
                    e.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0));
            }

            return hits;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
