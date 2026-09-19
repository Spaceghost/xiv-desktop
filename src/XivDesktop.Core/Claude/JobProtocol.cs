using System.Buffers.Binary;
using System.Text;

namespace XivDesktop.Core.Claude;

/// <summary>The agent's greeting, <c>"ghostty-agent &lt;version&gt; &lt;nonce&gt;"</c>.</summary>
public readonly record struct AgentHello(int Version, string Nonce)
{
    /// <summary>Version 1 agents greet without a nonce; anything unparseable is version 0.</summary>
    public static AgentHello Parse(string? text)
    {
        var parts = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("ghostty-agent", StringComparison.Ordinal) || !int.TryParse(parts[1], out var v))
            return new AgentHello(0, "");
        return new AgentHello(v, parts.Length > 2 ? parts[2] : "");
    }

    public bool SupportsJobs => Version >= AgentFrames.JobsVersion;
}

/// <summary>A JOPENED frame: the request it answers, the job id (0 when it failed), the pid, and why.</summary>
public readonly record struct JobOpened(uint Request, uint Job, uint Pid, string Error)
{
    public bool Ok => Job != 0;
}

/// <summary>A JOUT frame: which job, which stream, and the bytes.</summary>
public readonly record struct JobOutput(uint Job, byte Stream, byte[] Bytes)
{
    public bool IsStderr => Stream == AgentFrames.StreamStderr;
}

/// <summary>A JEXIT frame.</summary>
public readonly record struct JobExit(uint Job, int Status)
{
    /// <summary>The agent has no such job: it was never kept, or it has been forgotten.</summary>
    public bool Unknown => Status == AgentFrames.NoJob;

    /// <summary>The job was killed; <c>status - 128</c> is the signal.</summary>
    public bool Killed => Status >= 128;

    public string Describe() => Unknown ? "the agent no longer has that job"
        : Killed ? $"killed (signal {Status - 128})"
        : $"exit {Status}";
}

/// <summary>
/// Builds the version 4 job frames and parses the ones the agent sends back
/// (ghostty-dalamud <c>docs/JOBS.md</c>). Pure: no sockets, no logging, and nothing here ever sees the
/// connection token.
/// </summary>
public static class JobProtocol
{
    /// <summary>
    /// JOPEN: <c>req u32, flags u8, argv entries each NUL-terminated, an empty entry, then env entries</c>.
    /// <paramref name="cwd"/> is passed as the <c>PWD=</c> env entry the agent reads as a directory.
    /// <paramref name="keep"/> asks the agent to hold the job when this connection goes away, so a
    /// reload, a crash or a restart can JATTACH to it again.
    /// </summary>
    public static byte[] Open(uint request, string cwd, IReadOnlyList<string> argv, IReadOnlyList<string>? env = null, bool keep = true)
    {
        var body = new List<byte>(256);
        var id = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(id, request);
        body.AddRange(id);
        body.Add(keep ? AgentFrames.FlagKeep : (byte)0);
        foreach (var a in argv)
            AppendNul(body, a);
        body.Add(0);
        if (cwd.Trim().Length > 0)
            AppendNul(body, AgentFrames.CwdEnv + cwd.Trim());
        foreach (var e in env ?? [])
            AppendNul(body, e);
        return FrameCodec.Encode(AgentFrames.JOpen, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(body));
    }

    /// <summary>JDATA: bytes for the job's stdin. Claude's stream-json input wants one JSON object per line.</summary>
    public static byte[] Stdin(uint job, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var payload = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, job);
        bytes.CopyTo(payload, 4);
        return FrameCodec.Encode(AgentFrames.JData, payload);
    }

    /// <summary>JDATA with no bytes: the job sees EOF on stdin, which is how a turn stream ends.</summary>
    public static byte[] CloseStdin(uint job) => FrameCodec.Encode(AgentFrames.JData, U32(job));

    /// <summary>JCLOSE: kill the job and stop keeping it. Only "End session" does this.</summary>
    public static byte[] Close(uint job) => FrameCodec.Encode(AgentFrames.JClose, U32(job));

    public static byte[] Signal(uint job, byte signal)
    {
        var payload = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, job);
        payload[4] = signal;
        return FrameCodec.Encode(AgentFrames.JSig, payload);
    }

    /// <summary>Interrupt: what the stop button sends, so the turn ends without killing the session.</summary>
    public static byte[] Interrupt(uint job) => Signal(job, AgentFrames.SigInt);

    /// <summary>JATTACH: take a kept job over on this connection; the agent replays its ring.</summary>
    public static byte[] Attach(uint job) => FrameCodec.Encode(AgentFrames.JAttach, U32(job));

    public static byte[] Hello(string token) => FrameCodec.Encode(AgentFrames.Hello, token);

    public static JobOpened? ParseOpened(in Frame frame)
    {
        if (frame.Type != AgentFrames.JOpened || frame.Payload.Length < 12)
            return null;
        var request = FrameCodec.ReadU32(frame.Payload, 0);
        var job = FrameCodec.ReadU32(frame.Payload, 4);
        var pid = FrameCodec.ReadU32(frame.Payload, 8);
        var error = frame.Payload.Length > 12 ? Encoding.UTF8.GetString(frame.Payload, 12, frame.Payload.Length - 12) : "";
        return new JobOpened(request, job, pid, error);
    }

    public static JobOutput? ParseOutput(in Frame frame)
    {
        if (frame.Type != AgentFrames.JOut || frame.Payload.Length < 5)
            return null;
        var job = FrameCodec.ReadU32(frame.Payload, 0);
        return new JobOutput(job, frame.Payload[4], frame.Payload[5..]);
    }

    public static JobExit? ParseExit(in Frame frame)
    {
        if (frame.Type != AgentFrames.JExit || frame.Payload.Length < 8)
            return null;
        return new JobExit(FrameCodec.ReadU32(frame.Payload, 0), FrameCodec.ReadI32(frame.Payload, 4));
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private static void AppendNul(List<byte> body, string text)
    {
        body.AddRange(Encoding.UTF8.GetBytes(text.Replace("\0", "")));
        body.Add(0);
    }
}

/// <summary>
/// Splits a job's output into whole lines. Claude's <c>stream-json</c> output is one JSON object per
/// line, and a JOUT frame can carry a partial line or several; a line longer than
/// <see cref="LineLimit"/> is handed on as it is, so a runaway job cannot grow this without bound.
/// </summary>
public sealed class LineSplitter
{
    public const int LineLimit = 4 * 1024 * 1024;

    private readonly StringBuilder pending = new();
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();

    public IEnumerable<string> Feed(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var n = decoder.GetChars(bytes, chars, flush: false);
        return Feed(new string(chars, 0, n));
    }

    public IEnumerable<string> Feed(string text)
    {
        var lines = new List<string>();
        foreach (var c in text)
        {
            if (c == '\n')
            {
                lines.Add(Take());
            }
            else if (c != '\r')
            {
                pending.Append(c);
                if (pending.Length >= LineLimit)
                    lines.Add(Take());
            }
        }

        return lines;
    }

    /// <summary>Whatever is buffered without a newline (the job ended mid-line).</summary>
    public string Flush() => pending.Length == 0 ? "" : Take();

    private string Take()
    {
        var line = pending.ToString();
        pending.Clear();
        return line;
    }
}
