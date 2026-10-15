using Application.Commands;
using Application.Dtos;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;

namespace UnitTests;

public class SaveCandlesCommandHandlerTests
{
    private static readonly DateTime Open = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CandleDto Dto(DateTime openTime, decimal close = 105m) =>
        new(openTime, 100m, 110m, 95m, close, 12.5m, openTime.AddHours(4), 1300m, 321, 6m, 640m);

    private static SaveCandlesCommand Command(params CandleDto[] candles) =>
        new(ExchangeEnum.Binance, MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._4H, candles);

    [Fact]
    public async Task HandleAsync_InsertsMappedCandlesIntoTheResolvedTable()
    {
        var repository = new FakeCandleRepository();
        var handler = new SaveCandlesCommandHandler(repository);

        var inserted = await handler.HandleAsync(
            Command(Dto(Open), Dto(Open.AddHours(4), close: 120m)),
            CancellationToken.None);

        Assert.Equal(2, inserted);
        Assert.Equal("binance_futures_btcusdt_4h", repository.LastTableName);
        Assert.Collection(repository.LastCandles,
            first => Assert.Equal(105m, first.Close.Value),
            second => Assert.Equal(120m, second.Close.Value));
    }

    [Fact]
    public async Task HandleAsync_WithNoCandles_InsertsNothing()
    {
        var repository = new FakeCandleRepository();
        var handler = new SaveCandlesCommandHandler(repository);

        var inserted = await handler.HandleAsync(Command(), CancellationToken.None);

        Assert.Equal(0, inserted);
        Assert.Empty(repository.LastCandles);
    }

    [Fact]
    public async Task HandleAsync_WithInvalidCandle_Throws()
    {
        var repository = new FakeCandleRepository();
        var handler = new SaveCandlesCommandHandler(repository);
        var invalid = new CandleDto(Open, -1m, 1m, 1m, 1m, 1m, Open.AddHours(1), 1m, 1, 1m, 1m);

        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(Command(invalid), CancellationToken.None));
    }

    private sealed class FakeCandleRepository : ICandleRepository
    {
        public string? LastTableName { get; private set; }
        public List<Candle> LastCandles { get; private set; } = [];

        public Task<int> BulkInsert(string tableName, IEnumerable<Candle> candles, CancellationToken cancellationToken)
        {
            LastTableName = tableName;
            LastCandles = candles.ToList();
            return Task.FromResult(LastCandles.Count);
        }

        public Task<IEnumerable<Candle>> GetHistoryAsync(string tableName, DateTime? from, DateTime? to, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<Candle>>([]);

        public Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DateTimeOffset> GetLatestOpenDataTime(string tableName, CancellationToken cancellationToken)
            => Task.FromResult(DateTimeOffset.MinValue);
    }
}
