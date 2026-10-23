namespace WebRazor.Api;

// Wire shapes of WebApi responses. Kept here on purpose: the panel must not reference the Application layer.
public record LoginResponse(string AccessToken, DateTime ExpiresAt, string UserName, string RefreshToken);

public record AdminInfo(string UserName, string Email, string[] Roles);

public record TokenSet(string AccessToken, string RefreshToken, DateTime ExpiresAt);

// The public lookup lists (they mirror the enums; ids are the enum values).
public record Lookup(int Id, string? Name);

public record SymbolLookup(int Id, string? Name, string? Description);

public record TimeFrameLookup(int Id, string? Name, int IntervalToSeconds, string? FriendlyName);

public record Lookups(
    IReadOnlyList<Lookup> Exchanges,
    IReadOnlyList<Lookup> Markets,
    IReadOnlyList<SymbolLookup> Symbols,
    IReadOnlyList<TimeFrameLookup> TimeFrames,
    IReadOnlyList<MarketDataSupportEntry> MarketDataSupport)
{
    public static Lookups Empty { get; } = new([], [], [], [], []);

    /// <summary>
    /// Whether the exchange market provides <paramref name="dataType"/> (for <paramref name="timeFrameId"/>, when the data has
    /// one). Mirrors the rule the API enforces; the API still has the last word.
    /// </summary>
    public bool Supports(int exchangeId, int marketId, string dataType, int? timeFrameId = null) =>
        MarketDataSupport.Any(s => s.ExchangeId == exchangeId && s.MarketId == marketId && s.DataType == dataType
            && (s.TimeFrameIds is null || (timeFrameId is { } id && s.TimeFrameIds.Contains(id))));
}

/// <summary>One candle series the collector should fetch, as the API stores it (ids, not names).</summary>
public record CandleWorkerSeries(int Id, int ExchangeId, int MarketId, int SymbolId, int TimeFrameId, DateTime StartDate, bool CollectOpenInterest);

/// <summary>One page of the series list, with the totals the API reports for the (filtered) list.</summary>
public record SeriesPage(IReadOnlyList<CandleWorkerSeries> Items, int PageNumber, int PageSize, int TotalItems, int TotalPages)
{
    public bool HasNext => PageNumber < TotalPages;
}

/// <summary>What the panel sends to add or change a series. <c>StartDate</c> is a UTC date.</summary>
public record SeriesRequest(int ExchangeId, int MarketId, int SymbolId, int TimeFrameId, DateTime StartDate, bool CollectOpenInterest);

/// <summary>Outcome of a write. <see cref="SessionRejected"/> means the API no longer accepts the signed-in session.</summary>
public record ApiResult(bool Success, IReadOnlyList<string> Errors, bool SessionRejected = false);

/// <summary>
/// One kind of data an exchange market provides (the API publishes it from the same rule that refuses the impossible choices).
/// <c>TimeFrameIds</c> is null for data without a time frame.
/// </summary>
public record MarketDataSupportEntry(int ExchangeId, int MarketId, string DataType, IReadOnlyList<int>? TimeFrameIds);

/// <summary>Funding rate and liquidations of one symbol, as the API stores them (ids, not names).</summary>
public record SymbolDataSetting(
    int Id, int ExchangeId, int MarketId, int SymbolId, bool CollectFundingRate, DateTime? FundingStartDate, bool CollectLiquidation);

/// <summary>What the panel sends to add or change a symbol's funding rate and liquidations. <c>FundingStartDate</c> is a UTC date.</summary>
public record SymbolDataRequest(
    int ExchangeId, int MarketId, int SymbolId, bool CollectFundingRate, DateTime? FundingStartDate, bool CollectLiquidation);
