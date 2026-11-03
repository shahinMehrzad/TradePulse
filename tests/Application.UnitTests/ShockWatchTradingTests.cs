using Application.Commands.AdminConfigurations;
using Application.Interfaces;
using Application.Queries.AdminConfigurations;
using Application.Research.Watch;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Application.UnitTests;

/// <summary>
/// A symbol enabled for trading is watched on its own; the admin can switch that off on their own responsibility, and can add symbols
/// by hand to study them. Per symbol, a row decides; with no row, trading decides.
/// </summary>
public class ShockWatchTradingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SymbolListing BtcFutures = new() { Id = 1, ExchangeId = 1, MarketId = 2, SymbolId = 1, ExchangeSymbol = "BTCUSDT" };
    private static readonly SymbolListing EthFutures = new() { Id = 3, ExchangeId = 1, MarketId = 2, SymbolId = 3, ExchangeSymbol = "ETHUSDT" };
    private static readonly SymbolListing SolFutures = new() { Id = 4, ExchangeId = 1, MarketId = 2, SymbolId = 4, ExchangeSymbol = "SOLUSDT" };
    private static readonly SymbolListing BtcSpot = new() { Id = 15, ExchangeId = 1, MarketId = 1, SymbolId = 1, ExchangeSymbol = "BTCUSDT" };

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
    private readonly FakeTradedSymbolSource _traded = new();
    private readonly FakeSymbolCatalog _catalog = new(BtcFutures, EthFutures, SolFutures, BtcSpot);
    private readonly FakeAuditLog _auditLog = new();

    private AuditTrail Audit => new(_auditLog, new Admin(), new FakeTimeProvider(), NullLogger<AuditTrail>.Instance);

    private void OnTheList(SymbolListing listing, WatchReasonEnum reason, bool enabled) =>
        _watch.Rows.Add(new ShockWatchConfiguration { Id = _watch.Rows.Count + 10, SymbolListingId = listing.Id, SymbolListing = listing, Reason = reason, IsEnabled = enabled });

    private Task<IReadOnlyList<WatchedSymbol>> FollowedAsync() => new ConfiguredWatchedSymbols(_watch, _traded, _catalog).GetAsync(Ct);

    private static IEnumerable<string> Names(IEnumerable<WatchedSymbol> symbols) => symbols.Select(s => s.Symbol.Value).Order();

    // ---- what the watch follows ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASymbolEnabledForTradingIsWatchedWithoutAnyRow()
    {
        _traded.Enabled.UnionWith(["BTCUSDT", "ETHUSDT"]);

        var followed = await FollowedAsync();

        Assert.Equal(["BTCUSDT", "ETHUSDT"], Names(followed));
        Assert.All(followed, s => Assert.Equal(MarketTypeEnum.Futures, s.MarketType));
    }

    [Fact]
    public async Task TradingIsOnFuturesSoTheSpotListingOfTheSameNameIsNotTheOneWatched()
    {
        _traded.Enabled.Add("BTCUSDT");

        var followed = Assert.Single(await FollowedAsync());

        Assert.Equal((BtcFutures.Id, MarketTypeEnum.Futures), (followed.SymbolListingId, followed.MarketType));
    }

    [Fact]
    public async Task ARowDecidesOverTradingWhenTheAdminSwitchedTheWatchOff()
    {
        _traded.Enabled.UnionWith(["BTCUSDT", "ETHUSDT"]);
        OnTheList(EthFutures, WatchReasonEnum.Trading, enabled: false);

        Assert.Equal(["BTCUSDT"], Names(await FollowedAsync()));
    }

    [Fact]
    public async Task ASymbolAddedByHandToStudyItIsWatchedWithoutTradingAndOnlyOnceWhenItIsTradedToo()
    {
        OnTheList(SolFutures, WatchReasonEnum.Manual, enabled: true);
        Assert.Equal(["SOLUSDT"], Names(await FollowedAsync()));

        _traded.Enabled.UnionWith(["SOLUSDT", "BTCUSDT"]);

        Assert.Equal(["BTCUSDT", "SOLUSDT"], Names(await FollowedAsync()));
    }

    [Fact]
    public async Task ASymbolThatStopsBeingTradedStopsBeingWatchedBecauseNothingKeepsItThere()
    {
        _traded.Enabled.UnionWith(["BTCUSDT", "ETHUSDT"]);
        Assert.Equal(2, (await FollowedAsync()).Count);

        _traded.Enabled.Remove("ETHUSDT");

        Assert.Equal(["BTCUSDT"], Names(await FollowedAsync()));
    }

    [Fact]
    public async Task ATradedNameNobodyListedIsLeftOutBecauseThereIsNothingToFollowItBy()
    {
        _traded.Enabled.UnionWith(["BTCUSDT", "XYZUSDT"]);

        Assert.Equal(["BTCUSDT"], Names(await FollowedAsync()));
    }

    [Fact]
    public async Task ATradingServiceThatCannotBeReadIsAFailureNotAnEmptyAnswerSoNoSymbolIsDroppedOverAHiccup()
    {
        _traded.Throws = true;

        await Assert.ThrowsAsync<InvalidOperationException>(FollowedAsync);
    }

    // ---- the list the admin sees ---------------------------------------------------------------------------------------------

    private Task<IReadOnlyList<Application.Dtos.ShockWatch.ShockWatchListItem>> ListAsync() =>
        new GetShockWatchQueryHandler(_watch, _series, _traded, _catalog).HandleAsync(new GetShockWatchQuery(), Ct);

    [Fact]
    public async Task TheListShowsTradedSymbolsWithNoRowAsAutomaticAndTheRowsAsTheyAre()
    {
        _traded.Enabled.UnionWith(["BTCUSDT", "ETHUSDT"]);
        OnTheList(EthFutures, WatchReasonEnum.Trading, enabled: false);       // the admin switched this one off
        OnTheList(SolFutures, WatchReasonEnum.Manual, enabled: true);          // and studies this one by hand
        _series.Configurations.Add(new CandleWorkerConfiguration { SymbolListingId = BtcFutures.Id, TimeFrameId = (int)TimeFrameEnum._1M });

        var list = await ListAsync();

        Assert.Equal(["BTCUSDT", "ETHUSDT", "SOLUSDT"], list.Select(i => i.ExchangeSymbol));
        var btc = list[0];
        Assert.Equal((0, "Trading", true, true, true), (btc.Id, btc.Reason, btc.IsEnabled, btc.IsTradedNow, btc.HasMinuteCandles));
        Assert.Null(btc.CreateDate);
        var eth = list[1];
        Assert.Equal(("Trading", false, true), (eth.Reason, eth.IsEnabled, eth.IsTradedNow));
        Assert.NotEqual(0, eth.Id);
        var sol = list[2];
        Assert.Equal(("Manual", true, false), (sol.Reason, sol.IsEnabled, sol.IsTradedNow));
    }

    [Fact]
    public async Task ARowOfASymbolThatIsNoLongerTradedSaysSo()
    {
        OnTheList(EthFutures, WatchReasonEnum.Trading, enabled: false);

        Assert.False(Assert.Single(await ListAsync()).IsTradedNow);
    }

    // ---- switching off a traded symbol ---------------------------------------------------------------------------------------

    private Task<int> SwitchOffAsync(int listingId) =>
        new SwitchOffTradedWatchCommandHandler(_watch, _catalog, _traded, Audit).HandleAsync(new SwitchOffTradedWatchCommand(listingId), Ct);

    [Fact]
    public async Task SwitchingOffATradedSymbolLeavesARowThatSaysTheAdminDidItAndTheWatchStopsFollowingIt()
    {
        _traded.Enabled.UnionWith(["BTCUSDT", "ETHUSDT"]);

        var id = await SwitchOffAsync(EthFutures.Id);

        var row = Assert.Single(_watch.Rows);
        Assert.Equal((id, EthFutures.Id, WatchReasonEnum.Trading, false), (row.Id, row.SymbolListingId, row.Reason, row.IsEnabled));
        Assert.Equal(["BTCUSDT"], Names(await FollowedAsync()));
        var entry = Assert.Single(_auditLog.Entries);
        Assert.Equal(("shahin", AuditAction.Updated, AuditEntityTypes.ShockWatch), (entry.UserName, entry.Action, entry.EntityType));
        Assert.Equal("watch on -> off (enabled for trading)", entry.Details);
    }

    [Fact]
    public async Task OnlyASymbolEnabledForTradingHasAnAutomaticWatchToSwitchOff()
    {
        _traded.Enabled.Add("BTCUSDT");

        var refused = await Assert.ThrowsAsync<ConfigurationValidationException>(() => SwitchOffAsync(EthFutures.Id));

        Assert.Contains("not enabled for trading", refused.Message);
        Assert.Empty(_watch.Rows);
        await Assert.ThrowsAsync<ConfigurationValidationException>(() => SwitchOffAsync(999));
        await Assert.ThrowsAsync<ConfigurationValidationException>(() => SwitchOffAsync(0));
    }

    [Fact]
    public async Task SwitchingOffTwiceIsTheSameAsOnce()
    {
        _traded.Enabled.Add("BTCUSDT");

        var first = await SwitchOffAsync(BtcFutures.Id);
        var second = await SwitchOffAsync(BtcFutures.Id);

        Assert.Equal(first, second);
        Assert.Single(_watch.Rows);
        Assert.Single(_auditLog.Entries);
    }

    [Fact]
    public async Task ASymbolStudiedByHandAndTradedToIsSwitchedOffByTurningItsRowOff()
    {
        _traded.Enabled.Add("SOLUSDT");
        OnTheList(SolFutures, WatchReasonEnum.Manual, enabled: true);

        await SwitchOffAsync(SolFutures.Id);

        var row = Assert.Single(_watch.Rows);
        Assert.Equal((WatchReasonEnum.Manual, false), (row.Reason, row.IsEnabled));   // still the admin's own row: only its switch changed
        Assert.Empty(await FollowedAsync());
    }

    // ---- switching it back on ------------------------------------------------------------------------------------------------

    private Task<bool> SetAsync(int id, bool enabled) =>
        new SetShockWatchEnabledCommandHandler(_watch, _series, Audit).HandleAsync(new SetShockWatchEnabledCommand(id, enabled), Ct);

    [Fact]
    public async Task SwitchingATradedSymbolBackOnRemovesTheRowSoItIsAutomaticAgainEvenWithoutMinuteCandles()
    {
        _traded.Enabled.Add("ETHUSDT");
        var id = await SwitchOffAsync(EthFutures.Id);
        Assert.Empty(await FollowedAsync());

        await SetAsync(id, true);                         // no 1 minute candles are collected: a traded symbol is not refused its watch

        Assert.Empty(_watch.Rows);
        Assert.Equal(["ETHUSDT"], Names(await FollowedAsync()));
        Assert.EndsWith("(automatic again: enabled for trading)", _auditLog.Entries[^1].Details);
    }

    [Fact]
    public async Task OnceTheSymbolIsNoLongerTradedTheAutomaticWatchGoesAwayWithoutAnyCleanUp()
    {
        _traded.Enabled.Add("ETHUSDT");
        var id = await SwitchOffAsync(EthFutures.Id);
        await SetAsync(id, true);
        Assert.Equal(["ETHUSDT"], Names(await FollowedAsync()));

        _traded.Enabled.Clear();

        Assert.Empty(await FollowedAsync());
        Assert.Empty(_watch.Rows);
    }

    [Fact]
    public async Task ARowAddedByHandStillNeedsTheMinuteCandlesToBeSwitchedOn()
    {
        OnTheList(SolFutures, WatchReasonEnum.Manual, enabled: false);

        await Assert.ThrowsAsync<ConfigurationValidationException>(() => SetAsync(_watch.Rows[0].Id, true));
    }
}
