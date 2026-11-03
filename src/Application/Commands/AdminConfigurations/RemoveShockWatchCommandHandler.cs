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

        // A row of a traded symbol only says "the admin switched this watch off". Taking it away is switching the watch on, which is
        // what the admin means, so it is done that way (and the symbol goes back to being watched on its own).
        if (existing.Reason == WatchReasonEnum.Trading)
            throw new ConfigurationValidationException("This symbol is on the list because it is enabled for trading. Switch its watch on to return to the automatic watch, or off to keep it silent.");

        await repository.Remove(existing.Id, cancellationToken);
        await audit.RecordAsync(AuditAction.Removed, AuditEntityTypes.ShockWatch, existing.Id,
            AuditDescriptions.Summary(existing), AuditDescriptions.Values(existing), cancellationToken);
        return true;
    }
}
