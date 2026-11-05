namespace Application.Dtos.Dashboard;

/// <summary>
/// The newest long/short account ratio and taker buy/sell volume of one series, oldest first, for the dashboard's charts.
/// <see cref="Collected"/> is false when the series does not collect them (nothing to show, and nothing is wrong).
/// </summary>
public sealed record MarketStatisticsResponse(
    string Exchange, string Market, string Symbol, string Timeframe, bool Collected,
    IReadOnlyList<LongShortPoint> LongShort, IReadOnlyList<TakerVolumePoint> TakerVolume);

/// <summary>Accounts holding long and short positions: <c>Ratio</c> is long over short, the two accounts are shares that add up to 1.</summary>
public sealed record LongShortPoint(DateTime Time, decimal Ratio, decimal LongAccount, decimal ShortAccount);

/// <summary>Volume bought and sold by takers (market orders) in one period; <c>BuySellRatio</c> is buy over sell.</summary>
public sealed record TakerVolumePoint(DateTime Time, decimal BuySellRatio, decimal BuyVolume, decimal SellVolume);
