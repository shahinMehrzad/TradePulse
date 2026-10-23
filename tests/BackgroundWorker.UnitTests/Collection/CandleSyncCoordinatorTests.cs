using BackgroundWorker;
using BackgroundWorker.Collection.Candles;

namespace BackgroundWorker.UnitTests.Collection;

public class CandleSyncCoordinatorTests
{
    [Fact]
    public void Table_IsNotReadyUntilMarked()
    {
        var coordinator = new CandleSyncCoordinator();

        Assert.False(coordinator.IsReady("binance_futures_btcusdt_1h"));

        coordinator.MarkReady("binance_futures_btcusdt_1h");

        Assert.True(coordinator.IsReady("binance_futures_btcusdt_1h"));
        Assert.False(coordinator.IsReady("binance_futures_btcusdt_4h"));
    }

    [Fact]
    public void Resync_ReachesEveryRegisteredLoop()
    {
        var coordinator = new CandleSyncCoordinator();
        var first = coordinator.Register();
        var second = coordinator.Register();

        coordinator.RequestResync();

        Assert.True(first.TryRead(out _));
        Assert.True(second.TryRead(out _));
    }

    [Fact]
    public void RepeatedResyncRequests_AreCoalesced()
    {
        var coordinator = new CandleSyncCoordinator();
        var loop = coordinator.Register();

        coordinator.RequestResync();
        coordinator.RequestResync();
        coordinator.RequestResync();

        Assert.True(loop.TryRead(out _));
        Assert.False(loop.TryRead(out _));
    }

    [Fact]
    public void Resync_WithNoLoopsRegistered_DoesNothing() =>
        new CandleSyncCoordinator().RequestResync();
}
