using BackgroundWorker.Plans;
using BackgroundWorker.SymbolData;
using Microsoft.Extensions.Time.Testing;

namespace BackgroundWorker.UnitTests.SymbolData;

/// <summary>
/// How a provider waits, retries and freezes is covered in full by <c>CandleSeriesProviderTests</c> (same <c>PlanProvider</c>);
/// this checks that the symbol data one is wired to its own source and plans.
/// </summary>
public class SymbolDataProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

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
    public async Task ThePlansOfItsSourceAreHandedOutOnceThereIsOne()
    {
        var source = new FakeSymbolDataSource();
        using var provider = SymbolDataTestSupport.NewProvider(source, _time);
        await provider.StartAsync(Ct);
        await WaitUntilAsync(() => provider.Status == PlanStatus.WaitingForConfiguration);

        // The status is set before the provider starts waiting for its next read, so one jump right now could be lost:
        // move the clock until the new plan has been picked up.
        source.Plans.Add(SymbolDataTestSupport.Binance());
        await WaitUntilAsync(() =>
        {
            _time.Advance(SymbolDataProvider.RetryDelay);
            return provider.Status == PlanStatus.Loaded;
        });

        var plans = await provider.WaitForPlansAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(source.Plans, plans);
        Assert.Equal(PlanStatus.Loaded, provider.Status);
    }

    [Fact]
    public async Task ADatabaseThatCannotBeReadIsRetriedNotThrown()
    {
        var source = new FakeSymbolDataSource { Failure = new InvalidOperationException() };
        using var provider = SymbolDataTestSupport.NewProvider(source, _time);

        await provider.StartAsync(Ct);
        await WaitUntilAsync(() => provider.Status == PlanStatus.Failed);

        Assert.Equal(nameof(InvalidOperationException), provider.LastError);
    }
}
