using Application.Dtos.Collector;
using Application.Interfaces;
using Application.Queries.Collector;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers.Admin;

[Route("api/admin/collector")]
public class CollectorController(ICommandDispatcher dispatcher) : AdminControllerBase
{    
    [HttpGet("health")]
    public async Task<IActionResult> GetHealth(CancellationToken cancellationToken)
        => Ok(await dispatcher.SendAsync<GetCollectorHealthQuery, CollectorHealthResponse>(new GetCollectorHealthQuery(), cancellationToken));
}
