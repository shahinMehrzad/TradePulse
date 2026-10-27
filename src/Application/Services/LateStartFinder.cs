using Application.Dtos.CandleWorkerConfigurations;
using Domain.Repositories;
using Domain.ValueObjects;

namespace Application.Services;

public sealed class LateStartFinder(ICandleWorkerConfigurationRepository configurations, ICandleRepository candles)
{
    public async Task<IReadOnlyList<LateStartSeries>> FindAsync(CancellationToken cancellationToken)
    {
        var found = new List<LateStartSeries>();

        foreach (var row in await configurations.GetConfigurationList(cancellationToken))
        {
            if (!CandleSeriesPlan.TryCreate(row, out var plan, out _))
                continue;

            var first = await candles.GetFirstOpenTimeAsync(plan.Series.TableName, cancellationToken);
            if (first is { } firstCandle && firstCandle.UtcDateTime.Date > plan.StartDate.Date)
                found.Add(new LateStartSeries(row.Id, plan.StartDate, firstCandle.UtcDateTime));
        }

        return found;
    }
}
