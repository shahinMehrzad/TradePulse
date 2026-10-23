using Application.Dtos.SymbolDataConfigurations;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Extensions;
using Domain.Services;

namespace Application.Commands.AdminConfigurations;

internal static class SymbolDataValidation
{
    /// <summary>
    /// The row the request asks for, or a <see cref="ConfigurationValidationException"/> saying why it cannot be: ids missing,
    /// nothing chosen, data the exchange market does not have, or a funding rate without a usable start date.
    /// </summary>
    public static SymbolDataConfiguration ToValidConfiguration(SymbolDataConfigurationDto request)
    {
        if (request.ExchangeId == default || request.MarketId == default || request.SymbolId == default)
            throw new ConfigurationValidationException("Exchange, market and symbol are required.");

        if (!request.CollectFundingRate && !request.CollectLiquidation)
            throw new ConfigurationValidationException("Choose funding rate, liquidation or both. To collect neither, delete the configuration.");

        if (request.CollectFundingRate)
            EnsureSupported(request, MarketDataTypeEnum.FundingRate, "Funding rate");
        if (request.CollectLiquidation)
            EnsureSupported(request, MarketDataTypeEnum.Liquidation, "Liquidation");

        DateTime? fundingStartDate = null;
        if (request.CollectFundingRate)
        {
            // A date that was left out binds to null (or DateTime.MinValue when the client sent that).
            if (request.FundingStartDate is null || request.FundingStartDate == default(DateTime))
                throw new ConfigurationValidationException("FundingStartDate is required to collect the funding rate.");

            fundingStartDate = request.FundingStartDate.Value.AsUtc();
            if (fundingStartDate >= DateTime.UtcNow)
                throw new ConfigurationValidationException("FundingStartDate must be in the past.");
        }

        return new SymbolDataConfiguration
        {
            ExchangeId = request.ExchangeId,
            MarketId = request.MarketId,
            SymbolId = request.SymbolId,
            CollectFundingRate = request.CollectFundingRate,
            FundingStartDate = fundingStartDate,
            CollectLiquidation = request.CollectLiquidation,
        };
    }

    private static void EnsureSupported(SymbolDataConfigurationDto request, MarketDataTypeEnum dataType, string label)
    {
        if (!MarketDataSupport.TryValidate((ExchangeEnum)request.ExchangeId, (MarketTypeEnum)request.MarketId, dataType, timeFrame: null, out var reason))
            throw new ConfigurationValidationException($"{label} cannot be collected: {reason}.");
    }
}
