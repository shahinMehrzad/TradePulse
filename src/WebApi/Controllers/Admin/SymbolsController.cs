using Application.Commands.Symbols;
using Application.Dtos.Symbols;
using Application.Interfaces;
using Application.Queries.Symbols;
using Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers.Admin;

/// <summary>
/// The symbols and where each is listed. The plain symbol list is the public <c>api/lookups/symbols</c>; this is what only an admin may change.
/// </summary>
public class SymbolsController(ICommandDispatcher dispatcher) : AdminControllerBase
{
    /// <summary>
    /// One page of the symbols by name, each with the exchange markets it is listed on. The keyword matches the name, the description
    /// or an exchange's name for it.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetSymbols([FromQuery] string? keyword, CancellationToken cancellationToken, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 10)
        => Ok(await dispatcher.SendAsync<GetSymbolsPageQuery, SymbolPage>(new GetSymbolsPageQuery(keyword, pageNumber, pageSize), cancellationToken));

    /// <summary>One symbol with the markets it is listed on and how many series and settings use each listing.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetSymbol(int id, CancellationToken cancellationToken)
        => Ok(await dispatcher.SendAsync<GetSymbolDetailQuery, SymbolDetail>(new GetSymbolDetailQuery(id), cancellationToken));

    /// <summary>Changes the description. The name is never changed.</summary>
    [HttpPut]
    public async Task<IActionResult> UpdateSymbol(UpdateSymbolDto payload, CancellationToken cancellationToken)
        => Ok(await dispatcher.SendAsync<UpdateSymbolDto, SymbolLookup>(payload, cancellationToken));

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
