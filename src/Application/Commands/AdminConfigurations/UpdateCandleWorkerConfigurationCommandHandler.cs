using Application.Dtos.CandleWorkerConfigurations;
using Application.Interfaces;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Extensions;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations
{
    public sealed class UpdateCandleWorkerConfigurationCommandHandler(ICandleWorkerConfigurationRepository workerConfigurationRepository)
        : ICommandHandler<UpdateCandleWorkerConfigurationDto, CandleWorkerConfiguration>
    {
        public async Task<CandleWorkerConfiguration> HandleAsync(UpdateCandleWorkerConfigurationDto request, CancellationToken cancellationToken)
        {
            if (request.ExchangeId == default || request.MarketId == default || request.SymbolId == default || request.TimeFrameId == default)
                throw new ConfigurationValidationException("Exchange, market, symbol and time frame are required.");

            if (request.StartDate == default)
                throw new ConfigurationValidationException("StartDate is required.");

            var startDate = request.StartDate.AsUtc();
            if (startDate >= DateTime.UtcNow)
                throw new ConfigurationValidationException("StartDate must be in the past.");

            return await workerConfigurationRepository.UpdateCandleWorkerConfiguration(new CandleWorkerConfiguration
            {
                ExchangeId = request.ExchangeId,
                Id = request.Id,
                MarketId = request.MarketId,
                SymbolId = request.SymbolId,
                TimeFrameId = request.TimeFrameId
            }, cancellationToken);
        }
    }
}
