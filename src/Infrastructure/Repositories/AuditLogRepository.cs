using Domain.Entities;
using Domain.Repositories;
using Infrastructure.Persistence;

namespace Infrastructure.Repositories;

public class AuditLogRepository(ConfigDbContext context) : IAuditLogRepository
{
    public async Task Add(AuditLogEntry entry, CancellationToken cancellationToken)
    {
        // OccurredAt is UTC. timestamp columns hold no zone and Npgsql rejects Kind=Utc for them.
        entry.OccurredAt = DateTime.SpecifyKind(entry.OccurredAt, DateTimeKind.Unspecified);

        context.AuditLog.Add(entry);
        await context.SaveChangesAsync(cancellationToken);
    }
}
