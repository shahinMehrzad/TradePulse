using Application.Dtos.CandleWorkerConfigurations;
using Application.Interfaces;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Extensions;
using Domain.Repositories;
using Application.Services;

namespace Application.Commands.AdminConfigurations;

public sealed class UpdateCandleWorkerConfigurationCommandHandler(ICandleWorkerConfigurationRepository workerConfigurationRepository, ISymbolCatalogRepository catalog, AuditTrail audit)
    : ICommandHandler<UpdateCandleWorkerConfigurationDto, CandleWorkerConfiguration>
{
    public async Task<CandleWorkerConfiguration> HandleAsync(UpdateCandleWorkerConfigurationDto request, CancellationToken cancellationToken)
    {
        if (request.Id == default)
            throw new ConfigurationValidationException("Id is required.");

        if (request.SymbolListingId == default || request.TimeFrameId == default)
            throw new ConfigurationValidationException("Symbol listing and time frame are required.");

        if (request.StartDate == default)
            throw new ConfigurationValidationException("StartDate is required.");

        var startDate = request.StartDate.AsUtc();
        if (startDate >= DateTime.UtcNow)
            throw new ConfigurationValidationException("StartDate must be in the past.");

        var listing = await catalog.GetListing(request.SymbolListingId, cancellationToken)
            ?? throw new ConfigurationValidationException("Unknown symbol listing.");

        if (request.CollectOpenInterest)
            OpenInterestValidation.EnsureSupported(listing.ExchangeId, listing.MarketId, request.TimeFrameId);

        if (request.CollectMarketStatistics)
            OpenInterestValidation.EnsureMarketStatisticsSupported(listing.ExchangeId, listing.MarketId, request.TimeFrameId);

        // A row that does not exist is reported by the update itself.
        var before = await workerConfigurationRepository.GetConfigurationById(request.Id, cancellationToken);

        var updated = await workerConfigurationRepository.UpdateCandleWorkerConfiguration(new CandleWorkerConfiguration
        {
            Id = request.Id,
            SymbolListingId = request.SymbolListingId,
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
