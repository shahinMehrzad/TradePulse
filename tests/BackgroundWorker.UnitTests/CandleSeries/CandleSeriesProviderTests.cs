using Domain.ValueObjects;
using BackgroundWorker.CandleSeries;
using BackgroundWorker.Plans;
using Domain.Enums;
using Infrastructure.Configurations;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// Moves the fake clock until the condition holds. The provider sets its status before it starts the wait for the next
    /// read, so a single jump made right after the status changed can happen before that timer exists and would be lost.
    /// </summary>
    private async Task AdvanceUntilAsync(TimeSpan step, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the provider did not reach the expected state in time");
            _time.Advance(step);
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
        Assert.Equal((PlanStatus.Loaded, 1), (provider.Status, provider.Current.Count));
        Assert.Equal(_time.GetUtcNow().UtcDateTime, provider.LoadedAt);
    }

    [Fact]
    public async Task NothingIsServedBeforeTheSeriesAreKnown()
    {
        using var provider = Provider();

        Assert.Empty(provider.Current);
        Assert.Null(provider.LoadedAt);
        Assert.Equal(PlanStatus.Loading, provider.Status);
        Assert.False(provider.WaitForSeriesAsync(Ct).IsCompleted);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AnEmptyTableMakesTheWorkersWaitAndTheSeriesAreTakenUpWhenTheyAppear()
    {
        using var provider = Provider();
        await provider.StartAsync(Ct);
        await WaitUntilAsync(() => provider.Status == PlanStatus.WaitingForConfiguration);
        var waiting = provider.WaitForSeriesAsync(Ct);

        Assert.False(waiting.IsCompleted);
        Assert.Empty(provider.Current);

        // The admin adds the first series; the next look finds it.
        _source.Series.Add(Btc4h);
        await AdvanceUntilAsync(CandleSeriesProvider.RetryDelay, () => waiting.IsCompleted);

        Assert.Equal(Btc4h, Assert.Single(await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct)));
        Assert.Equal(PlanStatus.Loaded, provider.Status);
    }

    [Fact]
    public async Task AnUnreadableDatabaseIsRetriedNotFatalAndOnlyTheErrorTypeIsKept()
    {
        _source.Failure = new InvalidOperationException("password authentication failed for user secret-user at secret-host");
        using var provider = Provider();

        await provider.StartAsync(Ct);
        await WaitUntilAsync(() => provider.Status == PlanStatus.Failed);

        Assert.Equal(nameof(InvalidOperationException), provider.LastError);
        Assert.DoesNotContain("secret", provider.LastError);

        // The database comes back with a series: the worker recovers by itself.
        _source.Failure = null;
        _source.Series.Add(Btc4h);
        await AdvanceUntilAsync(CandleSeriesProvider.RetryDelay, () => provider.Status == PlanStatus.Loaded);

        await provider.WaitForSeriesAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal((PlanStatus.Loaded, null), (provider.Status, provider.LastError));
    }

    [Fact]
    public async Task ARepeatedFailureIsTriedEveryRetryDelayNotInATightLoop()
    {
        _source.Failure = new InvalidOperationException();
        using var provider = Provider();
        await provider.StartAsync(Ct);
        await WaitUntilAsync(() => provider.Status == PlanStatus.Failed);

        _time.Advance(CandleSeriesProvider.RetryDelay - TimeSpan.FromSeconds(1));
        await Task.Delay(50, Ct);
        Assert.Equal(1, _source.Calls);

        await AdvanceUntilAsync(TimeSpan.FromSeconds(1), () => _source.Calls == 2);
    }

    [Fact]
    public async Task AReadThatHangsCountsAsAFailure()
    {
        var hanging = new HangingSource();
        using var provider = new CandleSeriesProvider(hanging, _time, Microsoft.Extensions.Logging.Abstractions.NullLogger<CandleSeriesProvider>.Instance);

        await provider.StartAsync(Ct);
        await hanging.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        _time.Advance(CandleSeriesProvider.AttemptTimeout);

        await WaitUntilAsync(() => provider.Status == PlanStatus.Failed);
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

    // ---- a list that is known keeps being read: what the admin changes reaches the workers without a restart ----

    private static readonly CandleSeriesPlan Eth4h = TestPlans.Binance(MarketTypeEnum.Futures, SymbolEnum.ETHUSDT, TimeFrameEnum._4H);

    [Fact]
    public async Task ASeriesAddedLaterIsFoundAtALaterLookAndPublishedUnderANewVersion()
    {
        _source.Series.Add(Btc4h);
        using var provider = Provider();
        await provider.StartAsync(Ct);
        await provider.WaitForSeriesAsync(Ct);
        Assert.Equal(1, provider.Version);

        _source.Series.Add(Eth4h);
        await AdvanceUntilAsync(CandleSeriesProvider.RefreshInterval, () => provider.Version == 2);

        Assert.Equal([Btc4h, Eth4h], provider.Current.Order(Comparer<CandleSeriesPlan>.Create((a, b) => string.CompareOrdinal(a.Series.Symbol.Value, b.Series.Symbol.Value))));
        Assert.Equal(PlanStatus.Loaded, provider.Status);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, provider.LoadedAt);   // grace periods count from the change
    }

    [Fact]
    public async Task WhatChangedIsSaidInTheLogEvenWhenTheNumberOfSeriesStaysTheSame()
    {
        // What the admin reads in the console: an edited series leaves the total alone, so the total cannot be the message.
        var log = new TestSupport.ListLogger<CandleSeriesProvider>();
        _source.Series.AddRange([Btc4h, Eth4h]);
        using var provider = new CandleSeriesProvider(_source, _time, log);
        await provider.StartAsync(Ct);
        await provider.WaitForSeriesAsync(Ct);
        Assert.Contains(log.Entries, e => e.Message == "Collecting 2 candle series.");

        _source.Series[1] = Eth4h with { CollectOpenInterest = true };   // edited, not added or removed
        await AdvanceUntilAsync(CandleSeriesProvider.RefreshInterval, () => provider.Version == 2);

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information
            && e.Message == "The candle series changed: 1 new, 1 gone (an edited one counts as both), 2 now.");
    }

    [Fact]
    public async Task ALookThatFindsTheSameListChangesNothingNotEvenWhenTheOrderDiffers()
    {
        _source.Series.AddRange([Btc4h, Eth4h]);
        using var provider = Provider();
        await provider.StartAsync(Ct);
        await provider.WaitForSeriesAsync(Ct);
        var loadedAt = provider.LoadedAt;

        _source.Series.Reverse();
        var callsBefore = _source.Calls;
        await AdvanceUntilAsync(CandleSeriesProvider.RefreshInterval, () => _source.Calls >= callsBefore + 2);

        Assert.Equal((1L, loadedAt), (provider.Version, provider.LoadedAt));
    }

    [Fact]
    public async Task EmptyingTheTableIsAChangeToo_TheCollectorIdlesAgain()
    {
        _source.Series.Add(Btc4h);
        using var provider = Provider();
        await provider.StartAsync(Ct);
        await provider.WaitForSeriesAsync(Ct);

        _source.Series.Clear();
        await AdvanceUntilAsync(CandleSeriesProvider.RefreshInterval, () => provider.Version == 2);

        Assert.Equal((PlanStatus.WaitingForConfiguration, 0), (provider.Status, provider.Current.Count));
    }

    [Fact]
    public async Task ADatabaseThatFailsAfterTheListIsKnownKeepsTheLastListAndOnlyReportsTheError()
    {
        _source.Series.Add(Btc4h);
        using var provider = Provider();
        await provider.StartAsync(Ct);
        await provider.WaitForSeriesAsync(Ct);

        _source.Failure = new InvalidOperationException("password authentication failed for user secret-user");
        await AdvanceUntilAsync(CandleSeriesProvider.RefreshInterval, () => provider.LastError is not null);

        Assert.Equal((PlanStatus.Loaded, 1, 1L), (provider.Status, provider.Current.Count, provider.Version));   // nothing is stopped
        Assert.Equal(nameof(InvalidOperationException), provider.LastError);

        // It recovers by itself, and sees what changed meanwhile.
        _source.Failure = null;
        _source.Series.Add(Eth4h);
        await AdvanceUntilAsync(CandleSeriesProvider.RefreshInterval, () => provider.Version == 2);

        Assert.Equal((null, 2), (provider.LastError, provider.Current.Count));
    }

    [Fact]
    public async Task WaitingForAChangeIsAnsweredAtOnceForAnOlderVersionAndLaterForTheNextOne()
    {
        _source.Series.Add(Btc4h);
        using var provider = Provider();
        await provider.StartAsync(Ct);
        await provider.WaitForSeriesAsync(Ct);

        var older = await provider.WaitForChangeAsync(0, Ct);
        var next = provider.WaitForChangeAsync(older.Version, Ct);

        Assert.Equal((1L, 1), (older.Version, older.Plans.Count));
        Assert.False(next.IsCompleted);

        _source.Series.Add(Eth4h);
        await AdvanceUntilAsync(CandleSeriesProvider.RefreshInterval, () => next.IsCompleted);

        Assert.Equal((2L, 2), ((await next).Version, (await next).Plans.Count));
    }

    [Fact]
    public async Task ShuttingDownCancelsTheWorkersThatAreStillWaiting()
    {
        using var provider = Provider();
        await provider.StartAsync(Ct);
        await WaitUntilAsync(() => provider.Status == PlanStatus.WaitingForConfiguration);
        var waiting = provider.WaitForSeriesAsync(Ct);

        await provider.StopAsync(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task ACallerCanStopWaitingWithoutAffectingTheProvider()
    {
        using var provider = Provider();
        await provider.StartAsync(Ct);
        await WaitUntilAsync(() => provider.Status == PlanStatus.WaitingForConfiguration);
        using var impatient = new CancellationTokenSource();

        var waiting = provider.WaitForSeriesAsync(impatient.Token);
        await impatient.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(PlanStatus.WaitingForConfiguration, provider.Status);
    }
}
