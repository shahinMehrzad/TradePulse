using Application.Services;
using Microsoft.Extensions.Time.Testing;

namespace Application.UnitTests;

public class EmailCodeThrottleTests
{
    private static readonly Guid User = Guid.NewGuid();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    private EmailCodeThrottle Throttle => new(_time);

    [Fact]
    public void ASecondCodeWithinAMinuteIsRefusedAndOneAfterItIsNot()
    {
        var throttle = Throttle;

        Assert.True(throttle.TryAcquire(User, false));
        Assert.False(throttle.TryAcquire(User, false));

        _time.Advance(EmailCodeThrottle.MinimumGap);
        Assert.True(throttle.TryAcquire(User, false));
    }

    [Fact]
    public void NoMoreThanFiveCodesAnHour()
    {
        var throttle = Throttle;
        for (var i = 0; i < EmailCodeThrottle.MaxPerHour; i++)
        {
            Assert.True(throttle.TryAcquire(User, false));
            _time.Advance(TimeSpan.FromMinutes(2));
        }

        Assert.False(throttle.TryAcquire(User, false)); // six in ten minutes

        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(throttle.TryAcquire(User, false));
    }

    [Fact]
    public void UsersAndPurposesAreCountedApart()
    {
        var throttle = Throttle;

        Assert.True(throttle.TryAcquire(User, false));
        Assert.True(throttle.TryAcquire(User, true)); // the test mail of turning the codes on does not block a sign-in code
        Assert.True(throttle.TryAcquire(Guid.NewGuid(), false));
    }
}
