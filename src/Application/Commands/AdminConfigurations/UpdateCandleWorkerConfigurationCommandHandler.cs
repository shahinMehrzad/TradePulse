using Application.Dtos.CandleWorkerConfigurations;
using Application.Interfaces;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Extensions;
using Domain.Repositories;
using Application.Services;

namespace Application.Commands.AdminConfigurations;

public sealed class UpdateCandleWorkerConfigurationCommandHandler(ICandleWorkerConfigurationRepository workerConfigurationRepository, AuditTrail audit)
    : ICommandHandler<UpdateCandleWorkerConfigurationDto, CandleWorkerConfiguration>
{
    public async Task<CandleWorkerConfiguration> HandleAsync(UpdateCandleWorkerConfigurationDto request, CancellationToken cancellationToken)
    {
        if (request.Id == default)
            throw new ConfigurationValidationException("Id is required.");

        if (request.ExchangeId == default || request.MarketId == default || request.SymbolId == default || request.TimeFrameId == default)
            throw new ConfigurationValidationException("Exchange, market, symbol and time frame are required.");

        if (request.StartDate == default)
            throw new ConfigurationValidationException("StartDate is required.");

        var startDate = request.StartDate.AsUtc();
        if (startDate >= DateTime.UtcNow)
            throw new ConfigurationValidationException("StartDate must be in the past.");

        if (request.CollectOpenInterest)
            OpenInterestValidation.EnsureSupported(request.ExchangeId, request.MarketId, request.TimeFrameId);

        if (request.CollectMarketStatistics)
            OpenInterestValidation.EnsureMarketStatisticsSupported(request.ExchangeId, request.MarketId, request.TimeFrameId);

        // A row that does not exist is reported by the update itself.
        var before = await workerConfigurationRepository.GetConfigurationById(request.Id, cancellationToken);

        var updated = await workerConfigurationRepository.UpdateCandleWorkerConfiguration(new CandleWorkerConfiguration
        {
            ExchangeId = request.ExchangeId,
            Id = request.Id,
            MarketId = request.MarketId,
            SymbolId = request.SymbolId,
            TimeFrameId = request.TimeFrameId,
            StartDate = startDate,
            CollectOpenInterest = request.CollectOpenInterest,
            CollectMarketStatistics = request.CollectMarketStatistics
        }, cancellationToken);

        if (before is not null && AuditDescriptions.Changes(before, updated) is { } changes)
            await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.CandleSeries, updated.Id, AuditDescriptions.Summary(updated), changes, cancellationToken);

        return updated;
    }
}
