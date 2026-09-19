using System.Collections.Concurrent;
using System.Net.Sockets;
using Dalamud.Plugin.Services;
using XivDesktop.Core;
using XivDesktop.Core.Claude;

namespace XivDesktop.Plugin.Claude;

/// <summary>
/// XivDesktop's own connection to ghostty-agent, for job frames. One background thread owns the socket:
/// it connects, sends HELLO, reads frames and hands whole lines out through the events. It reconnects
/// with backoff, and on every reconnect it re-attaches the jobs the plugin still has, so a session
/// survives a reload, an agent restart and a game crash.
/// </summary>
/// <remarks>
/// The token is read from the agent's token file for the HELLO frame and nothing else: it is never
/// logged, never persisted by XivDesktop, and never part of an error message.
/// </remarks>
public sealed class AgentConnection : IJobTransport
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly IPluginLog log;
    private readonly Func<AgentEndpoint> endpoint;
    private readonly Func<bool> enabled;
    private readonly HostPaths paths;
    private readonly CancellationTokenSource stopping = new();
    private readonly BlockingCollection<byte[]> outgoing = new(new ConcurrentQueue<byte[]>(), 4096);
    private readonly ConcurrentDictionary<uint, Action<uint, string>> pending = new();
    private readonly ConcurrentDictionary<uint, LineSplitter> splitters = new();
    private readonly HashSet<uint> attached = [];
    private readonly Lock attachedGate = new();
    private readonly Thread worker;

    private int nextRequest;
    private volatile bool ready;
    private volatile string problem = "not connected yet";

    public AgentConnection(IPluginLog log, HostPaths paths, Func<AgentEndpoint> endpoint, Func<bool> enabled)
    {
        this.log = log;
        this.paths = paths;
        this.endpoint = endpoint;
        this.enabled = enabled;
        worker = new Thread(Run) { IsBackground = true, Name = "XivDesktop.Claude.Agent" };
        worker.Start();
    }

    public bool Ready => ready;

    public string Problem => problem;

    public event Action<uint, bool, string>? Line;

    public event Action<JobEnded>? Ended;

    public event Action? Reconnected;

    public uint Open(string cwd, IReadOnlyList<string> argv, IReadOnlyList<string> env, Action<uint, string> opened)
    {
        var request = (uint)Interlocked.Increment(ref nextRequest);
        pending[request] = opened;
        if (!Queue(JobProtocol.Open(request, cwd, argv, env, keep: true)))
        {
            pending.TryRemove(request, out _);
            opened(0, problem.Length > 0 ? problem : "the agent is not connected");
        }

        return request;
    }

    public void Send(uint job, string line) => Queue(JobProtocol.Stdin(job, line));

    /// <summary>Closes the job's stdin: the turn stream ends and Claude sees EOF.</summary>
    public void EndInput(uint job) => Queue(JobProtocol.CloseStdin(job));

    public void Interrupt(uint job) => Queue(JobProtocol.Interrupt(job));

    public void Close(uint job)
    {
        Forget(job);
        Queue(JobProtocol.Close(job));
    }

    public void Attach(uint job)
    {
        lock (attachedGate)
            attached.Add(job);
        splitters.TryRemove(job, out _);
        Queue(JobProtocol.Attach(job));
    }

    public void Dispose()
    {
        stopping.Cancel();
        outgoing.CompleteAdding();
        try
        {
            worker.Join(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // The worker is a background thread; the process can go without it.
        }

        stopping.Dispose();
        outgoing.Dispose();
    }

    private void Forget(uint job)
    {
        lock (attachedGate)
            attached.Remove(job);
        splitters.TryRemove(job, out _);
    }

    private bool Queue(byte[] frame)
    {
        if (stopping.IsCancellationRequested)
            return false;
        try
        {
            return outgoing.TryAdd(frame, 0);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Run()
    {
        var backoff = MinBackoff;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                if (!enabled())
                {
                    problem = "/claude is turned off in the settings";
                    stopping.Token.WaitHandle.WaitOne(MinBackoff);
                    continue;
                }

                if (Session())
                    backoff = MinBackoff;
            }
            catch (Exception ex)
            {
                problem = Describe(ex);
                log.Debug("XivDesktop: the Claude agent connection dropped: {Problem}", problem);
            }
            finally
            {
                ready = false;
            }

            if (stopping.IsCancellationRequested)
                return;
            stopping.Token.WaitHandle.WaitOne(backoff);
            backoff = TimeSpan.FromMilliseconds(Math.Min(MaxBackoff.TotalMilliseconds, backoff.TotalMilliseconds * 2));
        }
    }

    /// <summary>One connection, from HELLO to the socket going away. True when it got as far as ready.</summary>
    private bool Session()
    {
        var target = endpoint();
        var token = target.ReadToken(paths, File.ReadAllText, out var why);
        if (token.Length == 0)
        {
            problem = why;
            return false;
        }

        using var client = new TcpClient();
        if (!client.ConnectAsync(target.Host, target.Port, stopping.Token).AsTask().Wait(TimeSpan.FromSeconds(5)))
        {
            problem = $"no answer from ghostty-agent at {target}";
            return false;
        }

        client.NoDelay = true;
        using var stream = client.GetStream();
        Write(stream, JobProtocol.Hello(token));

        var reader = new FrameReader();
        var buffer = new byte[64 * 1024];
        var greeted = false;
        var sender = new Thread(() => Pump(stream)) { IsBackground = true, Name = "XivDesktop.Claude.Send" };
        sender.Start();

        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    problem = "ghostty-agent closed the connection";
                    return greeted;
                }

                reader.Append(buffer.AsSpan(0, read));
                if (reader.Desynchronised)
                {
                    problem = "the agent sent a frame that is too long";
                    return greeted;
                }

                while (reader.Next() is { } frame)
                {
                    if (!greeted)
                    {
                        if (!Greet(frame))
                            return false;
                        greeted = true;
                        continue;
                    }

                    Handle(frame);
                }
            }
        }
        finally
        {
            ready = false;
            try
            {
                client.Close();
            }
            catch (Exception)
            {
                // Already gone.
            }
        }

        return greeted;
    }

    private bool Greet(in Frame frame)
    {
        if (frame.Type == AgentFrames.Err)
        {
            // "bad token" is the usual one; the token itself is never in the message.
            problem = "ghostty-agent refused the connection: " + frame.Text;
            return false;
        }

        if (frame.Type != AgentFrames.Ok)
        {
            problem = "ghostty-agent said something unexpected";
            return false;
        }

        var hello = AgentHello.Parse(frame.Text);
        if (!hello.SupportsJobs)
        {
            problem = $"ghostty-agent {hello.Version} has no jobs; version {AgentFrames.JobsVersion} or newer is needed";
            return false;
        }

        problem = "";
        ready = true;
        uint[] live;
        lock (attachedGate)
            live = [.. attached];
        foreach (var job in live)
        {
            splitters.TryRemove(job, out _);
            Queue(JobProtocol.Attach(job));
        }

        try
        {
            Reconnected?.Invoke();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "XivDesktop: a Claude session failed to re-attach");
        }

        return true;
    }

    private void Handle(in Frame frame)
    {
        switch (frame.Type)
        {
            case AgentFrames.JOpened:
                if (JobProtocol.ParseOpened(frame) is { } opened && pending.TryRemove(opened.Request, out var callback))
                {
                    if (opened.Ok)
                    {
                        lock (attachedGate)
                            attached.Add(opened.Job);
                    }

                    Invoke(() => callback(opened.Job, opened.Error));
                }

                break;

            case AgentFrames.JOut:
                if (JobProtocol.ParseOutput(frame) is { } output)
                {
                    var splitter = splitters.GetOrAdd(output.Job, _ => new LineSplitter());
                    foreach (var line in splitter.Feed(output.Bytes))
                        Invoke(() => Line?.Invoke(output.Job, output.IsStderr, line));
                }

                break;

            case AgentFrames.JExit:
                if (JobProtocol.ParseExit(frame) is { } exit)
                {
                    if (splitters.TryRemove(exit.Job, out var last) && last.Flush() is { Length: > 0 } tail)
                        Invoke(() => Line?.Invoke(exit.Job, false, tail));
                    Forget(exit.Job);
                    Invoke(() => Ended?.Invoke(new JobEnded(exit.Job, exit.Status, exit.Describe())));
                }

                break;

            case AgentFrames.Err:
                log.Debug("XivDesktop: the agent refused a job frame: {Text}", frame.Text);
                break;
        }
    }

    private void Invoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "XivDesktop: a Claude event handler threw");
        }
    }

    private void Pump(NetworkStream stream)
    {
        try
        {
            foreach (var frame in outgoing.GetConsumingEnumerable(stopping.Token))
            {
                Write(stream, frame);
                if (!stream.Socket.Connected)
                    return;
            }
        }
        catch (Exception)
        {
            // The socket went away, or the plugin is unloading: the reader notices and reconnects.
        }
    }

    private static void Write(NetworkStream stream, byte[] frame) => stream.Write(frame, 0, frame.Length);

    private static string Describe(Exception ex) => ex switch
    {
        SocketException socket => "ghostty-agent: " + socket.SocketErrorCode,
        IOException => "the connection to ghostty-agent broke",
        OperationCanceledException => "stopping",
        _ => ex.GetType().Name,
    };
}
