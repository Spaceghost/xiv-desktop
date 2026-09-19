using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using Dalamud.Plugin.Services;
using XivDesktop.Core;
using XivDesktop.Core.Ask;

namespace XivDesktop.Plugin.Ask;

/// <summary>
/// Streams one answer at a time from almanac's local model gateway (OpenAI chat-completions, SSE) on the
/// thread pool. Events are queued; the framework thread drains them with <see cref="Drain"/>. The bearer
/// token is read from ~/.config/almanac/token for each request and is never logged or kept in a field.
/// </summary>
public sealed class AskBackend : IDisposable
{
    private readonly IPluginLog log;
    private readonly Func<HostPaths> paths;
    private readonly HttpClient http;
    private readonly ConcurrentQueue<StreamEvent> events = new();
    private CancellationTokenSource? current;

    public AskBackend(IPluginLog log, Func<HostPaths> paths)
    {
        this.log = log;
        this.paths = paths;
        http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            UseProxy = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan, // streaming: the per-request token cancels instead
        };
    }

    public bool Busy => current is not null;

    /// <summary>Starts streaming the answer to <paramref name="body"/> (a chat-completions request). Cancels any running one.</summary>
    public void Start(string gateway, string body)
    {
        Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        current = cts;
        _ = Task.Run(() => Run(gateway, body, cts.Token));
    }

    public void Cancel()
    {
        var c = Interlocked.Exchange(ref current, null);
        if (c is null)
            return;
        try
        {
            c.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Hands queued events to <paramref name="sink"/> (framework thread).</summary>
    public void Drain(Action<StreamEvent> sink)
    {
        while (events.TryDequeue(out var e))
            sink(e);
    }

    private async Task Run(string gateway, string body, CancellationToken token)
    {
        try
        {
            var token_ = ReadToken();
            if (token_ is null)
            {
                events.Enqueue(StreamEvent.Error("no almanac token (~/.config/almanac/token); run \"almanac init\""));
                return;
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, AlmanacPaths.ChatUrl(gateway))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token_);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var text = await SafeRead(resp, token).ConfigureAwait(false);
                var parsed = ChatStreamParser.ParsePayload(text).FirstOrDefault(e => e.Kind == StreamEventKind.Error);
                events.Enqueue(StreamEvent.Error($"gateway {(int)resp.StatusCode}: {(parsed.Text is { Length: > 0 } m ? m : resp.ReasonPhrase)}"));
                return;
            }

            var parser = new ChatStreamParser();
            await using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buf = new byte[4096];
            int n;
            while ((n = await stream.ReadAsync(buf, token).ConfigureAwait(false)) > 0)
            {
                foreach (var e in parser.Feed(buf.AsSpan(0, n)))
                    events.Enqueue(e);
                if (parser.Finished)
                    return;
            }

            foreach (var e in parser.Complete())
                events.Enqueue(e);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Dismissed, replaced by a newer question, or timed out: nothing to report unless it timed out.
            if (ReferenceEquals(Volatile.Read(ref current)?.Token, token))
                events.Enqueue(StreamEvent.Error("the answer took too long"));
        }
        catch (HttpRequestException ex)
        {
            log.Information("XivDesktop ask: gateway unreachable ({Message})", ex.Message);
            events.Enqueue(StreamEvent.Error("the almanac gateway is not answering (is \"almanac gateway\" running?)"));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "XivDesktop ask: request failed");
            events.Enqueue(StreamEvent.Error("the request failed; see /xllog"));
        }
        finally
        {
            var mine = Volatile.Read(ref current);
            if (mine is not null && mine.Token == token)
                Interlocked.CompareExchange(ref current, null, mine);
        }
    }

    private static async Task<string> SafeRead(HttpResponseMessage resp, CancellationToken token)
    {
        try
        {
            var s = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return s.Length > 2000 ? s[..2000] : s;
        }
        catch
        {
            return "";
        }
    }

    private string? ReadToken()
    {
        var path = AlmanacPaths.TokenPath(paths());
        if (path is null || !File.Exists(path))
            return null;
        var t = File.ReadAllText(path).Trim();
        return t.Length == 0 ? null : t;
    }

    public void Dispose()
    {
        Cancel();
        http.Dispose();
    }
}
