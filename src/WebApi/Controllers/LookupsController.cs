using Application.Dtos.Lookups;
using Application.Interfaces;
using Application.Queries.Lookups;
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
            => Ok(await dispatcher.SendAsync<GetExchangesQuery, IEnumerable<ExchangesResponseDto>>(new GetExchangesQuery(), cancellationToken));

        [HttpGet("markets")]
        public async Task<IActionResult> GetMarkets(CancellationToken cancellationToken)
            => Ok(await dispatcher.SendAsync<GetMarketsQuery, IEnumerable<MarketsResponseDto>>(new GetMarketsQuery(), cancellationToken));

        [HttpGet("symbols")]
        public async Task<IActionResult> GetSymbols([FromQuery]string? keyword, CancellationToken cancellationToken)
            => Ok(await dispatcher.SendAsync<GetSymbolsQuery, IEnumerable<SymbolsResponseDto>>(new GetSymbolsQuery(keyword), cancellationToken));

        [HttpGet("time-frames")]
        public async Task<IActionResult> GetTimeFrames([FromQuery] int minInterval, int? maxInterval, CancellationToken cancellationToken)
            => Ok(await dispatcher.SendAsync<GetTimeFramesQuery, IEnumerable<TimeFramesResponseDto>>(new GetTimeFramesQuery(minInterval, maxInterval ?? int.MaxValue), cancellationToken));
    }
}
