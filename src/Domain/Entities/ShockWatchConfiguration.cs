namespace Domain.Entities;

/// <summary>
/// A symbol listing (a symbol on an exchange market) the admin wants the live watch to follow, so that a sudden price move is told
/// to them. A row means "watched"; removing it stops the watch. One row per listing.
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

    public DateTime CreateDate { get; set; }
}
