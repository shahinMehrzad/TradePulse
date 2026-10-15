using BackgroundWorker.Health;
using Microsoft.Extensions.Time.Testing;

namespace BackgroundWorker.UnitTests.Health;

public class WorkerHealthStateTests
{
    private const string Component = "test";

    [Fact]
    public void UnknownComponentsHaveAnEmptySnapshot()
    {
        var snapshot = new WorkerHealthState(new FakeTimeProvider()).Get(Component);

        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Null(snapshot.LastSuccessAt);
        Assert.Null(snapshot.Connected);
        Assert.Equal(0, snapshot.MessageCount);
    }

    [Fact]
    public void FailuresAccumulateUntilASuccessResetsThem()
    {
        var time = new FakeTimeProvider();
        var state = new WorkerHealthState(time);

        state.RecordFailure(Component, "boom");
        state.RecordFailure(Component, "boom again");
        Assert.Equal(2, state.Get(Component).ConsecutiveFailures);
        Assert.Equal("boom again", state.Get(Component).LastError);

        state.RecordSuccess(Component);
        var snapshot = state.Get(Component);

        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Equal(time.GetUtcNow().UtcDateTime, snapshot.LastSuccessAt);
        Assert.NotNull(snapshot.LastFailureAt);
    }

    [Fact]
    public void RepeatingTheSameConnectionStateKeepsTheOriginalTimestamp()
    {
        var time = new FakeTimeProvider();
        var state = new WorkerHealthState(time);

        state.SetConnected(Component, false);
        var changedAt = state.Get(Component).ConnectionChangedAt;
        time.Advance(TimeSpan.FromMinutes(5));
        state.SetConnected(Component, false);

        Assert.Equal(changedAt, state.Get(Component).ConnectionChangedAt);

        state.SetConnected(Component, true);
        Assert.True(state.Get(Component).Connected);
        Assert.Equal(changedAt + TimeSpan.FromMinutes(5), state.Get(Component).ConnectionChangedAt);
    }

    [Fact]
    public void MessagesAreCountedAndTimestamped()
    {
        var time = new FakeTimeProvider();
        var state = new WorkerHealthState(time);

        state.RecordMessage(Component);
        time.Advance(TimeSpan.FromSeconds(3));
        state.RecordMessage(Component);

        var snapshot = state.Get(Component);
        Assert.Equal(2, snapshot.MessageCount);
        Assert.Equal(time.GetUtcNow().UtcDateTime, snapshot.LastMessageAt);
    }

    [Fact]
    public async Task ConcurrentReportingDoesNotLoseUpdates()
    {
        var state = new WorkerHealthState(new FakeTimeProvider());

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1_000; i++)
                state.RecordMessage(Component);
        }, TestContext.Current.CancellationToken)));

        Assert.Equal(8_000, state.Get(Component).MessageCount);
    }
}
