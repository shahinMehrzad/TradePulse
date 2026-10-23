using Domain.Entities;
using Domain.Repositories;

namespace Application.UnitTests;

internal sealed class FakeSymbolDataConfigurationRepository : ISymbolDataConfigurationRepository
{
    public List<SymbolDataConfiguration> Configurations { get; } = [];

    public Task<IReadOnlyList<SymbolDataConfiguration>> GetAll(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SymbolDataConfiguration>>(Configurations.ToList());

    public Task<SymbolDataConfiguration?> GetById(int id, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<int> Add(SymbolDataConfiguration configuration, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<SymbolDataConfiguration> Update(SymbolDataConfiguration configuration, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task Remove(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
}
