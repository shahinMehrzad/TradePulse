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
    IReadOnlyList<TimeFrameLookup> TimeFrames)
{
    public static Lookups Empty { get; } = new([], [], [], []);
}

/// <summary>One candle series the collector should fetch, as the API stores it (ids, not names).</summary>
public record CandleWorkerSeries(int Id, int ExchangeId, int MarketId, int SymbolId, int TimeFrameId, DateTime StartDate);

/// <summary>What the panel sends to add or change a series. <c>StartDate</c> is a UTC date.</summary>
public record SeriesRequest(int ExchangeId, int MarketId, int SymbolId, int TimeFrameId, DateTime StartDate);

/// <summary>Outcome of a write. <see cref="SessionRejected"/> means the API no longer accepts the signed-in session.</summary>
public record ApiResult(bool Success, IReadOnlyList<string> Errors, bool SessionRejected = false);
