using Application.Commands.AdminConfigurations;
using Application.Dtos.ShockWatch;
using Application.Interfaces;
using Application.Queries.AdminConfigurations;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers.Admin;

/// <summary>
/// The symbols the live watch follows (the one that tells the admin about a sudden price move). Two things put a symbol on it: the admin
/// adds it by hand to study it, or it is enabled for trading (in the trading service), and then it is watched on its own. Apart from what
/// the collector collects, on purpose: a symbol can be collected for months and never watched. The watch looks at this list every 30 seconds.
/// </summary>
[Route("api/admin/shock-watch")]
public class ShockWatchController(ICommandDispatcher dispatcher) : AdminControllerBase
{
    public record AddRequest(int SymbolListingId);

    public record EnabledRequest(bool Enabled);

    /// <summary>
    /// Every symbol the watch follows or has been told not to: why it is there (<c>Manual</c>, or <c>Trading</c>), whether its watch is on,
    /// whether it is enabled for trading now (<c>isTradedNow</c>) and whether its 1 minute candles are collected. A symbol that is
    /// watched only because it is traded has <c>id</c> 0: there is no row to change, use <c>traded/{symbolListingId}/off</c>.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => Ok(await dispatcher.SendAsync<GetShockWatchQuery, IReadOnlyList<ShockWatchListItem>>(new GetShockWatchQuery(), cancellationToken));

    /// <summary>
    /// Puts a symbol on the list by the admin's own choice (to study it, with or without trading it), switched on. 400 when the listing is
    /// unknown or its 1 minute candles are not collected (the watch starts from them), 409 when the symbol is on the list already.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Add(AddRequest request, CancellationToken cancellationToken)
    {
        var id = await dispatcher.SendAsync<AddShockWatchCommand, int>(new AddShockWatchCommand(request.SymbolListingId), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, new { id });
    }

    /// <summary>
    /// Switches the watch of one row on or off. Off always works. On is refused (400) for a row added by hand while its 1 minute candles
    /// are not collected. On for the row of a traded symbol removes the row: the symbol is watched on its own again.
    /// </summary>
    [HttpPut("{id:int}/enabled")]
    public async Task<IActionResult> SetEnabled(int id, EnabledRequest request, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync<SetShockWatchEnabledCommand, bool>(new SetShockWatchEnabledCommand(id, request.Enabled), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Turns off the watch of a symbol that is enabled for trading, on the admin's own responsibility. It is remembered as a row, so the
    /// symbol stays unwatched until the admin switches it on again (<c>PUT {id}/enabled</c> with the row's id). Answers <c>{ id }</c> of
    /// that row. 400 when the symbol is not enabled for trading (its watch is not automatic; add it by hand instead).
    /// </summary>
    [HttpPost("traded/{symbolListingId:int}/off")]
    public async Task<IActionResult> SwitchOffTraded(int symbolListingId, CancellationToken cancellationToken)
    {
        var id = await dispatcher.SendAsync<SwitchOffTradedWatchCommand, int>(new SwitchOffTradedWatchCommand(symbolListingId), cancellationToken);
        return Ok(new { id });
    }

    /// <summary>Takes a symbol that was added by hand off the list. The row of a traded symbol is switched on instead (400), which returns it to the automatic watch.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Remove(int id, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync<RemoveShockWatchCommand, bool>(new RemoveShockWatchCommand(id), cancellationToken);
        return NoContent();
    }
}
