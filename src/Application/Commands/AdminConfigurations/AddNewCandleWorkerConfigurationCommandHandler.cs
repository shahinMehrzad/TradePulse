using Application.Dtos.CandleWorkerConfigurations;
using Application.Interfaces;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Extensions;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations;

public sealed class AddNewCandleWorkerConfigurationCommandHandler(ICandleWorkerConfigurationRepository workerConfigurationRepository)
    : ICommandHandler<BaseCandleWorkerConfigurationDto, int>
{
    public async Task<int> HandleAsync(BaseCandleWorkerConfigurationDto request, CancellationToken cancellationToken)
    {
        if (request.ExchangeId == default || request.MarketId == default || request.SymbolId == default || request.TimeFrameId == default)
            throw new ConfigurationValidationException("Exchange, market, symbol and time frame are required.");

        // A date that was left out binds to DateTime.MinValue.
        if (request.StartDate == default)
            throw new ConfigurationValidationException("StartDate is required.");

        var startDate = request.StartDate.AsUtc();
        if (startDate >= DateTime.UtcNow)
            throw new ConfigurationValidationException("StartDate must be in the past.");
               
        if (request.CollectOpenInterest)
            OpenInterestValidation.EnsureSupported(request.ExchangeId, request.MarketId, request.TimeFrameId);

        var existingConfiguration =await workerConfigurationRepository.GetConfiguration(request.ExchangeId, request.MarketId, request.SymbolId, request.TimeFrameId, cancellationToken);
        if (existingConfiguration is not null)
            throw new ConfigurationConflictException("Configuration already exists.");

        return await workerConfigurationRepository.AddNewCandleWorkerConfiguration(new CandleWorkerConfiguration
        {
            ExchangeId = request.ExchangeId,
            MarketId = request.MarketId,
            StartDate = startDate,
            TimeFrameId = request.TimeFrameId,
            SymbolId = request.SymbolId,
            CollectOpenInterest = request.CollectOpenInterest
        }, cancellationToken);
    }
}
