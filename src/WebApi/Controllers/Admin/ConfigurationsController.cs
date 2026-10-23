using Application.Commands.AdminConfigurations;
using Application.Dtos;
using Application.Dtos.CandleWorkerConfigurations;
using Application.Dtos.SymbolDataConfigurations;
using Application.Interfaces;
using Application.Queries.AdminConfigurations;
using Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers.Admin
{
    public class ConfigurationsController(ICommandDispatcher dispatcher) : AdminControllerBase
    {
        [HttpGet("candle-worker")]
        public async Task<IActionResult> GetCandleWorkerConfigurations([FromQuery] GetPaginationCandleWorkerConfigurationsQuery payload, CancellationToken cancellationToken)
            => Ok(await dispatcher.SendAsync<GetPaginationCandleWorkerConfigurationsQuery, PaginationResponseDto<CandleWorkerConfiguration>>
                (payload, cancellationToken));

        [HttpPost("add-config")]
        public async Task<IActionResult> AddCandleWorkerConfigurations(BaseCandleWorkerConfigurationDto payload, CancellationToken cancellationToken)
        {
            var id = await dispatcher.SendAsync<BaseCandleWorkerConfigurationDto, int>(payload, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, new { id });
        }

        [HttpPut("update-config")]
        public async Task<IActionResult> UpdateCandleWorkerConfigurations(UpdateCandleWorkerConfigurationDto payload, CancellationToken cancellationToken)
            => Ok(await dispatcher.SendAsync<UpdateCandleWorkerConfigurationDto, CandleWorkerConfiguration>(payload, cancellationToken));


        [HttpDelete("delete-config/{id:int}")]
        public async Task<IActionResult> DeleteCandleWorkerConfigurations(int id, CancellationToken cancellationToken)
        {
            await dispatcher.SendAsync<RemoveCandleWorkerConfigurationCommand, bool>(new RemoveCandleWorkerConfigurationCommand(id), cancellationToken);
            return NoContent();
        }
                
        [HttpGet("symbol-data")]
        public async Task<IActionResult> GetSymbolDataConfigurations(CancellationToken cancellationToken)
            => Ok(await dispatcher.SendAsync<GetSymbolDataConfigurationsQuery, IReadOnlyList<SymbolDataConfiguration>>
                (new GetSymbolDataConfigurationsQuery(), cancellationToken));

        [HttpPost("add-symbol-data")]
        public async Task<IActionResult> AddSymbolDataConfiguration(SymbolDataConfigurationDto payload, CancellationToken cancellationToken)
        {
            var id = await dispatcher.SendAsync<SymbolDataConfigurationDto, int>(payload, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, new { id });
        }

        [HttpPut("update-symbol-data")]
        public async Task<IActionResult> UpdateSymbolDataConfiguration(UpdateSymbolDataConfigurationDto payload, CancellationToken cancellationToken)
            => Ok(await dispatcher.SendAsync<UpdateSymbolDataConfigurationDto, SymbolDataConfiguration>(payload, cancellationToken));

        [HttpDelete("delete-symbol-data/{id:int}")]
        public async Task<IActionResult> DeleteSymbolDataConfiguration(int id, CancellationToken cancellationToken)
        {
            await dispatcher.SendAsync<RemoveSymbolDataConfigurationCommand, bool>(new RemoveSymbolDataConfigurationCommand(id), cancellationToken);
            return NoContent();
        }
    }
}

