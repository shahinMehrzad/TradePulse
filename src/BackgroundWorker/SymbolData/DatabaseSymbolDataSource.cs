using Application.Interfaces;
using Application.Queries.AdminConfigurations;
using Domain.Entities;
using Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackgroundWorker.SymbolData;

/// <summary>Reads the choices through the same query handler the admin API lists them with.</summary>
public sealed class DatabaseSymbolDataSource(IServiceScopeFactory scopeFactory, ILogger<DatabaseSymbolDataSource> logger) : ISymbolDataSource
{
    public async Task<IReadOnlyList<SymbolDataPlan>> LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var rows = await dispatcher.SendAsync<GetSymbolDataConfigurationsQuery, IReadOnlyList<SymbolDataConfiguration>>(
            new GetSymbolDataConfigurationsQuery(), cancellationToken);

        return SymbolDataMapper.ToPlans(rows, (row, reason) =>
            logger.LogWarning("Skipping symbol data #{Id} (symbol listing {Listing}): {Reason}",
                row.Id, row.SymbolListingId, reason));
    }
}
