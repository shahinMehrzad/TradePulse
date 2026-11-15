using System.Buffers.Binary;
using Google.Protobuf;
using Infrastructure.Exchange.Ctrader;
using Infrastructure.Exchange.Ctrader.Protocol;

namespace Infrastructure.UnitTests.Ctrader;

public class CtraderFrameCodecTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProtoMessage Message(string id = "abc") => new()
    {
        PayloadType = (uint)ProtoOAPayloadType.ProtoOaGetTrendbarsReq,
        Payload = new ProtoOAGetTrendbarsReq { CtidTraderAccountId = 7, SymbolId = 9, Period = ProtoOATrendbarPeriod.H1 }.ToByteString(),
        ClientMsgId = id,
    };

    /// <summary>A stream that hands out at most one byte per read, the way a slow network can.</summary>
    private sealed class OneByteAtATime(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    [Fact]
    public async Task AMessageWrittenIsReadBackWithTheLengthInFrontBigEndian()
    {
        var stream = new MemoryStream();
        var message = Message();

        await CtraderFrameCodec.WriteAsync(stream, message, Ct);

        var bytes = stream.ToArray();
        Assert.Equal(message.CalculateSize(), BinaryPrimitives.ReadInt32BigEndian(bytes));
        Assert.Equal(sizeof(int) + message.CalculateSize(), bytes.Length);

        stream.Position = 0;
        Assert.Equal(message, await CtraderFrameCodec.ReadAsync(stream, Ct));
    }

    [Fact]
    public async Task SeveralFramesFollowEachOtherAndTheEndBetweenFramesIsNull()
    {
        var stream = new MemoryStream();
        await CtraderFrameCodec.WriteAsync(stream, Message("one"), Ct);
        await CtraderFrameCodec.WriteAsync(stream, Message("two"), Ct);
        stream.Position = 0;

        Assert.Equal("one", (await CtraderFrameCodec.ReadAsync(stream, Ct))!.ClientMsgId);
        Assert.Equal("two", (await CtraderFrameCodec.ReadAsync(stream, Ct))!.ClientMsgId);
        Assert.Null(await CtraderFrameCodec.ReadAsync(stream, Ct));
    }

    [Fact]
    public async Task AFrameArrivingAByteAtATimeIsStillOneMessage()
    {
        var source = new MemoryStream();
        await CtraderFrameCodec.WriteAsync(source, Message(), Ct);

        var read = await CtraderFrameCodec.ReadAsync(new OneByteAtATime(source.ToArray()), Ct);

        Assert.Equal(Message(), read);
    }

    [Theory]
    [InlineData(2)]    // inside the length
    [InlineData(10)]   // inside the body
    public async Task AStreamThatEndsInsideAFrameIsAnError(int keep)
    {
        var source = new MemoryStream();
        await CtraderFrameCodec.WriteAsync(source, Message(), Ct);

        await Assert.ThrowsAsync<EndOfStreamException>(() => CtraderFrameCodec.ReadAsync(new MemoryStream(source.ToArray()[..keep]), Ct));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(CtraderFrameCodecLimits.TooLong)]
    public async Task ALengthNoMessageCanHaveIsRefusedBeforeAnythingIsAllocated(int length)
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, length);

        await Assert.ThrowsAsync<InvalidDataException>(() => CtraderFrameCodec.ReadAsync(new MemoryStream(header), Ct));
    }

    private static class CtraderFrameCodecLimits
    {
        public const int TooLong = 16 * 1024 * 1024 + 1;
    }
}
