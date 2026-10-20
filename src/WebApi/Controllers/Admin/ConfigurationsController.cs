using Application.Dtos.CandleWorkerConfigurations;
using Application.Interfaces;
using Application.Queries.AdminConfigurations;
using Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers.Admin
{
    public class ConfigurationsController(ICommandDispatcher dispatcher) : AdminControllerBase
    {
        [HttpGet("candle-worker")]
        public async Task<IActionResult> GetCandleWorkerConfigurations(CancellationToken cancellationToken)
            => Ok(await dispatcher.SendAsync<GetCandleWorkerConfigurationsQuery, IEnumerable<CandleWorkerConfiguration>>(new GetCandleWorkerConfigurationsQuery(), cancellationToken));

        [HttpPost("add-config")]
        public async Task<IActionResult> AddCandleWorkerConfigurations(BaseCandleWorkerConfigurationDto payload, CancellationToken cancellationToken)
        {
            var id = await dispatcher.SendAsync<BaseCandleWorkerConfigurationDto, int>(payload, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, new { id });
        }
    }
}

