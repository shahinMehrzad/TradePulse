using Application.Interfaces;
using Domain.Enums;
using Domain.Repositories;
using Domain.Services;

namespace Application.Research;

public class DetectMarketEventsCommandHandler(ICandleRepository candleRepository, IMarketEventStore store)
    : ICommandHandler<DetectMarketEventsCommand, DetectMarketEventsResult>
{
    private static readonly MarketEventDetectorOptions Options = new();

    public async Task<DetectMarketEventsResult> HandleAsync(DetectMarketEventsCommand command, CancellationToken cancellationToken)
    {
        var tableName = TableNameRegistry.GetCandleTableName(command.Exchange, command.MarketType, command.Symbol.Value, TimeFrameEnum._1M);

        // The candles before From only warm up the baseline of the first minutes; the ones after To let the last events be followed.
        var readFrom = command.From.AddMinutes(-Options.Window);
        var readTo = command.To.AddMinutes(Options.Horizons.Max());

        var bars = (await candleRepository.GetHistoryAsync(tableName, readFrom, readTo, cancellationToken))
            .Select(c => new MinuteBar(c.TimeRange.OpenTime, c.High, c.Low, c.Close, c.Volume))
            .ToList();

        var events = new MarketEventDetector(Options).Detect(bars, command.SymbolListingId, command.From)
            .Where(e => e.StartedAt <= command.To)
            .ToList();

        var calculator = new EventReactionCalculator(Options);
        foreach (var marketEvent in events)
            marketEvent.Reactions.AddRange(calculator.Calculate(marketEvent, bars));

        var added = await store.AddNewAsync(events, cancellationToken);
        return new DetectMarketEventsResult(bars.Count, events.Count, added);
    }
}
