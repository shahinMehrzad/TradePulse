using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Application.UnitTests;

public class AuditTrailTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeAuditLogRepository : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Exception? Failure { get; set; }

        public Task Add(AuditLogEntry entry, CancellationToken cancellationToken)
        {
            if (Failure is not null)
                throw Failure;

            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedUser(string name) : ICurrentUser
    {
        public string Name { get; } = name;
    }

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));
    private readonly FakeAuditLogRepository _repository = new();

    private AuditTrail Trail(string user = "admin") => new(_repository, new FixedUser(user), _time, NullLogger<AuditTrail>.Instance);

    [Fact]
    public async Task AnEntryNamesTheUserTheTimeAndWhatHappened()
    {
        await Trail("shahin").RecordAsync(AuditAction.Updated, AuditEntityTypes.CandleSeries, 7, "binance / futures / btcusdt / 4h", "open interest off -> on", Ct);

        var entry = Assert.Single(_repository.Entries);
        Assert.Equal(("shahin", AuditAction.Updated, AuditEntityTypes.CandleSeries, 7), (entry.UserName, entry.Action, entry.EntityType, entry.EntityId));
        Assert.Equal(("binance / futures / btcusdt / 4h", "open interest off -> on"), (entry.Summary, entry.Details));
        Assert.Equal(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc), entry.OccurredAt);
    }

    [Fact]
    public async Task AFailureToWriteIsSwallowedBecauseTheChangeItDescribesAlreadyHappened()
    {
        _repository.Failure = new InvalidOperationException("database is down");

        await Trail().RecordAsync(AuditAction.Added, AuditEntityTypes.SymbolData, 1, "binance / futures / btcusdt", null, Ct);

        Assert.Empty(_repository.Entries);
    }

    [Fact]
    public async Task ShuttingDownIsNotSwallowed()
    {
        _repository.Failure = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Trail().RecordAsync(AuditAction.Removed, AuditEntityTypes.SymbolData, 1, "binance / futures / btcusdt", null, Ct));
    }
}
