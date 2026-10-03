using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Repositories;

namespace Application.Commands;

public class SaveCandlesCommandHandler(ICandleRepository candleRepository) : ICommandHandler<SaveCandlesCommand, int>
{
    private readonly ICandleRepository _candleRepository = candleRepository;

    public async Task<int> HandleAsync(SaveCandlesCommand command, CancellationToken cancellationToken)
    {
        var tableName = TableNameRegistry.GetCandleTableName(
            command.Exchange,
            command.MarketType,
            command.Symbol,
            command.Timeframe
        );

        var candles = command.Candles.Select(dto => new Candle(
            openTime: dto.OpenTime,
            openPrice: dto.OpenPrice,
            highPrice: dto.HighPrice,
            lowPrice: dto.LowPrice,
            closePrice: dto.ClosePrice,
            volume: dto.Volume,
            closeTime: dto.CloseTime,
            quoteAssetVolume: dto.QuoteAssetVolume,
            numberOfTrades: dto.NumberOfTrades,
            takerBuyBaseVolume: dto.TakerBuyBaseVolume,
            takerBuyQuoteVolume: dto.TakerBuyQuoteVolume
        ));

        return await _candleRepository.BulkInsert(tableName, candles, cancellationToken);
    }
}