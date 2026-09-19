namespace XivDesktop.Core.Ask;

/// <summary>Where almanac keeps its gateway token, as this process can open it.</summary>
public static class AlmanacPaths
{
    public const string DefaultGateway = "http://127.0.0.1:41881";

    /// <summary>~/.config/almanac/token mapped for this process ("Z:\home\name\.config\almanac\token" under Wine); null without a home.</summary>
    public static string? TokenPath(HostPaths paths) =>
        paths.LinuxHome.Length == 0 ? null : paths.ToLocal(paths.LinuxHome + "/.config/almanac/token");

    /// <summary>The chat-completions URL under a gateway base ("http://127.0.0.1:41881" or ".../v1").</summary>
    public static Uri ChatUrl(string gateway)
    {
        var g = string.IsNullOrWhiteSpace(gateway) ? DefaultGateway : gateway.Trim().TrimEnd('/');
        if (g.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return new Uri(g);
        if (!g.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            g += "/v1";
        return new Uri(g + "/chat/completions");
    }
}
