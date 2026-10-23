using BackgroundWorker.Exchange.Binance;
using Microsoft.Extensions.Time.Testing;

namespace BackgroundWorker.UnitTests.Exchange;

public class BinanceWeightLimiterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AllowsRequestsThatFitInsideTheWindow()
    {
        var limiter = new BinanceWeightLimiter(10, new FakeTimeProvider());

        await limiter.AcquireAsync(4, TestContext.Current.CancellationToken);
        await limiter.AcquireAsync(6, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BlocksOnceTheWindowIsFullUntilTheMinuteHasPassed()
    {
        var time = new FakeTimeProvider();
        var limiter = new BinanceWeightLimiter(10, time);
        await limiter.AcquireAsync(10, TestContext.Current.CancellationToken);

        var blocked = limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(blocked.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        await blocked.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ResetsTheUsedWeightWhenANewWindowStarts()
    {
        var time = new FakeTimeProvider();
        var limiter = new BinanceWeightLimiter(10, time);
        await limiter.AcquireAsync(10, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(1));

        await limiter.AcquireAsync(10, TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitingCallersCanBeCancelled()
    {
        var time = new FakeTimeProvider();
        var limiter = new BinanceWeightLimiter(1, time);
        await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();

        var blocked = limiter.AcquireAsync(1, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.WaitAsync(Timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledCallersDoNotLeaveTheLimiterLocked()
    {
        var time = new FakeTimeProvider();
        var limiter = new BinanceWeightLimiter(1, time);
        await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        var cancelled = limiter.AcquireAsync(1, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Timeout, TestContext.Current.CancellationToken));

        time.Advance(TimeSpan.FromMinutes(1));

        await limiter.AcquireAsync(1, TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }
}
