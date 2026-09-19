namespace XivDesktop.Core.Claude;

/// <summary>
/// Where ghostty-agent listens and the token that authenticates to it. The token is read on demand and
/// kept only for the HELLO frame; it is never part of <see cref="ToString"/>, never persisted by
/// XivDesktop and never written to the log.
/// </summary>
public sealed record AgentEndpoint
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 7777;

    /// <summary>ghostty's default on Linux: the agent writes it owner-readable at startup.</summary>
    public const string DefaultTokenPath = "~/.config/ghostty-agent/token";

    public required string Host { get; init; }

    public required int Port { get; init; }

    /// <summary>The token file as a Linux path; <see cref="HostPaths.ToLocal"/> maps it for reading.</summary>
    public required string TokenPath { get; init; }

    /// <summary>
    /// The endpoint from the settings, falling back to ghostty's defaults. <paramref name="host"/>,
    /// <paramref name="port"/> and <paramref name="tokenPath"/> are the configured overrides; each is
    /// used only when it is set. <c>~</c> in the token path is the Linux home.
    /// </summary>
    public static AgentEndpoint Resolve(HostPaths paths, string? host = null, int port = 0, string? tokenPath = null)
    {
        var configured = (tokenPath ?? "").Trim();
        if (configured.Length == 0)
            configured = DefaultTokenPath;
        if (configured.StartsWith("~/", StringComparison.Ordinal))
            configured = paths.LinuxHome.Length > 0 ? paths.LinuxHome + configured[1..] : configured[1..];
        return new AgentEndpoint
        {
            Host = string.IsNullOrWhiteSpace(host) ? DefaultHost : host.Trim(),
            Port = port is > 0 and < 65536 ? port : DefaultPort,
            TokenPath = configured,
        };
    }

    /// <summary>
    /// Reads the token with <paramref name="read"/> (given the path this process can open). Returns "" and
    /// a reason when it is missing or empty; the reason names the path, never the token.
    /// </summary>
    public string ReadToken(HostPaths paths, Func<string, string?> read, out string problem)
    {
        problem = "";
        string? raw;
        try
        {
            raw = read(paths.ToLocal(TokenPath));
        }
        catch (Exception ex)
        {
            problem = $"could not read {TokenPath}: {ex.GetType().Name}";
            return "";
        }

        var token = (raw ?? "").Trim();
        if (token.Length == 0)
        {
            problem = $"no token in {TokenPath} — is ghostty-agent running?";
            return "";
        }

        return token;
    }

    public override string ToString() => $"{Host}:{Port}";
}
