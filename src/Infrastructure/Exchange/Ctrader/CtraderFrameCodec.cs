using System.Buffers.Binary;
using Google.Protobuf;
using Infrastructure.Exchange.Ctrader.Protocol;

namespace Infrastructure.Exchange.Ctrader;

/// <summary>
/// How cTrader's Open API frames a message on a TCP stream: four bytes (big endian) with the length of what follows, then one
/// serialized <see cref="ProtoMessage"/> (its payload type, the serialized message itself and the id the answer will carry).
/// </summary>
internal static class CtraderFrameCodec
{
    /// <summary>A frame longer than this is not a message of this protocol (the biggest answers, a symbol list, are well under it).</summary>
    internal const int MaxFrameBytes = 16 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, ProtoMessage message, CancellationToken cancellationToken)
    {
        var length = message.CalculateSize();
        var frame = new byte[sizeof(int) + length];
        BinaryPrimitives.WriteInt32BigEndian(frame, length);
        message.WriteTo(frame.AsSpan(sizeof(int)));

        // One write: two frames written at the same moment must not interleave (the caller serialises the calls).
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>The next message, or null when the other side closed the stream between two frames. A stream that ends inside a frame throws.</summary>
    public static async Task<ProtoMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        if (!await FillAsync(stream, header, allowEndAtStart: true, cancellationToken))
            return null;

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 0 or > MaxFrameBytes)
            throw new InvalidDataException($"A cTrader frame of {length} bytes is not valid.");

        var body = new byte[length];
        await FillAsync(stream, body, allowEndAtStart: false, cancellationToken);
        return ProtoMessage.Parser.ParseFrom(body);
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, bool allowEndAtStart, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (count == 0)
            {
                if (read == 0 && allowEndAtStart)
                    return false;

                throw new EndOfStreamException("The connection ended in the middle of a cTrader frame.");
            }

            read += count;
        }

        return true;
    }
}
