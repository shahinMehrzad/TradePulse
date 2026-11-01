using Application.Research;
using Domain.Enums;

namespace Application.UnitTests;

public class DetectMarketEventsCommandHandlerTests
{
    private static DetectMarketEventsCommand CommandFor(List<MinuteBar> bars) =>
        new(ExchangeEnum.Binance, MarketTypeEnum.Futures, SeededSymbol.BTCUSDT.Sym(), SymbolListingId: 1,
            From: bars[1500].OpenTime, To: bars[^1].OpenTime);

    private static (DetectMarketEventsCommandHandler Handler, FakeCandleRepository Candles, FakeMarketEventStore Store, List<MinuteBar> Bars) Setup()
    {
        var bars = MinuteBars.Build(2000, MinuteBars.Jump(at: 1700, each: 0.015m));
        var candles = new FakeCandleRepository();
        candles.Stored.AddRange(bars.Select(MinuteBars.ToCandle));
        var store = new FakeMarketEventStore { NewCount = 1 };

        return (new DetectMarketEventsCommandHandler(candles, store), candles, store, bars);
    }

    [Fact]
    public async Task ReadsTheOneMinuteTableOfTheListing()
    {
        var (handler, candles, _, bars) = Setup();

        await handler.HandleAsync(CommandFor(bars), TestContext.Current.CancellationToken);

        Assert.Equal("binance_futures_btcusdt_1m", candles.LastRead!.Value.TableName);
    }

    [Fact]
    public async Task ReadsADayBeforeFromToKnowWhatIsUsual()
    {
        var (handler, candles, _, bars) = Setup();
        var command = CommandFor(bars);

        await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.Equal(command.From.AddMinutes(-1440), candles.LastRead!.Value.From);
    }

    [Fact]
    public async Task StoresTheEventsWithTheirReactionsAndReportsWhatWasNew()
    {
        var (handler, _, store, bars) = Setup();

        var result = await handler.HandleAsync(CommandFor(bars), TestContext.Current.CancellationToken);

        var stored = Assert.Single(Assert.Single(store.Added));
        Assert.Equal(bars[1700].OpenTime, stored.StartedAt);
        Assert.Equal([1, 5, 30, 60], stored.Reactions.Select(r => r.HorizonMinutes));
        Assert.Equal(1, result.Found);
        Assert.Equal(1, result.Added);
        Assert.True(result.CandlesRead > 1440);
    }

    [Fact]
    public async Task AnEventBeforeFromIsNotStored()
    {
        var (handler, _, store, bars) = Setup();
        var command = CommandFor(bars) with { From = bars[1800].OpenTime };

        var result = await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Found);
        Assert.Empty(Assert.Single(store.Added));
    }
}
