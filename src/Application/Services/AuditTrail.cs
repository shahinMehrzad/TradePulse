using Application.Interfaces;
using Domain.Entities;
using Domain.Repositories;
using Microsoft.Extensions.Logging;

namespace Application.Services;

/// <summary>
/// Writes the audit log entry of a change the admin made. It runs after the change is saved, and a failure to write it is logged
/// instead of thrown: the setting really did change, and telling the admin it failed would make them repeat it.
/// </summary>
public sealed class AuditTrail(IAuditLogRepository repository, ICurrentUser user, TimeProvider timeProvider, ILogger<AuditTrail> logger)
{
    public async Task RecordAsync(AuditAction action, string entityType, int entityId, string summary, string? details, CancellationToken cancellationToken)
    {
        try
        {
            await repository.Add(new AuditLogEntry
            {
                OccurredAt = timeProvider.GetUtcNow().UtcDateTime,
                UserName = user.Name,
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                Summary = summary,
                Details = details,
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not write the audit log entry for {Action} {EntityType} {EntityId}.", action, entityType, entityId);
        }
    }
}
