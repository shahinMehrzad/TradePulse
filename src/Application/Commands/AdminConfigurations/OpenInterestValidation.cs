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

    /// <summary>Long/short ratio and taker volume share the rules of one flag, so both must be possible.</summary>
    public static void EnsureMarketStatisticsSupported(int exchangeId, int marketId, int timeFrameId)
    {
        foreach (var type in new[] { MarketDataTypeEnum.LongShortRatio, MarketDataTypeEnum.TakerVolume })
        {
            if (!MarketDataSupport.TryValidate((ExchangeEnum)exchangeId, (MarketTypeEnum)marketId, type, (TimeFrameEnum)timeFrameId, out var reason))
                throw new ConfigurationValidationException($"Market statistics cannot be collected: {reason}.");
        }
    }
}
