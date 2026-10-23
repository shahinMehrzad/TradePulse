using Domain.Enums;
using Domain.Exceptions;
using Domain.Services;

namespace Application.Commands.AdminConfigurations;

internal static class OpenInterestValidation
{
    public static void EnsureSupported(int exchangeId, int marketId, int timeFrameId)
    {
        if (!MarketDataSupport.TryValidate(
                (ExchangeEnum)exchangeId, (MarketTypeEnum)marketId, MarketDataTypeEnum.OpenInterest, (TimeFrameEnum)timeFrameId, out var reason))
            throw new ConfigurationValidationException($"Open interest cannot be collected: {reason}.");
    }
}
