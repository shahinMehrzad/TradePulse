using Application.Dtos.Candles;
using Application.Interfaces;
using Domain.Entities;
using Domain.Repositories;
using Domain.ValueObjects;

namespace Application.Queries.Candles;

public sealed class CandleReader(ICandleRepository repository) : ICandleReader
{
    public const int MaxLimit = 1000;

    public async Task<CandlePage> GetRangeAsync(CandleSeries series, DateTime? from, DateTime? to, int limit, CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        if (from > to)
            throw new InvalidCandleQueryException("query", "'from' must not be later than 'to'.");

        // One extra row tells whether another page exists and where it starts, without a COUNT query.
        var rows = await repository.GetPageAsync(series.TableName, from, to, limit + 1, cancellationToken);

        var hasMore = rows.Count > limit;
        var items = rows.Take(limit).Select(ToResponse).ToList();

        return new CandlePage(items, hasMore ? rows[limit].TimeRange.OpenTime : null);
    }

    public async Task<IReadOnlyList<CandleResponse>> GetLatestAsync(CandleSeries series, int limit, DateTime? upTo, CancellationToken cancellationToken)
    {
        ValidateLimit(limit);

        var rows = await repository.GetLatestAsync(series.TableName, limit, upTo, cancellationToken);

        return rows.Select(ToResponse).ToList();
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > MaxLimit)
            throw new InvalidCandleQueryException(nameof(limit), $"limit must be between 1 and {MaxLimit}.");
    }

    private static CandleResponse ToResponse(Candle c) => new(
        c.TimeRange.OpenTime, c.Open, c.High, c.Low, c.Close, c.Volume,
        c.TimeRange.CloseTime, c.QuoteAssetVolume, c.NumberOfTrades, c.TakerBuyBaseVolume, c.TakerBuyQuoteVolume);
}
