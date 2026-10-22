using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WebRazor.Api;

/// <summary>Typed client for the signed-in admin's calls. <see cref="ApiAuthHandler"/> supplies the token.</summary>
public sealed class TradePulseApiClient(HttpClient http)
{
    private const string SeriesRoot = "api/admin/configurations";

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

    /// <summary>The four public lookup lists, fetched together.</summary>
    public async Task<Lookups> GetLookupsAsync(CancellationToken cancellationToken)
    {
        var exchanges = http.GetFromJsonAsync<List<Lookup>>("api/lookups/exchanges", cancellationToken);
        var markets = http.GetFromJsonAsync<List<Lookup>>("api/lookups/markets", cancellationToken);
        var symbols = http.GetFromJsonAsync<List<SymbolLookup>>("api/lookups/symbols", cancellationToken);
        var timeFrames = http.GetFromJsonAsync<List<TimeFrameLookup>>("api/lookups/time-frames", cancellationToken);
        await Task.WhenAll(exchanges, markets, symbols, timeFrames);

        return new Lookups(exchanges.Result ?? [], markets.Result ?? [], symbols.Result ?? [], timeFrames.Result ?? []);
    }

    /// <param name="keyword">Matched by the API against the exchange, market, symbol and time frame names.</param>
    /// <returns>One page of the configured series, or null when the session is no longer accepted by the API.</returns>
    public async Task<SeriesPage?> GetSeriesAsync(int pageNumber, int pageSize, string? keyword, CancellationToken cancellationToken)
    {
        var url = $"{SeriesRoot}/candle-worker?pageNumber={pageNumber}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(keyword))
            url += $"&keyword={Uri.EscapeDataString(keyword.Trim())}";

        using var response = await http.GetAsync(url, cancellationToken);
        if (IsRejected(response))
            return null;

        response.EnsureSuccessStatusCode();
        var wire = await response.Content.ReadFromJsonAsync<SeriesPageWire>(cancellationToken);
        return new SeriesPage(wire?.Data ?? [], pageNumber, pageSize, wire?.TotalItemsCount ?? 0, wire?.TotalPages ?? 0);
    }

    /// <summary>The API's paged answer (<c>PaginationResponseDto</c>).</summary>
    private sealed record SeriesPageWire(int TotalItemsCount, int TotalPages, int ItemsPerPage, int CurrentPage, List<CandleWorkerSeries>? Data);

    public async Task<ApiResult> AddSeriesAsync(SeriesRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"{SeriesRoot}/add-config", request, cancellationToken);
        return await ToResultAsync(response, cancellationToken);
    }

    public async Task<ApiResult> UpdateSeriesAsync(int id, SeriesRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync(
            $"{SeriesRoot}/update-config",
            new { id, request.ExchangeId, request.MarketId, request.SymbolId, request.TimeFrameId, request.StartDate },
            cancellationToken);
        return await ToResultAsync(response, cancellationToken);
    }

    public async Task<ApiResult> DeleteSeriesAsync(int id, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync($"{SeriesRoot}/delete-config/{id}", cancellationToken);
        return await ToResultAsync(response, cancellationToken);
    }

    /// <summary>Success, or the reasons the API gave (a ProblemDetails <c>detail</c>, or the <c>errors</c> of a validation problem).</summary>
    private static async Task<ApiResult> ToResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return new ApiResult(true, []);
        if (IsRejected(response))
            return new ApiResult(false, [], SessionRejected: true);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ApiResult(false, ["That series does not exist (anymore). Reload the page."]);

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
