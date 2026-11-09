using BackgroundWorker.Collection.Candles;
using Microsoft.Extensions.Time.Testing;

namespace BackgroundWorker.UnitTests.Collection;

/// <summary>What the gap repair remembers: when the short repair is due and how often the exchange was asked about a hole.</summary>
public class GapRepairScheduleTests
{
    private const string Table = "binance_futures_btcusdt_4h";
    private static readonly DateTime Start = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = new(2026, 1, 1, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero));

    private GapRepairSchedule NewSchedule(GapRepairSettings? settings = null) => new(_time, settings);

    [Fact]
    public void AnUnknownTableIsDueForTheShortRepair() => Assert.True(NewSchedule().IsRecentDue(Table));

    [Fact]
    public void ATableIsDueAgainWhenTheHourHasPassedAndNotBefore()
    {
        var schedule = NewSchedule();
        schedule.MarkRecentRepaired(Table);

        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.False(schedule.IsRecentDue(Table));

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(schedule.IsRecentDue(Table));
    }

    [Fact]
    public void TheTablesAreIndependent()
    {
        var schedule = NewSchedule();
        schedule.MarkRecentRepaired(Table);

        Assert.True(schedule.IsRecentDue("binance_futures_ethusdt_4h"));
    }

    [Fact]
    public void TheShortRepairStartsFourteenDaysBack()
    {
        Assert.Equal(new DateTime(2025, 12, 27, 12, 0, 0, DateTimeKind.Utc), NewSchedule().RecentSince(Start));
    }

    [Fact]
    public void TheShortRepairNeverStartsBeforeTheStartDateOfTheSeries()
    {
        var lateStart = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(lateStart, NewSchedule().RecentSince(lateStart));
    }

    [Fact]
    public void AHoleMayBeAskedAboutThreeTimesAndNotAFourth()
    {
        var schedule = NewSchedule();

        for (var asked = 0; asked < 3; asked++)
        {
            Assert.True(schedule.MayAsk(Table, From, To));
            schedule.RecordAsk(Table, From, To);
        }

        Assert.False(schedule.MayAsk(Table, From, To));
    }

    [Fact]
    public void AnotherHoleOrAnotherTableHasItsOwnCount()
    {
        var schedule = NewSchedule(new GapRepairSettings { MaxAsksPerHole = 1 });
        schedule.RecordAsk(Table, From, To);

        Assert.False(schedule.MayAsk(Table, From, To));
        Assert.True(schedule.MayAsk(Table, From, To.AddHours(4)));   // the hole grew: a different one
        Assert.True(schedule.MayAsk("binance_futures_ethusdt_4h", From, To));
    }

    [Fact]
    public void TheLimitIsTheSettingsAndNotAFixedNumber()
    {
        var schedule = NewSchedule(new GapRepairSettings { MaxAsksPerHole = 5 });

        for (var asked = 0; asked < 4; asked++)
            schedule.RecordAsk(Table, From, To);

        Assert.True(schedule.MayAsk(Table, From, To));
    }

    [Fact]
    public void ForgettingKeepsTheHolesThatAreStillThereAndDropsTheOthers()
    {
        var schedule = NewSchedule();
        schedule.RecordAsk(Table, From, To);
        schedule.RecordAsk("binance_futures_ethusdt_4h", From, To);

        schedule.ForgetAsksExcept(Table, [(From.AddDays(1), To.AddDays(1))]);

        Assert.Equal(0, schedule.AsksAbout(Table, From, To));                              // filled: forgotten
        Assert.Equal(1, schedule.AsksAbout("binance_futures_ethusdt_4h", From, To));        // another table: untouched
    }

    [Fact]
    public void AHoleIsWrittenDownOnceAndForgottenWhenItIsGone()
    {
        var schedule = NewSchedule();
        Assert.False(schedule.IsConfirmed(Table, From, To));

        schedule.MarkConfirmed(Table, From, To);
        Assert.True(schedule.IsConfirmed(Table, From, To));

        schedule.ForgetAsksExcept(Table, []);
        Assert.False(schedule.IsConfirmed(Table, From, To));
    }

    [Fact]
    public void AHoleOlderThanTheLookIsNotForgottenByIt()
    {
        var schedule = NewSchedule();
        schedule.RecordAsk(Table, From, To);

        schedule.ForgetAsksExcept(Table, [], since: To.AddDays(1));   // the short look starts after the hole: it cannot know it is gone

        Assert.Equal(1, schedule.AsksAbout(Table, From, To));
    }

    [Fact]
    public void ASettingThatMakesNoSenseIsRefusedAtTheStart()
    {
        Assert.Throws<InvalidOperationException>(() => new GapRepairSettings { RecentWindowDays = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new GapRepairSettings { RecentPeriodMinutes = 1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new GapRepairSettings { MaxAsksPerHole = 0 }.Validate());
        Assert.Same(new GapRepairSettings().Validate().GetType(), typeof(GapRepairSettings));
    }
}
