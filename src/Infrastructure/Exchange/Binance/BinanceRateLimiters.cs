using Domain.Enums;
using Infrastructure.Configurations;
using Microsoft.Extensions.Options;

namespace Infrastructure.Exchange.Binance;

/// <summary>
/// One weight limiter per market, shared by every worker. Binance counts request weight per IP, so candle,
/// funding rate and open interest collectors must draw from the same budget.
/// </summary>
public sealed class BinanceRateLimiters(IOptions<BinanceExchangeConfigurations> options)
{
    private readonly BinanceWeightLimiter _spot = new(options.Value.MaxLimit);
    private readonly BinanceWeightLimiter _futures = new(options.Value.MaxLimit);

    public BinanceWeightLimiter For(MarketTypeEnum market) => market switch
    {
        MarketTypeEnum.Spot => _spot,
        MarketTypeEnum.Futures => _futures,
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, "No rate limiter for this market type."),
    };
}
