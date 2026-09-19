using System.Buffers.Binary;
using System.Text;

namespace XivDesktop.Core.Claude;

/// <summary>One decoded frame: its type and the payload bytes (a copy, owned by the caller).</summary>
public readonly record struct Frame(byte Type, byte[] Payload)
{
    public string Text => Encoding.UTF8.GetString(Payload);
}

/// <summary>
/// Encodes and decodes ghostty-agent frames (<c>u8 type, u32 little-endian length, payload</c>).
/// Pure and total: a malformed length is reported, never thrown, and nothing here logs.
/// </summary>
public static class FrameCodec
{
    public static byte[] Encode(byte type, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[AgentFrames.HeaderSize + payload.Length];
        frame[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(AgentFrames.HeaderSize));
        return frame;
    }

    public static byte[] Encode(byte type, string text) => Encode(type, Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Reads one frame from the front of <paramref name="buffer"/>. Returns false when fewer than a whole
    /// frame is buffered; <paramref name="consumed"/> then stays 0. <paramref name="tooLong"/> means the
    /// header asks for more than <see cref="AgentFrames.MaxPayload"/>: the connection must be dropped,
    /// because the stream can no longer be resynchronised.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out Frame frame, out int consumed, out bool tooLong)
    {
        frame = default;
        consumed = 0;
        tooLong = false;
        if (buffer.Length < AgentFrames.HeaderSize)
            return false;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer[1..]);
        if (length > AgentFrames.MaxPayload)
        {
            tooLong = true;
            return false;
        }

        var total = AgentFrames.HeaderSize + (int)length;
        if (buffer.Length < total)
            return false;
        frame = new Frame(buffer[0], buffer.Slice(AgentFrames.HeaderSize, (int)length).ToArray());
        consumed = total;
        return true;
    }

    public static uint ReadU32(ReadOnlySpan<byte> p, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(p[offset..]);

    public static int ReadI32(ReadOnlySpan<byte> p, int offset) => BinaryPrimitives.ReadInt32LittleEndian(p[offset..]);

    public static ulong ReadU64(ReadOnlySpan<byte> p, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(p[offset..]);
}

/// <summary>
/// A growable receive buffer that hands out whole frames. Not thread safe: one reader owns it.
/// </summary>
public sealed class FrameReader
{
    private byte[] buffer = new byte[16 * 1024];
    private int length;

    /// <summary>Set once a header asked for more than the protocol allows; the connection must be dropped.</summary>
    public bool Desynchronised { get; private set; }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (length + data.Length > buffer.Length)
        {
            var size = buffer.Length;
            while (size < length + data.Length)
                size *= 2;
            Array.Resize(ref buffer, size);
        }

        data.CopyTo(buffer.AsSpan(length));
        length += data.Length;
    }

    /// <summary>The next whole frame, or null while one is still arriving.</summary>
    public Frame? Next()
    {
        if (Desynchronised)
            return null;
        if (!FrameCodec.TryDecode(buffer.AsSpan(0, length), out var frame, out var consumed, out var tooLong))
        {
            Desynchronised |= tooLong;
            return null;
        }

        buffer.AsSpan(consumed, length - consumed).CopyTo(buffer);
        length -= consumed;
        return frame;
    }

    public void Reset()
    {
        length = 0;
        Desynchronised = false;
    }
}
