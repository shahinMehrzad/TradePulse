namespace WebRazor.Api;

// Wire shapes of WebApi responses. Kept here on purpose: the panel must not reference the Application layer.
/// <summary>The answer of every sign-in step. <c>Stage</c> is Authenticated (the tokens are set), TwoFactorRequired or SetupRequired (a challenge token is).</summary>
public record LoginResponse(
    string Stage, string? AccessToken, DateTime? ExpiresAt, string? UserName, string? RefreshToken,
    string? ChallengeToken, bool MustChangePassword, bool NeedsTwoFactorSetup, string[]? RecoveryCodes, bool EmailCodeAvailable);

public record AccountInfo(string UserName, string Email, string[] Roles, bool TwoFactorEnabled, int RecoveryCodesLeft, bool EmailCodesEnabled, bool EmailAvailable);

public record TwoFactorEnrollment(string SharedKey, string AuthenticatorUri);

public record UserRow(Guid Id, string UserName, string Email, string[] Roles, bool TwoFactorEnabled, bool MustChangePassword, bool IsDisabled);

/// <summary>A one-time password the API generated; the panel shows it once.</summary>
public record OneTimePassword(Guid UserId, string UserName, string TemporaryPassword);

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
public record CandleWorkerSeries(int Id, int ExchangeId, int MarketId, int SymbolId, int TimeFrameId, DateTime StartDate, bool CollectOpenInterest, bool CollectMarketStatistics);

/// <summary>A series whose first stored candle is on a later day than its start date (both UTC): the symbol was listed later.</summary>
public record LateStart(int Id, DateTime StartDate, DateTime FirstCandle);

/// <summary>One page of the series list, with the totals the API reports for the (filtered) list.</summary>
public record SeriesPage(IReadOnlyList<CandleWorkerSeries> Items, int PageNumber, int PageSize, int TotalItems, int TotalPages)
{
    public bool HasNext => PageNumber < TotalPages;
}

/// <summary>The collector's own report as the API relays it. <c>State</c> is answered, notConfigured or unreachable; the rest is only there when it answered.</summary>
public record CollectorHealth(string State, string? Status, DateTime CheckedAt, IReadOnlyList<CollectorHealthCheck> Checks);

/// <summary>One component of the collector. <c>Status</c> is Healthy, Degraded or Unhealthy.</summary>
public record CollectorHealthCheck(string Name, string Status, string? Description, IReadOnlyDictionary<string, string> Data);

/// <summary>One line of the audit log: who changed which setting, how, and what was different. Times are UTC.</summary>
public record AuditEntry(long Id, DateTime OccurredAt, string UserName, string Action, string EntityType, int EntityId, string Summary, string? Details);

/// <summary>What the audit log page asks for; empty fields do not filter. <c>From</c> and <c>To</c> are UTC dates, <c>To</c> included.</summary>
public record AuditQuery(string? Action, string? EntityType, string? User, string? Search, DateTime? From, DateTime? To, bool NewestFirst, int PageNumber, int PageSize);

/// <summary>One page of the audit log, with the totals the API reports for the filtered log.</summary>
public record AuditPage(IReadOnlyList<AuditEntry> Items, int PageNumber, int PageSize, int TotalItems, int TotalPages)
{
    public bool HasNext => PageNumber < TotalPages;
}

/// <summary>What the panel sends to add or change a series. <c>StartDate</c> is a UTC date.</summary>
public record SeriesRequest(int ExchangeId, int MarketId, int SymbolId, int TimeFrameId, DateTime StartDate, bool CollectOpenInterest, bool CollectMarketStatistics);

/// <summary>Outcome of a write. <see cref="SessionRejected"/> means the API no longer accepts the signed-in session.</summary>
/// <param name="StepUpRequired">The API wants a fresh second-factor confirmation for this (not a lack of rights): see the StepUp page.</param>
public record ApiResult(bool Success, IReadOnlyList<string> Errors, bool SessionRejected = false, bool StepUpRequired = false);

public record ElevationGrant(string Token, DateTime ExpiresAt);

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

/// <summary>The dashboard's answer: one card per exchange market symbol (names are lowercase, as in the candle routes).</summary>
public record DashboardResponse(DateTime GeneratedAt, IReadOnlyList<DashboardCard> Items);

/// <summary><c>Timeframes</c> are the ones the collector gathers for the symbol, shortest first; <c>Current</c> is the default one.</summary>
public record DashboardCard(
    string Exchange, string Market, string Symbol,
    IReadOnlyList<string> Timeframes, string DefaultTimeframe,
    DashboardSnapshot Current);

/// <summary>Status is <c>ok</c>, <c>late</c> or <c>empty</c>; everything but the time frame and status is null when it is empty.</summary>
public record DashboardSnapshot(
    string Timeframe, string Status, int? LagSeconds, DateTime? LastOpenTime,
    decimal? LastClose, decimal? ChangePercent, decimal? High, decimal? Low, decimal? Volume,
    DateTime? WindowStart, DateTime? WindowEnd, IReadOnlyList<decimal>? Sparkline);

/// <summary>A card plus the data collected around the symbol (funding rate, open interest, liquidations).</summary>
public record DashboardDetail(
    string Exchange, string Market, string Symbol,
    IReadOnlyList<string> Timeframes, string DefaultTimeframe,
    DashboardSnapshot Current,
    IReadOnlyList<DashboardRelated> Related)
    : DashboardCard(Exchange, Market, Symbol, Timeframes, DefaultTimeframe, Current);

/// <summary><c>DataType</c> is <c>fundingrate</c>, <c>openinterest</c> or <c>liquidation</c>; status as in <see cref="DashboardSnapshot"/>.</summary>
public record DashboardRelated(string DataType, string Status, int? LagSeconds, DateTime? LastTime);

/// <summary>The part of a stored candle the chart draws. Times are UTC.</summary>
public record ChartCandle(DateTime OpenTime, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

/// <summary>One page of the candle range answer; the panel asks for a range that fits one page.</summary>
public record ChartCandlePage(IReadOnlyList<ChartCandle> Items);
