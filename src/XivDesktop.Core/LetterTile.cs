namespace XivDesktop.Core;

/// <summary>Fallback tile for apps without a PNG icon: a letter on a colour derived from the app id.</summary>
public static class LetterTile
{
    /// <summary>The first letter or digit of the name, upper-cased; '?' when there is none.</summary>
    public static string Letter(string name)
    {
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
                return char.ToUpperInvariant(ch).ToString();
        }

        return "?";
    }

    /// <summary>A stable, mid-saturation colour as 0xAABBGGRR (ImGui's packed order).</summary>
    public static uint Color(string id)
    {
        // FNV-1a: stable across runs and platforms (string.GetHashCode is randomized).
        var hash = 2166136261u;
        foreach (var ch in id)
        {
            hash ^= ch;
            hash *= 16777619u;
        }

        var hue = (hash % 360) / 360.0;
        var (r, g, b) = HsvToRgb(hue, 0.45, 0.62);
        return 0xFF000000u | ((uint)(b * 255) << 16) | ((uint)(g * 255) << 8) | (uint)(r * 255);
    }

    private static (double R, double G, double B) HsvToRgb(double h, double s, double v)
    {
        var i = (int)(h * 6) % 6;
        var f = (h * 6) - Math.Floor(h * 6);
        double p = v * (1 - s), q = v * (1 - (f * s)), t = v * (1 - ((1 - f) * s));
        return i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }
}
