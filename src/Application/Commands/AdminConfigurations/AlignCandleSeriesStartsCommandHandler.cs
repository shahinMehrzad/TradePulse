using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations;

public readonly record struct AlignCandleSeriesStartsCommand(int? Id);

public sealed class AlignCandleSeriesStartsCommandHandler(
    LateStartFinder finder, ICandleWorkerConfigurationRepository repository, AuditTrail audit)
    : ICommandHandler<AlignCandleSeriesStartsCommand, int>
{
    public async Task<int> HandleAsync(AlignCandleSeriesStartsCommand request, CancellationToken cancellationToken)
    {
        var late = await finder.FindAsync(cancellationToken);
        var targets = request.Id is { } id ? late.Where(l => l.Id == id).ToList() : late.ToList();

        if (request.Id is { } wanted && targets.Count == 0)
        {
            if (await repository.GetConfigurationById(wanted, cancellationToken) is null)
                throw new ConfigurationNotFoundException("Configuration does not exist.");

            throw new ConfigurationValidationException("This series does not start later than its start date.");
        }

        var changed = 0;
        foreach (var target in targets)
        {
            var before = await repository.GetConfigurationById(target.Id, cancellationToken);
            if (before is null)
                continue;

            var updated = await repository.UpdateCandleWorkerConfiguration(new CandleWorkerConfiguration
            {
                Id = before.Id,
                ExchangeId = before.ExchangeId,
                MarketId = before.MarketId,
                SymbolId = before.SymbolId,
                TimeFrameId = before.TimeFrameId,
                StartDate = target.FirstCandle,
                CollectOpenInterest = before.CollectOpenInterest,
            }, cancellationToken);

            if (AuditDescriptions.Changes(before, updated) is { } changes)
                await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.CandleSeries, updated.Id, AuditDescriptions.Summary(updated), changes, cancellationToken);

            changed++;
        }

        return changed;
    }
}
