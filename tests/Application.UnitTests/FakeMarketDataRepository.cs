using Domain.Repositories;

namespace Application.UnitTests;

/// <summary>Only the tables listed in <see cref="Latest"/> exist; a null value is a table without rows.</summary>
internal sealed class FakeMarketDataRepository<T> : IMarketDataRepository<T>
{
    public Dictionary<string, DateTime?> Latest { get; } = [];

    public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken) =>
        Task.FromResult(Latest.ContainsKey(tableName));

    public Task<DateTime?> GetLatestTimeAsync(string tableName, CancellationToken cancellationToken) =>
        Latest.TryGetValue(tableName, out var time) ? Task.FromResult(time) : throw new InvalidOperationException($"No table '{tableName}'.");

    public Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<int> BulkInsert(string tableName, IEnumerable<T> rows, CancellationToken cancellationToken) => throw new NotSupportedException();
}
