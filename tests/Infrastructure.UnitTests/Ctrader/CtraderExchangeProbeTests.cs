using Domain.Enums;
using Infrastructure.Exchange.Ctrader;
using Infrastructure.Exchange.Ctrader.Protocol;

namespace Infrastructure.UnitTests.Ctrader;

public class CtraderExchangeProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ItSignsInAndAsksForTheServersVersion()
    {
        await using var server = new FakeCtraderServer
        {
            Handler = _ => FakeCtraderServer.Answer(ProtoOAPayloadType.ProtoOaVersionRes, new ProtoOAVersionRes { Version = "10.1" }),
        };
        await using var connection = server.Connection();
        var probe = new CtraderExchangeProbe(connection);

        var result = await probe.PingAsync(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, Ct);

        Assert.True(result.Success);
        Assert.Single(server.ReceivedOf(ProtoOAPayloadType.ProtoOaVersionReq));
        Assert.Single(server.ReceivedOf(ProtoOAPayloadType.ProtoOaAccountAuthReq));
    }

    [Fact]
    public async Task AWithdrawnTokenIsAFailedProbeWithTheReasonAndNeverAThrow()
    {
        await using var server = new FakeCtraderServer
        {
            ApplicationAuthAnswer = FakeCtraderServer.Answer(ProtoOAPayloadType.ProtoOaErrorRes,
                new ProtoOAErrorRes { ErrorCode = "CH_ACCESS_TOKEN_INVALID", Description = "token is not valid" }),
        };
        await using var connection = server.Connection();

        var result = await new CtraderExchangeProbe(connection).PingAsync(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, Ct);

        Assert.False(result.Success);
        Assert.Contains("CH_ACCESS_TOKEN_INVALID", result.Error);
    }

    [Fact]
    public async Task ItServesPepperstoneOnlyAndHasNoStream()
    {
        await using var server = new FakeCtraderServer();
        await using var connection = server.Connection();
        var probe = new CtraderExchangeProbe(connection);

        Assert.True(probe.Serves(ExchangeEnum.Pepperstone));
        Assert.False(probe.Serves(ExchangeEnum.Binance));
        await Assert.ThrowsAsync<ArgumentException>(() => probe.PingAsync(ExchangeEnum.Binance, MarketTypeEnum.Futures, Ct));
        Assert.False((await probe.SubscribeToProbeStreamAsync(ExchangeEnum.Pepperstone, new("EURUSD"), null!, Ct)).Success);
    }
}
