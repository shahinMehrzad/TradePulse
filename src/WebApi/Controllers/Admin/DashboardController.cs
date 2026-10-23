using Application.Dtos.Dashboard;
using Application.Interfaces;
using Application.Queries.Dashboard;
using Domain.Enums;
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
    }
}
