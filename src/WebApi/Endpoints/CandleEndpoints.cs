using System.Globalization;
using Application.Dtos.Candles;
using Application.Interfaces;
using Application.Queries.Candles;
using Domain.Enums;
using Domain.Repositories;
using Microsoft.AspNetCore.Http.HttpResults;

namespace WebApi.Endpoints;

/// <summary>
/// Read-only access to the stored closed candles. The route picks the series by name
/// (<c>/api/candles/binance/futures/btcusdt/4h</c>); the table name is derived from the parsed enums only.
/// </summary>
public static class CandleEndpoints
{
    private const int DefaultRangeLimit = 500;
    private const int DefaultLatestLimit = 100;

    public static IEndpointRouteBuilder MapCandleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/candles/{exchange}/{market}/{symbol}/{timeframe}")
            .WithTags("Candles")
            .RequireAuthorization(); // any signed-in user; tokens come from POST /api/auth/login

        group.MapGet("/", GetRange)
            .WithSummary("Candles in a time range, oldest first, paged. Follow nextFrom for the next page.");
        group.MapGet("/latest", GetLatest)
            .WithSummary("The newest N candles up to an optional time, oldest first (indicator lookback, e.g. Ichimoku).");

        return app;
    }

    private static async Task<Results<Ok<CandlePage>, ValidationProblem, NotFound>> GetRange(
        string exchange, string market, string symbol, string timeframe,
        string? from, string? to, int? limit, ICandleReader reader, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var series = ParseSeries(exchange, market, symbol, timeframe, errors);
        var fromTime = ParseTime(nameof(from), from, errors);
        var toTime = ParseTime(nameof(to), to, errors);
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        try
        {
            return TypedResults.Ok(await reader.GetRangeAsync(series!.Value, fromTime, toTime, limit ?? DefaultRangeLimit, cancellationToken));
        }
        catch (CandleSeriesNotFoundException)
        {
            return TypedResults.NotFound();
        }
        catch (InvalidCandleQueryException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [ex.Parameter] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<IReadOnlyList<CandleResponse>>, ValidationProblem, NotFound>> GetLatest(
        string exchange, string market, string symbol, string timeframe,
        string? to, int? limit, ICandleReader reader, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var series = ParseSeries(exchange, market, symbol, timeframe, errors);
        var toTime = ParseTime(nameof(to), to, errors);
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        try
        {
            return TypedResults.Ok(await reader.GetLatestAsync(series!.Value, limit ?? DefaultLatestLimit, toTime, cancellationToken));
        }
        catch (CandleSeriesNotFoundException)
        {
            return TypedResults.NotFound();
        }
        catch (InvalidCandleQueryException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [ex.Parameter] = [ex.Message] });
        }
    }

    private static CandleSeries? ParseSeries(string exchange, string market, string symbol, string timeframe, Dictionary<string, string[]> errors)
    {
        var parsedExchange = ParseEnum<ExchangeEnum>(nameof(exchange), exchange, errors);
        var parsedMarket = ParseEnum<MarketTypeEnum>(nameof(market), market, errors);
        var parsedSymbol = ParseEnum<SymbolEnum>(nameof(symbol), symbol, errors);
        // Timeframe members are named "_4H", "_1D", ... so the URL can say "4h".
        var parsedTimeframe = ParseEnum<TimeFrameEnum>(nameof(timeframe), "_" + timeframe, errors, display: timeframe);

        return errors.Count > 0
            ? null
            : new CandleSeries(parsedExchange!.Value, parsedMarket!.Value, parsedSymbol!.Value, parsedTimeframe!.Value);
    }

    private static T? ParseEnum<T>(string name, string value, Dictionary<string, string[]> errors, string? display = null) where T : struct, Enum
    {
        // Enum.TryParse also accepts numbers ("2"); only names are part of the API.
        if (Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !char.IsDigit(value[0]))
            return parsed;

        errors[name] = [$"'{display ?? value}' is not a known {name}. Known: {string.Join(", ", Enum.GetNames<T>().Select(n => n.TrimStart('_').ToLowerInvariant()))}."];
        return null;
    }

    private static DateTime? ParseTime(string name, string? value, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // Offset-less input is taken as UTC, never as the server's local time.
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed;

        errors[name] = [$"'{value}' is not a valid date/time. Use ISO 8601, e.g. 2026-01-31T00:00:00Z."];
        return null;
    }
}
