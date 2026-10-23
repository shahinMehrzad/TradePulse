using Domain.Entities;

namespace Domain.Repositories;

public interface ISymbolDataConfigurationRepository
{    
    Task<IReadOnlyList<SymbolDataConfiguration>> GetAll(CancellationToken cancellationToken);
    Task<SymbolDataConfiguration?> GetById(int id, CancellationToken cancellationToken);    
    Task<int> Add(SymbolDataConfiguration configuration, CancellationToken cancellationToken);    
    Task<SymbolDataConfiguration> Update(SymbolDataConfiguration configuration, CancellationToken cancellationToken);    
    Task Remove(int id, CancellationToken cancellationToken);
}
