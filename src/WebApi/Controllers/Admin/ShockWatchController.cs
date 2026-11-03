using Application.Commands.AdminConfigurations;
using Application.Dtos.ShockWatch;
using Application.Interfaces;
using Application.Queries.AdminConfigurations;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers.Admin;

/// <summary>
/// The symbols the live watch follows (the one that tells the admin about a sudden price move). Apart from what the collector
/// collects, on purpose: a symbol can be collected for months and never watched. The watch looks at this list again every 30 seconds.
/// </summary>
[Route("api/admin/shock-watch")]
public class ShockWatchController(ICommandDispatcher dispatcher) : AdminControllerBase
{
    public record AddRequest(int SymbolListingId);

    public record EnabledRequest(bool Enabled);

    /// <summary>Every symbol on the list: why it is there, whether its watch is on, and whether its 1 minute candles are collected.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => Ok(await dispatcher.SendAsync<GetShockWatchQuery, IReadOnlyList<ShockWatchListItem>>(new GetShockWatchQuery(), cancellationToken));

    /// <summary>
    /// Puts a symbol on the list and switches its watch on. 400 when the listing is unknown or its 1 minute candles are not collected
    /// (the watch starts from them), 409 when the symbol is on the list already.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Add(AddRequest request, CancellationToken cancellationToken)
    {
        var id = await dispatcher.SendAsync<AddShockWatchCommand, int>(new AddShockWatchCommand(request.SymbolListingId), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, new { id });
    }

    /// <summary>
    /// Switches the watch of one symbol on or off. Off also works for a symbol that is on the list because it is enabled for trading:
    /// that is the admin's own decision and it is remembered. On is refused (400) while the 1 minute candles are not collected.
    /// </summary>
    [HttpPut("{id:int}/enabled")]
    public async Task<IActionResult> SetEnabled(int id, EnabledRequest request, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync<SetShockWatchEnabledCommand, bool>(new SetShockWatchEnabledCommand(id, request.Enabled), cancellationToken);
        return NoContent();
    }

    /// <summary>Takes a symbol off the list. Only the ones added by hand: one that is there because of trading is switched off instead (400).</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Remove(int id, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync<RemoveShockWatchCommand, bool>(new RemoveShockWatchCommand(id), cancellationToken);
        return NoContent();
    }
}
