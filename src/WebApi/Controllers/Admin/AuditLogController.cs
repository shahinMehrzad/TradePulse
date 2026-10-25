using Application.Dtos.AuditLog;
using Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.OData;
using WebApi.OData;

namespace WebApi.Controllers.Admin;

[Route("api/admin/audit-log")]
public class AuditLogController : AdminControllerBase
{
    /// <summary>
    /// Who changed which collection setting. OData query options: <c>$filter</c>, <c>$orderby</c>, <c>$top</c>, <c>$skip</c>, <c>$count</c>.
    /// Without <c>$orderby</c> the oldest entry comes first; the panel asks for <c>occurredAt desc</c>. Answers <c>{ count, value }</c>.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(
        ODataQueryOptions<AuditLogListItem> options,
        [FromServices] IReadModel<AuditLogListItem> readModel,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await ODataQueryExecutor.ExecuteAsync(options, readModel, cancellationToken));
        }
        catch (ODataException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
