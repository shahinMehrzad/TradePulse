using Domain.ValueObjects;
using BackgroundWorker.CandleSeries;
using Domain.Enums;
using Infrastructure.Configurations;
using Microsoft.Extensions.Time.Testing;

namespace BackgroundWorker.UnitTests.CandleSeries;

public class CandleSeriesProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CandleSeriesPlan Btc4h { get; } =
        TestPlans.Binance(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._4H);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeCandleSeriesSource _source = new();

    private CandleSeriesProvider Provider() => CandleSeriesTestSupport.NewProvider(_source, _time);

    /// <summary>The provider works on a background task; wait (really, briefly) until it has settled into a state.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the provider did not reach the expected state in time");
            await Task.Delay(5, Ct);
        }
    }

    [Fact]
    public async Task SeriesThatAreThereAtStartAreServedAtOnce()
    {
        _source.Series.Add(Btc4h);
        using var provider = Provider();

        await provider.StartAsync(Ct);
        var series = await provider.WaitForSeriesAsync(Ct);

        Assert.Equal(Btc4h, Assert.Single(series));
        Assert.Equal((CandleSeriesStatus.Loaded, 1), (provider.Status, provider.Current.Count));
        Assert.Equal(_time.GetUtcNow().UtcDateTime, provider.LoadedAt);
    }

    [Fact]
    public async Task NothingIsServedBeforeTheSeriesAreKnown()
    {
        using var provider = Provider();

        Assert.Empty(provider.Current);
        Assert.Null(provider.LoadedAt);
        Assert.Equal(CandleSeriesStatus.Loading, provider.Status);
        Assert.False(provider.WaitForSeriesAsync(Ct).IsCompleted);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AnEmptyTableMakesTheWorkersWaitAndTheSeriesAreTakenUpWhenTheyAppear()
    {
        using var provider = Provider();
        await provider.StartAsync(Ct);
        await WaitUntilAsync(() => provider.Status == CandleSeriesStatus.WaitingForConfiguration);
        var waiting = provider.WaitForSeriesAsync(Ct);

        Assert.False(waiting.IsCompleted);
        Assert.Empty(provider.Current);

        // The admin adds the first series; the next look finds it.
        _source.Series.Add(Btc4h);
        _time.Advance(CandleSeriesProvider.RetryDelay);

        Assert.Equal(Btc4h, Assert.Single(await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct)));
        Assert.Equal(CandleSeriesStatus.Loaded, provider.Status);
    }

    [Fact]
    public async Task AnUnreadableDatabaseIsRetriedNotFatalAndOnlyTheErrorTypeIsKept()
    {
        _source.Failure = new InvalidOperationException("password authentication failed for user secret-user at secret-host");
        using var provider = Provider();

        await provider.StartAsync(Ct);
        await WaitUntilAsync(() => provider.Status == CandleSeriesStatus.Failed);

        Assert.Equal(nameof(InvalidOperationException), provider.LastError);
        Assert.DoesNotContain("secret", provider.LastError);

        // The database comes back with a series: the worker recovers by itself.
        _source.Failure = null;
        _source.Series.Add(Btc4h);
        _time.Advance(CandleSeriesProvider.RetryDelay);

        await provider.WaitForSeriesAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal((CandleSeriesStatus.Loaded, null), (provider.Status, provider.LastError));
    }

    [Fact]
    public async Task ARepeatedFailureIsTriedEveryRetryDelayNotInATightLoop()
    {
        _source.Failure = new InvalidOperationException();
        using var provider = Provider();
        await provider.StartAsync(Ct);
        await WaitUntilAsync(() => provider.Status == CandleSeriesStatus.Failed);

        _time.Advance(CandleSeriesProvider.RetryDelay - TimeSpan.FromSeconds(1));
        await Task.Delay(50, Ct);
        Assert.Equal(1, _source.Calls);

        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => _source.Calls == 2);
    }

    [Fact]
    public async Task AReadThatHangsCountsAsAFailure()
    {
        var hanging = new HangingSource();
        using var provider = new CandleSeriesProvider(hanging, _time, Microsoft.Extensions.Logging.Abstractions.NullLogger<CandleSeriesProvider>.Instance);

        await provider.StartAsync(Ct);
        await hanging.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        _time.Advance(CandleSeriesProvider.AttemptTimeout);

        await WaitUntilAsync(() => provider.Status == CandleSeriesStatus.Failed);
        Assert.Equal(nameof(TaskCanceledException), provider.LastError);
    }

    private sealed class HangingSource : ICandleSeriesSource
    {
        public TaskCompletionSource Started { get; } = new();

        public async Task<IReadOnlyList<CandleSeriesPlan>> LoadAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        }
    }

    [Fact]
    public async Task OnceLoadedTheSeriesDoNotChangeUntilTheProcessIsRestarted()
    {
        _source.Series.Add(Btc4h);
        using var provider = Provider();
        await provider.StartAsync(Ct);
        await provider.WaitForSeriesAsync(Ct);

        _source.Series.Clear();
        _time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50, Ct);

        Assert.Equal(1, _source.Calls);
        Assert.Single(provider.Current);
    }

    [Fact]
    public async Task ShuttingDownCancelsTheWorkersThatAreStillWaiting()
    {
        using var provider = Provider();
        await provider.StartAsync(Ct);
        await WaitUntilAsync(() => provider.Status == CandleSeriesStatus.WaitingForConfiguration);
        var waiting = provider.WaitForSeriesAsync(Ct);

        await provider.StopAsync(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task ACallerCanStopWaitingWithoutAffectingTheProvider()
    {
        using var provider = Provider();
        await provider.StartAsync(Ct);
        await WaitUntilAsync(() => provider.Status == CandleSeriesStatus.WaitingForConfiguration);
        using var impatient = new CancellationTokenSource();

        var waiting = provider.WaitForSeriesAsync(impatient.Token);
        await impatient.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(CandleSeriesStatus.WaitingForConfiguration, provider.Status);
    }
}
