using Application.Commands.Symbols;
using Application.Dtos.Symbols;
using Application.Interfaces;
using Application.Queries.Symbols;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers.Admin;

/// <summary>
/// The symbols and where each is listed. The plain symbol list is the public <c>api/lookups/symbols</c>; this is what only an admin may change.
/// </summary>
public class SymbolsController(ICommandDispatcher dispatcher) : AdminControllerBase
{
    [HttpPost]
    public async Task<IActionResult> AddSymbol(AddSymbolDto payload, CancellationToken cancellationToken)
    {
        var id = await dispatcher.SendAsync<AddSymbolDto, int>(payload, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, new { id });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> RemoveSymbol(int id, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync<RemoveSymbolCommand, bool>(new RemoveSymbolCommand(id), cancellationToken);
        return NoContent();
    }

    [HttpGet("listings")]
    public async Task<IActionResult> GetListings(CancellationToken cancellationToken)
        => Ok(await dispatcher.SendAsync<GetSymbolListingsQuery, IReadOnlyList<SymbolListingListItem>>(new GetSymbolListingsQuery(), cancellationToken));

    /// <summary>Lists a symbol on an exchange market. The exchange is asked whether it trades the name, so this can answer 503 when it cannot be reached.</summary>
    [HttpPost("listings")]
    public async Task<IActionResult> AddListing(AddSymbolListingDto payload, CancellationToken cancellationToken)
    {
        var id = await dispatcher.SendAsync<AddSymbolListingDto, int>(payload, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, new { id });
    }

    [HttpDelete("listings/{id:int}")]
    public async Task<IActionResult> RemoveListing(int id, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync<RemoveSymbolListingCommand, bool>(new RemoveSymbolListingCommand(id), cancellationToken);
        return NoContent();
    }
}
