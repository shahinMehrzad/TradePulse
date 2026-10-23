using Application.Interfaces;
using Application.Queries.AdminConfigurations;
using Domain.Entities;
using Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackgroundWorker.CandleSeries;

/// <summary>Reads the series through the same query handler the admin API lists them with.</summary>
public sealed class DatabaseCandleSeriesSource(IServiceScopeFactory scopeFactory, ILogger<DatabaseCandleSeriesSource> logger) : ICandleSeriesSource
{
    public async Task<IReadOnlyList<CandleSeriesPlan>> LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var rows = await dispatcher.SendAsync<GetCandleWorkerConfigurationsQuery, IReadOnlyList<CandleWorkerConfiguration>>(
            new GetCandleWorkerConfigurationsQuery(), cancellationToken);

        return CandleSeriesMapper.ToSeries(rows, (row, reason) =>
            logger.LogWarning("Skipping candle series #{Id} (exchange {Exchange}, market {Market}, symbol {Symbol}, time frame {TimeFrame}): {Reason}",
                row.Id, row.ExchangeId, row.MarketId, row.SymbolId, row.TimeFrameId, reason));
    }
}
