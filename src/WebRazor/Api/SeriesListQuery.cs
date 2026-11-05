namespace WebRazor.Api;

/// <summary>On, off or either: a filter on a yes/no column.</summary>
public enum FlagFilter { Any, On, Off }

/// <summary>
/// What the admin asked of the series list (search, filters, sort, page, page size), and how that becomes an OData query for the API and a
/// query string for the links of the page. Everything that comes from the address is cleaned here (an unknown sort, a page size the API
/// would refuse, a negative page), so a hand-edited address cannot make the page ask the API for something it rejects. Kept free of
/// anything from ASP.NET: the same file is compiled into the API's integration tests, which run it against the real endpoint.
/// </summary>
public sealed record SeriesListQuery
{
    public const int DefaultPageSize = 10;

    /// <summary>The page sizes offered. The API refuses more than 100 rows at a time.</summary>
    public static readonly IReadOnlyList<int> PageSizes = [10, 25, 50, 100];

    /// <summary>The columns that can be sorted, by the name used in the address, and the field of the OData list they sort by.</summary>
    public static readonly IReadOnlyDictionary<string, string> SortFields = new Dictionary<string, string>
    {
        ["exchange"] = "exchange",
        ["market"] = "market",
        ["symbol"] = "symbol",
        ["timeframe"] = "intervalSeconds",   // by length of time, not by the name ("15m" would come before "1h" otherwise)
        ["from"] = "startDate",
        ["oi"] = "collectOpenInterest",
        ["stats"] = "collectMarketStatistics",
    };

    /// <summary>How the list was always sorted, and what breaks a tie so that pages never overlap or lose a row.</summary>
    private static readonly string[] TieBreakers = ["symbol", "intervalSeconds", "id"];

    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>Matched against the names of the exchange, market and symbol.</summary>
    public string? Keyword { get; init; }

    /// <summary>A key of <see cref="SortFields"/>; null is the usual order (symbol, then time frame).</summary>
    public string? SortBy { get; init; }

    public bool Descending { get; init; }
    public int? ExchangeId { get; init; }
    public int? MarketId { get; init; }
    public int? TimeFrameId { get; init; }
    public FlagFilter OpenInterest { get; init; }
    public FlagFilter Statistics { get; init; }

    /// <summary>Builds the query from what the address held, throwing nothing away that is valid and nothing in that the API would not take.</summary>
    public static SeriesListQuery From(
        int? pageNumber, int? pageSize, string? keyword, string? sort, string? direction,
        int? exchangeId, int? marketId, int? timeFrameId, string? openInterest, string? statistics)
    {
        var sortBy = sort is not null && SortFields.ContainsKey(sort.Trim().ToLowerInvariant()) ? sort.Trim().ToLowerInvariant() : null;
        return new SeriesListQuery
        {
            PageNumber = Math.Max(1, pageNumber ?? 1),
            PageSize = pageSize is { } size && PageSizes.Contains(size) ? size : DefaultPageSize,
            Keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim(),
            SortBy = sortBy,
            Descending = sortBy is not null && string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase),
            ExchangeId = exchangeId is > 0 ? exchangeId : null,
            MarketId = marketId is > 0 ? marketId : null,
            TimeFrameId = timeFrameId is > 0 ? timeFrameId : null,
            OpenInterest = ParseFlag(openInterest),
            Statistics = ParseFlag(statistics),
        };
    }

    public static FlagFilter ParseFlag(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "on" => FlagFilter.On,
        "off" => FlagFilter.Off,
        _ => FlagFilter.Any,
    };

    /// <summary>True when something narrows the list (a search or a filter), as opposed to sort and paging.</summary>
    public bool IsFiltered => Keyword is not null || ExchangeId is not null || MarketId is not null || TimeFrameId is not null
        || OpenInterest != FlagFilter.Any || Statistics != FlagFilter.Any;

    /// <summary>The same list, one page earlier or later (never before the first).</summary>
    public SeriesListQuery OnPage(int pageNumber) => this with { PageNumber = Math.Max(1, pageNumber) };

    /// <summary>
    /// What a click on a column header asks for: that column ascending, or descending when it is already the one sorted ascending. The
    /// first page again, because the rows of the old page are somewhere else now.
    /// </summary>
    public SeriesListQuery SortedBy(string key) => this with
    {
        SortBy = key,
        Descending = SortBy == key && !Descending,
        PageNumber = 1,
    };

    /// <summary>The query options for <c>api/admin/configurations/candle-worker/odata</c>, without a leading <c>?</c>.</summary>
    public string ToODataQuery()
    {
        var order = new List<string>();
        if (SortBy is not null)
            order.Add(SortFields[SortBy] + (Descending ? " desc" : ""));
        foreach (var field in TieBreakers)
            if (!order.Any(o => o.Split(' ')[0] == field))
                order.Add(field);

        var query = $"$orderby={Uri.EscapeDataString(string.Join(",", order))}&$top={PageSize}&$skip={(PageNumber - 1) * PageSize}";

        var filter = Filter();
        return filter is null ? query : $"{query}&$filter={Uri.EscapeDataString(filter)}";
    }

    /// <summary>The <c>$filter</c> for the search and the filters, or null when nothing narrows the list.</summary>
    public string? Filter()
    {
        var parts = new List<string>();
        if (Keyword is not null)
            parts.Add(KeywordFilter(Keyword));
        if (ExchangeId is { } exchange)
            parts.Add($"exchangeId eq {exchange}");
        if (MarketId is { } market)
            parts.Add($"marketId eq {market}");
        if (TimeFrameId is { } timeFrame)
            parts.Add($"timeFrameId eq {timeFrame}");
        if (OpenInterest != FlagFilter.Any)
            parts.Add($"collectOpenInterest eq {(OpenInterest == FlagFilter.On ? "true" : "false")}");
        if (Statistics != FlagFilter.Any)
            parts.Add($"collectMarketStatistics eq {(Statistics == FlagFilter.On ? "true" : "false")}");

        return parts.Count == 0 ? null : string.Join(" and ", parts);
    }

    /// <summary>Case-insensitive match of the keyword against the exchange, market, symbol and symbol description.</summary>
    public static string KeywordFilter(string keyword) => ODataText.ContainsAny(keyword, "exchange", "market", "symbol", "symbolDescription");

    /// <summary>
    /// The address values of this query, for the links and forms of the page to carry it along (only what differs from the defaults, so an
    /// untouched list has a clean address). The names are the ones the page binds from the address.
    /// </summary>
    public Dictionary<string, string> RouteValues()
    {
        var values = new Dictionary<string, string>();
        if (PageNumber > 1) values["pageNumber"] = PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (PageSize != DefaultPageSize) values["size"] = PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Keyword is not null) values["keyword"] = Keyword;
        if (SortBy is not null)
        {
            values["sort"] = SortBy;
            if (Descending) values["dir"] = "desc";
        }

        if (ExchangeId is { } exchange) values["exchange"] = exchange.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (MarketId is { } market) values["market"] = market.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (TimeFrameId is { } timeFrame) values["timeframe"] = timeFrame.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (OpenInterest != FlagFilter.Any) values["oi"] = OpenInterest.ToString().ToLowerInvariant();
        if (Statistics != FlagFilter.Any) values["stats"] = Statistics.ToString().ToLowerInvariant();
        return values;
    }
}
