using Domain.Entities;

namespace Domain.Repositories;

public interface IAuditLogRepository
{
    Task Add(AuditLogEntry entry, CancellationToken cancellationToken);
}
