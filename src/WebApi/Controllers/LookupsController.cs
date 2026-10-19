using Application.Interfaces;
using Application.Queries.Lookups;
using Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [AllowAnonymous]
    public class LookupsController(ICommandDispatcher dispatcher) : BaseController
    {
        [HttpGet("exchanges")]
        public async Task<IActionResult> GetExhanges(CancellationToken cancellationToken)
            => Ok(await dispatcher.SendAsync<GetExchangesQuery, IEnumerable<ExchangeLookup>>(new GetExchangesQuery(), cancellationToken));

        [HttpGet("markets")]
        public async Task<IActionResult> GetMarkets(CancellationToken cancellationToken)
            => Ok(await dispatcher.SendAsync<GetMarketsQuery, IEnumerable<MarketLookup>>(new GetMarketsQuery(), cancellationToken));

        [HttpGet("symbols")]
        public async Task<IActionResult> GetSymbols([FromQuery]string? name, CancellationToken cancellationToken)
            => Ok(await dispatcher.SendAsync<GetSymbolsQuery, IEnumerable<SymbolLookup>>(new GetSymbolsQuery(name), cancellationToken));

        [HttpGet("time-frames")]
        public async Task<IActionResult> GetTimeFrames([FromQuery] int minInterval, int? maxInterval, CancellationToken cancellationToken)
            => Ok(await dispatcher.SendAsync<GetTimeFramesQuery, IEnumerable<TimeFrameLookup>>(new GetTimeFramesQuery(minInterval, maxInterval ?? int.MaxValue), cancellationToken));
    }
}
