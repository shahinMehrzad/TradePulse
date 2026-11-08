using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// One line of the log of a signal: it came, it was refused, its trade opened or ended, its message was sent, edited or could not be.
/// Kept by signal id, with no foreign key: the log may speak of a signal that was never stored. The detail never holds a secret or an
/// exception's message.
/// </summary>
public class SignalEvent
{
    public int Id { get; set; }
    public string SignalId { get; set; } = string.Empty;

    /// <summary>UTC. Stored as <c>timestamp</c> (no zone) like every other time column.</summary>
    public DateTime At { get; set; }

    public SignalEventKindEnum Kind { get; set; }
    public string? Detail { get; set; }
}
