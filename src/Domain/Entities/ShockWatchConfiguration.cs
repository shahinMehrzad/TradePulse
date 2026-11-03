using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// A symbol listing (a symbol on an exchange market) on the list of the live watch, which tells the admin about a sudden price move.
/// One row per listing. It is watched when <see cref="IsEnabled"/>; a row that is not enabled stays to remember that the admin
/// turned the watch off on purpose, so the symbol being enabled for trading again does not switch it back on behind their back.
/// <para>
/// This is not collecting: a symbol can be collected for months without ever being watched, so that the admin is not told about
/// symbols they do not trade. Watching needs the symbol's 1 minute candles to be collected (the watch starts from them), which the
/// application checks when a row is added.
/// </para>
/// </summary>
public class ShockWatchConfiguration
{
    public int Id { get; set; }
    public int SymbolListingId { get; set; }
    public SymbolListing? SymbolListing { get; set; }

    public WatchReasonEnum Reason { get; set; }

    public bool IsEnabled { get; set; }

    public DateTime CreateDate { get; set; }
}
