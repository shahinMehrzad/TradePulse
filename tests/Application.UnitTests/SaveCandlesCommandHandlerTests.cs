using Application.Commands.Candles;
using Application.Dtos;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.UnitTests;

public class SaveCandlesCommandHandlerTests
{
    private static readonly DateTime Open = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CandleDto Dto(DateTime open) =>
        new(open, 1m, 4m, 0.5m, 3m, 10m, open.AddHours(1).AddMilliseconds(-1), 25m, 42, 6m, 15m);

    [Fact]
    public async Task WritesToTheTableMatchingTheCommand()
    {
        var repository = new FakeCandleRepository();
        var handler = new SaveCandlesCommandHandler(repository);
        var command = new SaveCandlesCommand(ExchangeEnum.Binance, MarketTypeEnum.Futures, SeededSymbol.SOLUSDT.Sym(), TimeFrameEnum._1H,
            [Dto(Open)]);

        await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        var insert = Assert.Single(repository.Inserts);
        Assert.Equal("binance_futures_solusdt_1h", insert.TableName);
    }

    [Fact]
    public async Task MapsEveryDtoToACandleInOrder()
    {
        var repository = new FakeCandleRepository();
        var handler = new SaveCandlesCommandHandler(repository);
        var command = new SaveCandlesCommand(ExchangeEnum.Binance, MarketTypeEnum.Spot, SeededSymbol.BTCUSDT.Sym(), TimeFrameEnum._1H,
            [Dto(Open), Dto(Open.AddHours(1))]);

        await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        var candles = Assert.Single(repository.Inserts).Candles;
        Assert.Equal([Open, Open.AddHours(1)], candles.Select(c => c.TimeRange.OpenTime));
        Assert.All(candles, c =>
        {
            Assert.Equal(4m, c.High.Value);
            Assert.Equal(42, c.NumberOfTrades);
        });
    }

    [Fact]
    public async Task ReturnsTheNumberOfInsertedRowsReportedByTheRepository()
    {
        var repository = new FakeCandleRepository { InsertResult = 1 };
        var handler = new SaveCandlesCommandHandler(repository);
        var command = new SaveCandlesCommand(ExchangeEnum.Binance, MarketTypeEnum.Spot, SeededSymbol.BTCUSDT.Sym(), TimeFrameEnum._1H,
            [Dto(Open), Dto(Open.AddHours(1))]);

        var inserted = await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        // Duplicates are skipped by the database, so this is not necessarily the number of candles sent.
        Assert.Equal(1, inserted);
    }

    [Fact]
    public async Task RejectsUndefinedEnumValuesAndUnsafeSymbolNamesBeforeTouchingTheRepository()
    {
        var repository = new FakeCandleRepository();
        var handler = new SaveCandlesCommandHandler(repository);
        var commands = new[]
        {
            new SaveCandlesCommand((ExchangeEnum)99, MarketTypeEnum.Spot, SeededSymbol.BTCUSDT.Sym(), TimeFrameEnum._1H, [Dto(Open)]),
            new SaveCandlesCommand(ExchangeEnum.Binance, (MarketTypeEnum)99, SeededSymbol.BTCUSDT.Sym(), TimeFrameEnum._1H, [Dto(Open)]),
            new SaveCandlesCommand(ExchangeEnum.Binance, MarketTypeEnum.Spot, SeededSymbol.BTCUSDT.Sym(), (TimeFrameEnum)12345, [Dto(Open)]),
            new SaveCandlesCommand(ExchangeEnum.Binance, MarketTypeEnum.Spot, new ExchangeSymbol("BTCUSDT; DROP TABLE x"), TimeFrameEnum._1H, [Dto(Open)]),
        };

        foreach (var command in commands)
            await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command, TestContext.Current.CancellationToken));

        Assert.Empty(repository.Inserts);
    }
}
