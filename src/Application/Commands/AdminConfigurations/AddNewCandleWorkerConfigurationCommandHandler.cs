using Application.Dtos.CandleWorkerConfigurations;
using Application.Interfaces;
using Domain.Entities;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations;

public sealed class AddNewCandleWorkerConfigurationCommandHandler(ICandleWorkerConfigurationRepository workerConfigurationRepository)
    : ICommandHandler<BaseCandleWorkerConfigurationDto, int>
{
    public async Task<int> HandleAsync(BaseCandleWorkerConfigurationDto request, CancellationToken cancellationToken)
    {
        if (request.ExchangeId == default || request.MarketId == default || request.SymbolId == default || request.TimeFrameId == default)
            throw new ArgumentException("Invalid configuration parameters.");
        
        var existingConfiguration = await workerConfigurationRepository.GetConfiguration(request.ExchangeId, request.MarketId, request.SymbolId, request.TimeFrameId, cancellationToken);
        if (existingConfiguration is not null)
            throw new InvalidOperationException("Configuration already exists.");

        return await workerConfigurationRepository.AddNewCandleWorkerConfiguration(new CandleWorkerConfiguration
        {
            ExchangeId = request.ExchangeId,
            MarketId = request.MarketId,
            StartDate = request.StartDate,
            TimeFrameId = request.TimeFrameId,
            SymbolId = request.SymbolId
        }, cancellationToken);
    }
}
