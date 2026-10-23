using Application.Dtos.Dashboard;
using Application.Interfaces;
using Application.Queries.Dashboard;
using Domain.Enums;
using Domain.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers.Admin
{
    public class DashboardController(ICommandDispatcher dispatcher) : AdminControllerBase
    {
        [HttpGet("series")]
        public async Task<IActionResult> GetSeries([FromQuery] string? market, CancellationToken cancellationToken)
        {
            MarketTypeEnum? parsedMarket = null;
            if (!string.IsNullOrWhiteSpace(market))
            {
                if (!Enum.TryParse<MarketTypeEnum>(market, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || char.IsDigit(market[0]))
                {
                    ModelState.AddModelError(nameof(market), $"'{market}' is not a known market. Known: {string.Join(", ", Enum.GetNames<MarketTypeEnum>().Select(n => n.ToLowerInvariant()))}.");
                    return ValidationProblem(ModelState);
                }

                parsedMarket = parsed;
            }

            return Ok(await dispatcher.SendAsync<GetDashboardSeriesQuery, DashboardSeriesResponse>(new GetDashboardSeriesQuery(parsedMarket), cancellationToken));
        }

        [HttpGet("series/{exchange}/{market}/{symbol}")]
        public async Task<IActionResult> GetSeriesDetail(string exchange, string market, string symbol, [FromQuery] string? timeframe, [FromQuery] int? candles, CancellationToken cancellationToken)
        {
            if (candles is < 2 or > DashboardSnapshotBuilder.MaxPoints)
                ModelState.AddModelError(nameof(candles), $"candles must be between 2 and {DashboardSnapshotBuilder.MaxPoints}.");

            TryParseName(nameof(exchange), exchange, out ExchangeEnum parsedExchange);
            TryParseName(nameof(market), market, out MarketTypeEnum parsedMarket);
            TryParseName(nameof(symbol), symbol, out SymbolEnum parsedSymbol);

            TimeFrameEnum? parsedTimeframe = null;
            if (!string.IsNullOrWhiteSpace(timeframe) && TryParseName(nameof(timeframe), timeframe, out TimeFrameEnum frame))
                parsedTimeframe = frame;

            if (!ModelState.IsValid)
                return ValidationProblem(ModelState);

            return Ok(await dispatcher.SendAsync<GetDashboardSeriesDetailQuery, DashboardSeriesDetail>(
                new GetDashboardSeriesDetailQuery(parsedExchange, parsedMarket, parsedSymbol, parsedTimeframe, candles), cancellationToken));
        }

        private bool TryParseName<T>(string parameter, string value, out T result) where T : struct, Enum
        {
            if (EnumExtensions.TryParseRouteName(value, out result))
                return true;

            ModelState.AddModelError(parameter, $"'{value}' is not a known {parameter}. Known: {string.Join(", ", Enum.GetValues<T>().Select(v => v.ToRouteName()))}.");
            return false;
        }
    }
}
