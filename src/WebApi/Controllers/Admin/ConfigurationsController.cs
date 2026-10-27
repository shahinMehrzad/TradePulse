using Application.Commands.AdminConfigurations;
using Application.Dtos;
using Application.Dtos.CandleWorkerConfigurations;
using Application.Dtos.SymbolDataConfigurations;
using Application.Interfaces;
using Application.Queries.AdminConfigurations;
using Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.OData;
using WebApi.OData;

namespace WebApi.Controllers.Admin
{
    public class ConfigurationsController(ICommandDispatcher dispatcher) : AdminControllerBase
    {
        /// <summary>
        /// The candle series list with OData query options: <c>$filter</c>, <c>$orderby</c>, <c>$top</c>, <c>$skip</c>, <c>$count</c>.
        /// Answers <c>{ count, value }</c>, where count is the number of rows matching the filter before paging.
        /// </summary>
        [HttpGet("candle-worker/odata")]
        public async Task<IActionResult> QueryCandleWorkerConfigurations(
            ODataQueryOptions<CandleWorkerConfigurationListItem> options,
            [FromServices] IReadModel<CandleWorkerConfigurationListItem> readModel,
            CancellationToken cancellationToken)
        {
            try
            {
                return Ok(await ODataQueryExecutor.ExecuteAsync(options, readModel, cancellationToken));
            }
            catch (ODataException ex)
            {
                // The parser's messages describe the query, not the server.
                return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }

        [HttpGet("candle-worker/late-starts")]
        public async Task<IActionResult> GetLateStarts(CancellationToken cancellationToken)
            => Ok(await dispatcher.SendAsync<GetLateStartSeriesQuery, IReadOnlyList<LateStartSeries>>(new GetLateStartSeriesQuery(), cancellationToken));

        [HttpPost("candle-worker/align-starts")]
        public async Task<IActionResult> AlignStarts(CancellationToken cancellationToken)
            => Ok(new { aligned = await dispatcher.SendAsync<AlignCandleSeriesStartsCommand, int>(new AlignCandleSeriesStartsCommand(null), cancellationToken) });

        [HttpPost("candle-worker/{id:int}/align-start")]
        public async Task<IActionResult> AlignStart(int id, CancellationToken cancellationToken)
            => Ok(new { aligned = await dispatcher.SendAsync<AlignCandleSeriesStartsCommand, int>(new AlignCandleSeriesStartsCommand(id), cancellationToken) });

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

