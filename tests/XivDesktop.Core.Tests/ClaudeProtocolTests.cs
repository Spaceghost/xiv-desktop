using System.Buffers.Binary;
using System.Text;
using XivDesktop.Core;
using XivDesktop.Core.Claude;

namespace XivDesktop.Core.Tests;

public class ClaudeProtocolTests
{
    [Fact]
    public void EncodesTheFiveByteHeader()
    {
        var frame = FrameCodec.Encode(AgentFrames.Hello, "abc");
        Assert.Equal(AgentFrames.Hello, frame[0]);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(1)));
        Assert.Equal("abc", Encoding.UTF8.GetString(frame, 5, 3));
    }

    [Fact]
    public void ReadsFramesOutOfASplitStream()
    {
        var a = FrameCodec.Encode(AgentFrames.Ok, "ghostty-agent 4 nonce");
        var b = JobProtocol.Signal(7, AgentFrames.SigInt);
        var stream = a.Concat(b).ToArray();

        var reader = new FrameReader();
        // Feed it a byte at a time: the reader must not hand out a frame until it is whole.
        var frames = new List<Frame>();
        foreach (var by in stream)
        {
            reader.Append([by]);
            while (reader.Next() is { } f)
                frames.Add(f);
        }

        Assert.Equal(2, frames.Count);
        Assert.Equal(AgentFrames.Ok, frames[0].Type);
        Assert.Equal("ghostty-agent 4 nonce", frames[0].Text);
        Assert.Equal(AgentFrames.JSig, frames[1].Type);
        Assert.Equal(AgentFrames.SigInt, frames[1].Payload[4]);
    }

    [Fact]
    public void RefusesAFrameLongerThanTheProtocolAllows()
    {
        var header = new byte[5];
        header[0] = AgentFrames.JOut;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), (uint)AgentFrames.MaxPayload + 1);
        var reader = new FrameReader();
        reader.Append(header);
        Assert.Null(reader.Next());
        Assert.True(reader.Desynchronised);
    }

    [Fact]
    public void ParsesTheAgentGreeting()
    {
        Assert.Equal(3, AgentHello.Parse("ghostty-agent 3 f00d").Version);
        Assert.Equal("f00d", AgentHello.Parse("ghostty-agent 3 f00d").Nonce);
        Assert.False(AgentHello.Parse("ghostty-agent 3 f00d").SupportsJobs);
        Assert.True(AgentHello.Parse("ghostty-agent 4 f00d").SupportsJobs);
        Assert.Equal(0, AgentHello.Parse("something else").Version);
        Assert.Equal(0, AgentHello.Parse(null).Version);
    }

    [Fact]
    public void BuildsAJobOpenWithArgvThenEnvAndTheCwdEntry()
    {
        var frame = JobProtocol.Open(42, "/home/player/code", ["@claude", "{\"model\":\"opus\"}"], ["TERM=dumb"], keep: true);
        Assert.Equal(AgentFrames.JOpen, frame[0]);
        var payload = frame.AsSpan(5);
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(payload));
        Assert.Equal(AgentFrames.FlagKeep, payload[4]);

        // argv entries, an empty entry, then env — the working directory as the PWD= entry.
        var parts = Encoding.UTF8.GetString(payload[5..]).Split('\0');
        Assert.Equal(["@claude", "{\"model\":\"opus\"}"], parts[0..2]);
        Assert.Equal("", parts[2]);
        Assert.Equal("PWD=/home/player/code", parts[3]);
        Assert.Equal("TERM=dumb", parts[4]);

        Assert.Equal(0, JobProtocol.Open(1, "", ["x"], keep: false).AsSpan(5)[4]);
    }

    [Fact]
    public void ParsesOpenedOutputAndExit()
    {
        var ok = JobProtocol.ParseOpened(Decode(Opened(9, 3, 1234, "")))!.Value;
        Assert.True(ok.Ok);
        Assert.Equal(3u, ok.Job);
        Assert.Equal(1234u, ok.Pid);

        var failed = JobProtocol.ParseOpened(Decode(Opened(9, 0, 0, "no such program")))!.Value;
        Assert.Equal(9u, failed.Request);
        Assert.False(failed.Ok);
        Assert.Equal("no such program", failed.Error);

        var out1 = JobProtocol.ParseOutput(Decode(Output(3, AgentFrames.StreamStderr, "oops")))!.Value;
        Assert.Equal(3u, out1.Job);
        Assert.True(out1.IsStderr);
        Assert.Equal("oops", Encoding.UTF8.GetString(out1.Bytes));

        Assert.Equal("exit 7", JobProtocol.ParseExit(Decode(Exit(3, 7)))!.Value.Describe());
        Assert.True(JobProtocol.ParseExit(Decode(Exit(3, 137)))!.Value.Killed);
        Assert.True(JobProtocol.ParseExit(Decode(Exit(3, AgentFrames.NoJob)))!.Value.Unknown);
    }

    [Fact]
    public void AttachAndStdinEofAreJustTheJobId()
    {
        var attach = JobProtocol.Attach(5);
        Assert.Equal(AgentFrames.JAttach, attach[0]);
        Assert.Equal(5u, FrameCodec.ReadU32(attach.AsSpan(5), 0));

        // JDATA with no bytes closes stdin, which is how a turn stream ends.
        var eof = JobProtocol.CloseStdin(5);
        Assert.Equal(AgentFrames.JData, eof[0]);
        Assert.Equal(AgentFrames.HeaderSize + 4, eof.Length);

        var stdin = JobProtocol.Stdin(5, "{}\n");
        Assert.Equal("{}\n", Encoding.UTF8.GetString(stdin, AgentFrames.HeaderSize + 4, 3));
    }

    [Fact]
    public void SplitsOutputIntoWholeLines()
    {
        var splitter = new LineSplitter();
        Assert.Empty(splitter.Feed("{\"a\":1"));
        Assert.Equal(["{\"a\":1}"], splitter.Feed("}\r\n"));
        Assert.Equal(["one", "two"], splitter.Feed("one\ntwo\nthr"));
        Assert.Equal("thr", splitter.Flush());
        Assert.Equal("", splitter.Flush());
    }

    [Fact]
    public void SplitsMultiByteCharactersAcrossFrames()
    {
        var splitter = new LineSplitter();
        var bytes = Encoding.UTF8.GetBytes("caf\u00e9\n");
        Assert.Empty(splitter.Feed(bytes.AsSpan(0, 4))); // cuts the é in half
        Assert.Equal(["caf\u00e9"], splitter.Feed(bytes.AsSpan(4)));
    }

    [Fact]
    public void ResolvesTheEndpointFromDefaultsAndOverrides()
    {
        var paths = HostPaths.Native("/home/player");
        var fallback = AgentEndpoint.Resolve(paths);
        Assert.Equal("127.0.0.1", fallback.Host);
        Assert.Equal(7777, fallback.Port);
        Assert.Equal("/home/player/.config/ghostty-agent/token", fallback.TokenPath);

        var configured = AgentEndpoint.Resolve(paths, "10.0.0.2", 9000, "/run/token");
        Assert.Equal("10.0.0.2:9000", configured.ToString());
        Assert.Equal("/run/token", configured.TokenPath);

        // A nonsense port falls back rather than failing to connect at all.
        Assert.Equal(7777, AgentEndpoint.Resolve(paths, port: 99999).Port);
    }

    [Fact]
    public void ReadsTheTokenAndNeverPutsItInAMessage()
    {
        var paths = HostPaths.Wine("/home/player");
        var endpoint = AgentEndpoint.Resolve(paths);
        var token = endpoint.ReadToken(paths, path =>
        {
            Assert.Equal(@"Z:\home\player\.config\ghostty-agent\token", path);
            return "  s3cret\n";
        }, out var problem);
        Assert.Equal("s3cret", token);
        Assert.Equal("", problem);

        var empty = endpoint.ReadToken(paths, _ => "  ", out var why);
        Assert.Equal("", empty);
        Assert.Contains("ghostty-agent/token", why);
        Assert.DoesNotContain("s3cret", why);

        var missing = endpoint.ReadToken(paths, _ => throw new FileNotFoundException(), out var reason);
        Assert.Equal("", missing);
        Assert.Contains("could not read", reason);
    }

    private static Frame Decode(byte[] frame)
    {
        Assert.True(FrameCodec.TryDecode(frame, out var decoded, out _, out _));
        return decoded;
    }

    private static byte[] Opened(uint request, uint job, uint pid, string error)
    {
        var payload = new byte[12 + Encoding.UTF8.GetByteCount(error)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, request);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), job);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), pid);
        Encoding.UTF8.GetBytes(error).CopyTo(payload, 12);
        return FrameCodec.Encode(AgentFrames.JOpened, payload);
    }

    private static byte[] Output(uint job, byte stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var payload = new byte[5 + bytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, job);
        payload[4] = stream;
        bytes.CopyTo(payload, 5);
        return FrameCodec.Encode(AgentFrames.JOut, payload);
    }

    private static byte[] Exit(uint job, int status)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, job);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), status);
        return FrameCodec.Encode(AgentFrames.JExit, payload);
    }
}
