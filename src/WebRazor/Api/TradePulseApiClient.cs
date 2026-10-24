using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WebRazor.Api;

/// <summary>Typed client for the signed-in admin's calls. <see cref="ApiAuthHandler"/> supplies the token.</summary>
public sealed class TradePulseApiClient(HttpClient http)
{
    private const string SeriesRoot = "api/admin/configurations";
    private const string NotFoundSeries = "That series does not exist (anymore). Reload the page.";
    private const string NotFoundSymbolData = "That setting does not exist (anymore). Reload the page.";

    private static bool IsRejected(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    /// <returns>The admin the token belongs to, or null when the session is no longer accepted by the API.</returns>
    public async Task<AdminInfo?> GetMeAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("api/admin/me", cancellationToken);
        if (IsRejected(response))
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AdminInfo>(cancellationToken);
    }

    /// <param name="market">"spot" or "futures"; null for all markets.</param>
    /// <returns>The dashboard cards, or null when the session is no longer accepted by the API.</returns>
    public async Task<DashboardResponse?> GetDashboardSeriesAsync(string? market, CancellationToken cancellationToken)
    {
        var url = "api/admin/dashboard/series";
        if (!string.IsNullOrWhiteSpace(market))
            url += $"?market={Uri.EscapeDataString(market)}";

        using var response = await http.GetAsync(url, cancellationToken);
        if (IsRejected(response))
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DashboardResponse>(cancellationToken);
    }

    /// <param name="timeframe">e.g. "4h"; the shortest collected one when null.</param>
    /// <param name="candles">How many of the newest candles the figures cover; the API's default (what the dashboard cards use) when null.</param>
    /// <returns>
    /// The card with its related data. <c>Detail</c> is null when the API does not collect that series or time frame (404);
    /// <c>SessionRejected</c> when it no longer accepts the session.
    /// </returns>
    public async Task<(DashboardDetail? Detail, bool SessionRejected)> GetDashboardDetailAsync(
        string exchange, string market, string symbol, string? timeframe, int? candles, CancellationToken cancellationToken)
    {
        var url = $"api/admin/dashboard/series/{Uri.EscapeDataString(exchange)}/{Uri.EscapeDataString(market)}/{Uri.EscapeDataString(symbol)}";
        var query = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(timeframe))
            query.Add($"timeframe={Uri.EscapeDataString(timeframe)}");
        if (candles is { } count)
            query.Add($"candles={count}");
        if (query.Count > 0)
            url += "?" + string.Join('&', query);

        using var response = await http.GetAsync(url, cancellationToken);
        if (IsRejected(response))
            return (null, true);
        // 400 too: a hand-edited address with a name the API does not know is "no such series" to the panel.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            return (null, false);

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DashboardDetail>(cancellationToken), false);
    }

    /// <summary>The stored candles with <c>from &lt;= open time &lt;= to</c>, oldest first (at most one page of 500); empty when the collector has stored none yet.</summary>
    public async Task<IReadOnlyList<ChartCandle>> GetCandlesAsync(
        string exchange, string market, string symbol, string timeframe, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var url = $"api/candles/{Uri.EscapeDataString(exchange)}/{Uri.EscapeDataString(market)}/{Uri.EscapeDataString(symbol)}/{Uri.EscapeDataString(timeframe)}"
            + $"?from={Uri.EscapeDataString(Iso(from))}&to={Uri.EscapeDataString(Iso(to))}&limit=500";

        using var response = await http.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChartCandlePage>(cancellationToken))?.Items ?? [];
    }

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The public lookup lists and the market data rules, fetched together.</summary>
    public async Task<Lookups> GetLookupsAsync(CancellationToken cancellationToken)
    {
        var exchanges = http.GetFromJsonAsync<List<Lookup>>("api/lookups/exchanges", cancellationToken);
        var markets = http.GetFromJsonAsync<List<Lookup>>("api/lookups/markets", cancellationToken);
        var symbols = http.GetFromJsonAsync<List<SymbolLookup>>("api/lookups/symbols", cancellationToken);
        var timeFrames = http.GetFromJsonAsync<List<TimeFrameLookup>>("api/lookups/time-frames", cancellationToken);
        var support = http.GetFromJsonAsync<List<MarketDataSupportEntry>>("api/lookups/market-data-support", cancellationToken);
        await Task.WhenAll(exchanges, markets, symbols, timeFrames, support);

        return new Lookups(exchanges.Result ?? [], markets.Result ?? [], symbols.Result ?? [], timeFrames.Result ?? [], support.Result ?? []);
    }

    /// <param name="keyword">Matched by the API against the exchange, market, symbol and time frame names.</param>
    /// <returns>One page of the configured series, or null when the session is no longer accepted by the API.</returns>
    public async Task<SeriesPage?> GetSeriesAsync(int pageNumber, int pageSize, string? keyword, CancellationToken cancellationToken)
    {
        // OData: the keyword becomes a $filter over the names, the list is sorted like the panel always showed it, and
        // $count comes back as "count" (the number of matches before paging).
        var url = $"{SeriesRoot}/candle-worker/odata?$orderby=symbol,intervalSeconds,id&$top={pageSize}&$skip={(pageNumber - 1) * pageSize}";
        if (!string.IsNullOrWhiteSpace(keyword))
            url += $"&$filter={Uri.EscapeDataString(KeywordFilter(keyword))}";

        using var response = await http.GetAsync(url, cancellationToken);
        if (IsRejected(response))
            return null;

        response.EnsureSuccessStatusCode();
        var wire = await response.Content.ReadFromJsonAsync<SeriesPageWire>(cancellationToken);
        var total = wire?.Count ?? 0;
        return new SeriesPage(wire?.Value ?? [], pageNumber, pageSize, total, (int)Math.Ceiling(total / (double)pageSize));
    }

    /// <summary>Case-insensitive match of the keyword against the exchange, market, symbol and symbol description.</summary>
    internal static string KeywordFilter(string keyword) => ContainsAny(keyword, "exchange", "market", "symbol", "symbolDescription");

    /// <summary>A text in an OData filter: a quote inside it is written twice.</summary>
    private static string Quote(string text) => $"'{text.Replace("'", "''")}'";

    private static string ContainsAny(string text, params string[] fields)
    {
        var term = Quote(text.Trim().ToLowerInvariant());
        return "(" + string.Join(" or ", fields.Select(field => $"contains(tolower({field}),{term})")) + ")";
    }

    /// <returns>What the collector says about itself, or null when the session is no longer accepted by the API.</returns>
    public async Task<CollectorHealth?> GetCollectorHealthAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("api/admin/collector/health", cancellationToken);
        if (IsRejected(response))
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CollectorHealth>(cancellationToken);
    }

    /// <returns>One page of the audit log, or null when the session is no longer accepted by the API.</returns>
    public async Task<AuditPage?> GetAuditLogAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        var filters = AuditFilters(query);
        var url = $"api/admin/audit-log?$orderby=occurredAt {(query.NewestFirst ? "desc" : "asc")},id {(query.NewestFirst ? "desc" : "asc")}"
            + $"&$top={query.PageSize}&$skip={(query.PageNumber - 1) * query.PageSize}";
        if (filters.Count > 0)
            url += $"&$filter={Uri.EscapeDataString(string.Join(" and ", filters))}";

        using var response = await http.GetAsync(url, cancellationToken);
        if (IsRejected(response))
            return null;

        response.EnsureSuccessStatusCode();
        var wire = await response.Content.ReadFromJsonAsync<AuditPageWire>(cancellationToken);
        var total = wire?.Count ?? 0;
        return new AuditPage(wire?.Value ?? [], query.PageNumber, query.PageSize, total, (int)Math.Ceiling(total / (double)query.PageSize));
    }

    /// <summary>The parts of the filter the query asks for. Action and entity are matched exactly, the rest by contents or range.</summary>
    internal static List<string> AuditFilters(AuditQuery query)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Action))
            filters.Add($"action eq {Quote(query.Action.Trim())}");
        if (!string.IsNullOrWhiteSpace(query.EntityType))
            filters.Add($"entityType eq {Quote(query.EntityType.Trim())}");
        if (!string.IsNullOrWhiteSpace(query.User))
            filters.Add(ContainsAny(query.User, "userName"));
        if (!string.IsNullOrWhiteSpace(query.Search))
            filters.Add(ContainsAny(query.Search, "summary", "details"));
        if (query.From is { } from)
            filters.Add($"occurredAt ge {Iso(from.Date)}");
        if (query.To is { } to)
            filters.Add($"occurredAt lt {Iso(to.Date.AddDays(1))}");   // the whole last day
        return filters;
    }

    /// <summary>The API's OData answer: one page of log lines and the number of lines that matched.</summary>
    private sealed record AuditPageWire(int Count, List<AuditEntry>? Value);

    /// <summary>The API's OData answer: one page of rows and the number of rows that matched.</summary>
    private sealed record SeriesPageWire(int Count, List<CandleWorkerSeries>? Value);

    public async Task<ApiResult> AddSeriesAsync(SeriesRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"{SeriesRoot}/add-config", request, cancellationToken);
        return await ToResultAsync(response, NotFoundSeries, cancellationToken);
    }

    public async Task<ApiResult> UpdateSeriesAsync(int id, SeriesRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync(
            $"{SeriesRoot}/update-config",
            new { id, request.ExchangeId, request.MarketId, request.SymbolId, request.TimeFrameId, request.StartDate, request.CollectOpenInterest },
            cancellationToken);
        return await ToResultAsync(response, NotFoundSeries, cancellationToken);
    }

    public async Task<ApiResult> DeleteSeriesAsync(int id, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync($"{SeriesRoot}/delete-config/{id}", cancellationToken);
        return await ToResultAsync(response, NotFoundSeries, cancellationToken);
    }

    /// <returns>The funding rate and liquidation settings of every symbol, or null when the session is no longer accepted by the API.</returns>
    public async Task<IReadOnlyList<SymbolDataSetting>?> GetSymbolDataAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"{SeriesRoot}/symbol-data", cancellationToken);
        if (IsRejected(response))
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<SymbolDataSetting>>(cancellationToken) ?? [];
    }

    public async Task<ApiResult> AddSymbolDataAsync(SymbolDataRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"{SeriesRoot}/add-symbol-data", request, cancellationToken);
        return await ToResultAsync(response, NotFoundSymbolData, cancellationToken);
    }

    public async Task<ApiResult> UpdateSymbolDataAsync(int id, SymbolDataRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync(
            $"{SeriesRoot}/update-symbol-data",
            new { id, request.ExchangeId, request.MarketId, request.SymbolId, request.CollectFundingRate, request.FundingStartDate, request.CollectLiquidation },
            cancellationToken);
        return await ToResultAsync(response, NotFoundSymbolData, cancellationToken);
    }

    public async Task<ApiResult> DeleteSymbolDataAsync(int id, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync($"{SeriesRoot}/delete-symbol-data/{id}", cancellationToken);
        return await ToResultAsync(response, NotFoundSymbolData, cancellationToken);
    }

    /// <summary>Success, or the reasons the API gave (a ProblemDetails <c>detail</c>, or the <c>errors</c> of a validation problem).</summary>
    private static async Task<ApiResult> ToResultAsync(HttpResponseMessage response, string notFound, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return new ApiResult(true, []);
        if (IsRejected(response))
            return new ApiResult(false, [], SessionRejected: true);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ApiResult(false, [notFound]);

        var reasons = await ReadReasonsAsync(response, cancellationToken);
        return new ApiResult(false, reasons.Count > 0 ? reasons : [$"The API answered {(int)response.StatusCode}."]);
    }

    private static async Task<List<string>> ReadReasonsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var reasons = new List<string>();
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in errors.EnumerateObject())
                    foreach (var message in field.Value.EnumerateArray())
                        if (message.GetString() is { Length: > 0 } text)
                            reasons.Add(text);
            }

            if (reasons.Count == 0 && root.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } reason)
                reasons.Add(reason);
        }
        catch (JsonException)
        {
            // Not JSON (a proxy's error page, say): the caller falls back to the status code.
        }

        return reasons;
    }
}
