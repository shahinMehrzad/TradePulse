namespace Domain.Entities;

public enum AuditAction
{
    Added,
    Updated,
    Removed,
}

/// <summary>The kinds of thing the audit log talks about. Stored as text, so a value here is part of what the admin filters on.</summary>
public static class AuditEntityTypes
{
    public const string CandleSeries = "CandleSeries";
    public const string SymbolData = "SymbolData";

    /// <summary>A panel account. Users have a Guid, not an int id, so these entries carry <c>EntityId</c> 0 and name the user in the summary.</summary>
    public const string User = "User";
}

/// <summary>Who changed which collection setting, and how. Written by the admin API only; never edited.</summary>
public class AuditLogEntry
{
    public long Id { get; set; }

    /// <summary>UTC. Stored as <c>timestamp</c> (no zone) like every other time column.</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>The user name from the access token, or <c>system</c> when there was no signed-in user.</summary>
    public required string UserName { get; set; }

    public AuditAction Action { get; set; }

    /// <summary>One of <see cref="AuditEntityTypes"/>.</summary>
    public required string EntityType { get; set; }

    /// <summary>The id the row had. After a removal the row is gone; the id and <see cref="Summary"/> are what is left of it.</summary>
    public int EntityId { get; set; }

    /// <summary>Says which row it was, e.g. <c>binance / futures / btcusdt / 4h</c>.</summary>
    public required string Summary { get; set; }

    /// <summary>What changed (<c>start date 2020-01-01 -> 2021-01-01</c>) or the values of a new row; null when there is nothing to add.</summary>
    public string? Details { get; set; }
}
