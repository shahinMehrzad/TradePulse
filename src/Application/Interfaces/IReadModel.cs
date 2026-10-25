namespace Application.Interfaces;

/// <summary>
/// A read-only, query-able view of one kind of row, for the admin lists whose caller shapes the query (OData: filter, sort,
/// page). <see cref="Query"/> is a plain <see cref="IQueryable{T}"/> over a flat DTO, so the caller can compose on it without
/// knowing what stores it; the two execute methods are how it is run, because only the implementation knows the provider.
/// </summary>
public interface IReadModel<T>
{
    IQueryable<T> Query();

    Task<int> CountAsync(IQueryable<T> query, CancellationToken cancellationToken);

    Task<IReadOnlyList<T>> ToListAsync(IQueryable<T> query, CancellationToken cancellationToken);
}
