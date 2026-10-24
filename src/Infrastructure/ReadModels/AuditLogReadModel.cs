using Application.Dtos.AuditLog;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadModels;

public sealed class AuditLogReadModel(ConfigDbContext context) : EfReadModel<AuditLogListItem>
{
    public override IQueryable<AuditLogListItem> Query() =>
        context.AuditLog.AsNoTracking().Select(e => new AuditLogListItem
        {
            Id = e.Id,
            OccurredAt = e.OccurredAt,
            UserName = e.UserName,
            Action = e.Action.ToString(),
            EntityType = e.EntityType,
            EntityId = e.EntityId,
            Summary = e.Summary,
            Details = e.Details,
        });
}
