using Application.Commands.AdminConfigurations;
using Application.Interfaces;
using Application.Queries.AdminConfigurations;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Application.UnitTests;

/// <summary>What the admin may do with the watch list, and what has to be true first.</summary>
public class ShockWatchHandlersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SymbolListing Btc = new() { Id = 1, ExchangeId = 1, MarketId = 2, SymbolId = 1, ExchangeSymbol = "BTCUSDT" };
    private static readonly SymbolListing Eth = new() { Id = 3, ExchangeId = 1, MarketId = 2, SymbolId = 3, ExchangeSymbol = "ETHUSDT" };

    private sealed class FakeAuditLog : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task Add(AuditLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class Admin : ICurrentUser
    {
        public string Name => "shahin";
    }

    private readonly FakeShockWatchConfigurationRepository _watch = new();
    private readonly FakeCandleWorkerConfigurationRepository _series = new();
    private readonly FakeAuditLog _auditLog = new();

    private AuditTrail Audit => new(_auditLog, new Admin(), new FakeTimeProvider(), NullLogger<AuditTrail>.Instance);

    private void CollectMinuteCandles(SymbolListing listing) =>
        _series.Configurations.Add(new CandleWorkerConfiguration { SymbolListingId = listing.Id, TimeFrameId = (int)TimeFrameEnum._1M });

    private void OnTheList(SymbolListing listing, WatchReasonEnum reason = WatchReasonEnum.Manual, bool enabled = true) =>
        _watch.Rows.Add(new ShockWatchConfiguration { Id = _watch.Rows.Count + 10, SymbolListingId = listing.Id, SymbolListing = listing, Reason = reason, IsEnabled = enabled });

    private Task<int> AddAsync(int listingId, params SymbolListing[] listings) =>
        new AddShockWatchCommandHandler(_watch, new FakeSymbolCatalog(listings), _series, Audit).HandleAsync(new AddShockWatchCommand(listingId), Ct);

    // ---- adding ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASymbolWithItsMinuteCandlesCollectedIsAddedByTheAdminAndSwitchedOn()
    {
        CollectMinuteCandles(Btc);

        var id = await AddAsync(Btc.Id, Btc, Eth);

        var row = Assert.Single(_watch.Rows);
        Assert.Equal((id, Btc.Id, WatchReasonEnum.Manual, true), (row.Id, row.SymbolListingId, row.Reason, row.IsEnabled));
    }

    [Fact]
    public async Task ASymbolWhoseMinuteCandlesAreNotCollectedCannotBeAdded()
    {
        _series.Configurations.Add(new CandleWorkerConfiguration { SymbolListingId = Btc.Id, TimeFrameId = (int)TimeFrameEnum._1H });

        var refused = await Assert.ThrowsAsync<ConfigurationValidationException>(() => AddAsync(Btc.Id, Btc));

        Assert.Contains("1 minute candles of BTCUSDT", refused.Message);
        Assert.Empty(_watch.Rows);
        Assert.Empty(_auditLog.Entries);
    }

    [Fact]
    public async Task AnUnknownOrMissingListingIsRefused()
    {
        await Assert.ThrowsAsync<ConfigurationValidationException>(() => AddAsync(999, Btc));
        await Assert.ThrowsAsync<ConfigurationValidationException>(() => AddAsync(0, Btc));
    }

    [Fact]
    public async Task ASymbolAlreadyOnTheListIsAConflict()
    {
        CollectMinuteCandles(Btc);
        await AddAsync(Btc.Id, Btc);

        await Assert.ThrowsAsync<ConfigurationConflictException>(() => AddAsync(Btc.Id, Btc));
    }

    [Fact]
    public async Task AddingIsWrittenToTheAuditLog()
    {
        CollectMinuteCandles(Btc);

        var id = await AddAsync(Btc.Id, Btc);

        var entry = Assert.Single(_auditLog.Entries);
        Assert.Equal(("shahin", AuditAction.Added, AuditEntityTypes.ShockWatch, id), (entry.UserName, entry.Action, entry.EntityType, entry.EntityId));
        Assert.Equal("binance / futures / btcusdt", entry.Summary);
        Assert.Equal("manual, watch on", entry.Details);
    }

    // ---- switching ---------------------------------------------------------------------------------------------------------

    private Task<bool> SetAsync(int id, bool enabled) =>
        new SetShockWatchEnabledCommandHandler(_watch, _series, Audit).HandleAsync(new SetShockWatchEnabledCommand(id, enabled), Ct);

    [Fact]
    public async Task SwitchingOffNeedsNothingEvenWhenTheCandlesAreGone()
    {
        OnTheList(Btc);                                   // no 1 minute candles collected any more

        await SetAsync(_watch.Rows[0].Id, false);

        Assert.False(_watch.Rows[0].IsEnabled);
        Assert.Equal("watch on -> off", Assert.Single(_auditLog.Entries).Details);
    }

    [Fact]
    public async Task SwitchingOnNeedsTheMinuteCandles()
    {
        OnTheList(Btc, enabled: false);

        await Assert.ThrowsAsync<ConfigurationValidationException>(() => SetAsync(_watch.Rows[0].Id, true));
        Assert.False(_watch.Rows[0].IsEnabled);

        CollectMinuteCandles(Btc);
        await SetAsync(_watch.Rows[0].Id, true);
        Assert.True(_watch.Rows[0].IsEnabled);
    }

    [Fact]
    public async Task TheWatchOfASymbolEnabledForTradingCanBeSwitchedOffAndStaysOnTheListForReason()
    {
        CollectMinuteCandles(Btc);
        OnTheList(Btc, WatchReasonEnum.Trading);

        await SetAsync(_watch.Rows[0].Id, false);

        var row = Assert.Single(_watch.Rows);
        Assert.Equal((WatchReasonEnum.Trading, false), (row.Reason, row.IsEnabled));
    }

    [Fact]
    public async Task SwitchingToWhatItAlreadyIsChangesNothingAndWritesNothing()
    {
        OnTheList(Btc);

        await SetAsync(_watch.Rows[0].Id, true);

        Assert.Empty(_auditLog.Entries);
    }

    [Fact]
    public async Task SwitchingARowThatDoesNotExistIsNotFound()
    {
        await Assert.ThrowsAsync<ConfigurationNotFoundException>(() => SetAsync(404, true));
        await Assert.ThrowsAsync<ConfigurationValidationException>(() => SetAsync(0, true));
    }

    // ---- removing ----------------------------------------------------------------------------------------------------------

    private Task<bool> RemoveAsync(int id) =>
        new RemoveShockWatchCommandHandler(_watch, Audit).HandleAsync(new RemoveShockWatchCommand(id), Ct);

    [Fact]
    public async Task ASymbolTheAdminAddedCanBeRemoved()
    {
        OnTheList(Btc);

        await RemoveAsync(_watch.Rows[0].Id);

        Assert.Empty(_watch.Rows);
        Assert.Equal(AuditAction.Removed, Assert.Single(_auditLog.Entries).Action);
    }

    [Fact]
    public async Task ASymbolThereBecauseOfTradingIsNotRemovedBecauseSwitchingItOnIsTheWay()
    {
        OnTheList(Btc, WatchReasonEnum.Trading);

        var refused = await Assert.ThrowsAsync<ConfigurationValidationException>(() => RemoveAsync(_watch.Rows[0].Id));

        Assert.Contains("Switch its watch on to return to the automatic watch", refused.Message);
        Assert.Single(_watch.Rows);
    }

    [Fact]
    public async Task RemovingARowThatDoesNotExistIsNotFound()
    {
        await Assert.ThrowsAsync<ConfigurationNotFoundException>(() => RemoveAsync(404));
    }

    // ---- reading -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheListShowsEachSymbolWithItsReasonItsSwitchAndWhetherItHasMinuteCandles()
    {
        CollectMinuteCandles(Btc);
        OnTheList(Btc, WatchReasonEnum.Trading);
        OnTheList(Eth, WatchReasonEnum.Manual, enabled: false);

        var list = await new GetShockWatchQueryHandler(_watch, _series, new FakeTradedSymbolSource(), new FakeSymbolCatalog(Btc, Eth)).HandleAsync(new GetShockWatchQuery(), Ct);

        Assert.Equal(["BTCUSDT", "ETHUSDT"], list.Select(i => i.ExchangeSymbol));
        Assert.Equal(["Trading", "Manual"], list.Select(i => i.Reason));
        Assert.Equal([true, false], list.Select(i => i.IsEnabled));
        Assert.Equal([true, false], list.Select(i => i.HasMinuteCandles));
    }

    // ---- what the watch follows --------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheWatchFollowsOnlyTheSymbolsThatAreSwitchedOn()
    {
        OnTheList(Btc);
        OnTheList(Eth, enabled: false);

        var followed = await new Application.Research.Watch.ConfiguredWatchedSymbols(_watch, new FakeTradedSymbolSource(), new FakeSymbolCatalog(Btc, Eth)).GetAsync(Ct);

        var symbol = Assert.Single(followed);
        Assert.Equal(("BTCUSDT", Btc.Id, ExchangeEnum.Binance, MarketTypeEnum.Futures), (symbol.Symbol.Value, symbol.SymbolListingId, symbol.Exchange, symbol.MarketType));
    }

    [Fact]
    public async Task AnEmptyListIsNothingToFollowNotAnError()
    {
        Assert.Empty(await new Application.Research.Watch.ConfiguredWatchedSymbols(_watch, new FakeTradedSymbolSource(), new FakeSymbolCatalog(Btc, Eth)).GetAsync(Ct));
    }
}
