using System.Buffers.Binary;

namespace ScreenDrop.Protocol;

public enum FrameType : byte
{
    Hello = 1,
    Offer = 2,
    Accept = 3,
    Chunk = 4,
    Done = 5,
    Ack = 6,
    Error = 7,
    Ping = 8,
    Pong = 9,
}

public static class Frames
{
    // 256 KB data chunks + headers; anything larger than this is a corrupt or hostile stream.
    public const int ChunkSize = 256 * 1024;
    public const int MaxFrameSize = ChunkSize + 64 * 1024;

    public static async Task WriteAsync(Stream stream, FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var header = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(payload.Length + 1));
        header[4] = (byte)type;
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (payload.Length > 0)
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<(FrameType Type, byte[] Payload)> ReadAsync(Stream stream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await ReadExactAsync(stream, lenBuf, ct).ConfigureAwait(false);
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
        if (len < 1 || len > MaxFrameSize)
            throw new InvalidDataException($"Invalid frame length {len}");

        var body = new byte[len];
        await ReadExactAsync(stream, body, ct).ConfigureAwait(false);
        var type = (FrameType)body[0];
        var payload = new byte[len - 1];
        Array.Copy(body, 1, payload, 0, payload.Length);
        return (type, payload);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Peer closed the connection");
            read += n;
        }
    }
}

/// <summary>Binary chunk frame: 32-byte SHA-256 file id, 8-byte LE offset, then data.</summary>
public static class ChunkFrame
{
    public const int HeaderSize = 40;

    public static byte[] Build(byte[] fileId, long offset, ReadOnlySpan<byte> data)
    {
        if (fileId.Length != 32) throw new ArgumentException("fileId must be 32 bytes");
        var buf = new byte[HeaderSize + data.Length];
        fileId.CopyTo(buf, 0);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(32), offset);
        data.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    public static (byte[] FileId, long Offset, ReadOnlyMemory<byte> Data) Parse(byte[] payload)
    {
        if (payload.Length < HeaderSize) throw new InvalidDataException("Chunk frame too short");
        var fileId = payload[..32];
        long offset = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(32));
        return (fileId, offset, payload.AsMemory(HeaderSize));
    }
}
