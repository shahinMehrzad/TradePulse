namespace Application.Dtos.AuditLog;

/// <summary>One row of the admin's audit log. <see cref="Action"/> is the name of an <c>AuditAction</c> (Added, Updated, Removed).</summary>
public sealed class AuditLogListItem
{
    public long Id { get; set; }

    /// <summary>UTC.</summary>
    public DateTime OccurredAt { get; set; }
    public string UserName { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public int EntityId { get; set; }
    public string Summary { get; set; } = "";
    public string? Details { get; set; }
}
