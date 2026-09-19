using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin.Services;
using XivDesktop.Core.Claude;

namespace XivDesktop.Plugin.Claude;

/// <summary>
/// Serves <see cref="McpServer"/> over HTTP on the loopback address, which is how a Claude job reaches
/// XivDesktop's permission prompt tool: the game runs under Wine, which shares the host's network stack,
/// so a listener here is the host's own 127.0.0.1.
/// </summary>
/// <remarks>
/// The listener binds only 127.0.0.1, never a routable address. Each session is given its own secret,
/// carried in <see cref="ClaudeCommand.AuthHeader"/>: a request without a secret XivDesktop handed out is
/// refused, and one session's job can never answer another session's prompts.
/// </remarks>
public sealed class McpHttpServer : IDisposable
{
    private readonly IPluginLog log;
    private readonly McpServer server;
    private readonly HttpListener? listener;
    private readonly ConcurrentDictionary<string, string> secrets = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();

    public McpHttpServer(IPluginLog log, Func<PermissionRequest, PermissionDecision> ask)
    {
        this.log = log;
        server = new McpServer(ask, header => header != null && secrets.TryGetValue(header, out var session) ? session : null);

        for (var port = 8795; port < 8825 && listener == null; port++)
        {
            try
            {
                var candidate = new HttpListener();
                candidate.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
                candidate.Start();
                listener = candidate;
                Url = $"http://127.0.0.1:{port}/mcp";
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                // That port is taken; try the next.
            }
        }

        if (listener == null)
        {
            Problem = "no loopback port was free for the permission prompt server";
            log.Warning("XivDesktop: {Problem}", Problem);
            return;
        }

        new Thread(Run) { IsBackground = true, Name = "XivDesktop.Claude.Mcp" }.Start();
    }

    /// <summary>Where a job's <c>--mcp-config</c> points, or "" when the listener never came up.</summary>
    public string Url { get; } = "";

    /// <summary>Why the server is unavailable ("" when it is fine).</summary>
    public string Problem { get; private set; } = "";

    public bool Running => listener is { IsListening: true };

    /// <summary>A fresh secret for one session's job. Its previous one, if any, stops working.</summary>
    public string Register(string sessionKey)
    {
        Unregister(sessionKey);
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        secrets[secret] = sessionKey;
        return secret;
    }

    /// <summary>The session's secret stops being accepted (it ended, or it is starting again).</summary>
    public void Unregister(string sessionKey)
    {
        foreach (var pair in secrets)
        {
            if (string.Equals(pair.Value, sessionKey, StringComparison.Ordinal))
                secrets.TryRemove(pair.Key, out _);
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        secrets.Clear();
        try
        {
            listener?.Stop();
            listener?.Close();
        }
        catch (Exception)
        {
            // Already stopped.
        }

        stopping.Dispose();
    }

    private void Run()
    {
        while (!stopping.IsCancellationRequested && listener is { IsListening: true })
        {
            HttpListenerContext context;
            try
            {
                context = listener.GetContext();
            }
            catch (Exception)
            {
                return; // Stopped, or the listener went away.
            }

            // Each request is handled on the pool: a permission prompt blocks until the player answers.
            ThreadPool.QueueUserWorkItem(_ => Serve(context));
        }
    }

    private void Serve(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            if (!string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal))
            {
                Reply(context, 405, "");
                return;
            }

            var session = server.SessionFor(request.Headers[ClaudeCommand.AuthHeader]);
            if (session == null)
            {
                log.Warning("XivDesktop: a request to the permission server did not carry a secret this run handed out");
                Reply(context, 403, "");
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
                body = reader.ReadToEnd();

            var reply = server.Handle(body, session);
            Reply(context, reply == null ? 202 : 200, reply ?? "");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "XivDesktop: the permission prompt server failed a request");
            try
            {
                Reply(context, 500, "");
            }
            catch (Exception)
            {
                // The client hung up.
            }
        }
    }

    private static void Reply(HttpListenerContext context, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        if (bytes.Length > 0)
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }
}
