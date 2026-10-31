namespace WebApi.IntegrationTests;

/// <summary>
/// The configuration endpoints take the id of a symbol listing. The migration lists the fourteen symbols on Binance Futures with
/// the old enum value as the id, and the fixture lists them on Binance Spot right after (14 + the value), so a test can still say
/// "exchange, market, symbol" and get the id. A combination nobody listed gets an id that no listing has.
/// </summary>
internal static class ListingIds
{
    public const int SeededSymbols = 14;

    public static int Of(int exchangeId, int marketId, int symbolId) => (exchangeId, marketId) switch
    {
        (0, _) or (_, 0) => 0,
        (1, 2) => symbolId,
        (1, 1) => symbolId == 0 ? 0 : SeededSymbols + symbolId,
        _ => 9999,
    };
}
