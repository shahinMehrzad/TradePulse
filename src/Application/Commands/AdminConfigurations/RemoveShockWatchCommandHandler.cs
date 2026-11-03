using Application.Interfaces;
using Application.Research.Watch;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;

namespace Application.Commands.AdminConfigurations;

public readonly record struct RemoveShockWatchCommand(int Id);

public sealed class RemoveShockWatchCommandHandler(IShockWatchConfigurationRepository repository, AuditTrail audit)
    : ICommandHandler<RemoveShockWatchCommand, bool>
{
    public async Task<bool> HandleAsync(RemoveShockWatchCommand request, CancellationToken cancellationToken)
    {
        if (request.Id == default)
            throw new ConfigurationValidationException("Id is required.");

        var existing = await repository.GetById(request.Id, cancellationToken)
            ?? throw new ConfigurationNotFoundException("Configuration does not exist.");

        // A row that is there because the symbol is enabled for trading would be put back by the trading sync: switching it off is the way.
        if (existing.Reason == WatchReasonEnum.Trading)
            throw new ConfigurationValidationException("This symbol is watched because it is enabled for trading. Switch its watch off instead of removing it.");

        await repository.Remove(existing.Id, cancellationToken);
        await audit.RecordAsync(AuditAction.Removed, AuditEntityTypes.ShockWatch, existing.Id,
            AuditDescriptions.Summary(existing), AuditDescriptions.Values(existing), cancellationToken);
        return true;
    }
}
