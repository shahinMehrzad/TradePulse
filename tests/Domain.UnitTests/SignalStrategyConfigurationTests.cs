using Domain.Entities;

namespace Domain.UnitTests;

public class SignalStrategyConfigurationTests
{
    private static SignalStrategyConfiguration Valid() => new()
    {
        Strategy = "S1", IsEnabled = true, MaxSignalAgeSeconds = 3600, MaxMoveOfStopDistance = 0.25m, MaxOpenPerSymbol = null, MaxLeverage = 20m,
    };

    [Fact]
    public void ValidValuesHaveNoProblem() => Assert.Null(Valid().FindProblem());

    [Theory]
    [InlineData("S1", true)]
    [InlineData("S2", true)]
    [InlineData("strategy_3-b", true)]
    [InlineData("A", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("has space", false)]
    [InlineData("a:b", false)]
    [InlineData("a/b", false)]
    [InlineData("señal", false)]
    [InlineData("S1;", false)]
    public void ANameIsLettersDigitsUnderscoreAndDash(string? name, bool valid) =>
        Assert.Equal(valid, SignalStrategyConfiguration.IsValidName(name));

    [Fact]
    public void ANameCanBeAsLongAsTheLimitAndNoLonger()
    {
        Assert.True(SignalStrategyConfiguration.IsValidName(new string('a', SignalStrategyConfiguration.MaxNameLength)));
        Assert.False(SignalStrategyConfiguration.IsValidName(new string('a', SignalStrategyConfiguration.MaxNameLength + 1)));
    }

    [Fact]
    public void ABadNameIsAProblem()
    {
        var configuration = Valid();
        configuration.Strategy = "a:b";

        Assert.NotNull(configuration.FindProblem());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(604_801)]
    public void AnAgeOutsideOneSecondToAWeekIsAProblem(int seconds)
    {
        var configuration = Valid();
        configuration.MaxSignalAgeSeconds = seconds;

        Assert.NotNull(configuration.FindProblem());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(604_800)]
    public void TheEdgesOfTheAgeAreAllowed(int seconds)
    {
        var configuration = Valid();
        configuration.MaxSignalAgeSeconds = seconds;

        Assert.Null(configuration.FindProblem());
    }

    [Fact]
    public void NoAgeLimitIsAllowedAndMeansNoLimit()
    {
        var configuration = Valid();
        configuration.MaxSignalAgeSeconds = null;

        Assert.Null(configuration.FindProblem());
        Assert.Null(configuration.ToSettings().MaxSignalAge);
    }

    [Fact]
    public void AMoveLimitOfZeroOrBelowIsAProblem()
    {
        var configuration = Valid();

        configuration.MaxMoveOfStopDistance = 0m;
        Assert.NotNull(configuration.FindProblem());
        configuration.MaxMoveOfStopDistance = -0.1m;
        Assert.NotNull(configuration.FindProblem());
    }

    [Fact]
    public void AMoveLimitAboveTheWholeStopDistanceIsAProblemAndTheWholeIsNot()
    {
        var configuration = Valid();

        configuration.MaxMoveOfStopDistance = 1m;
        Assert.Null(configuration.FindProblem());
        configuration.MaxMoveOfStopDistance = 1.01m;
        Assert.NotNull(configuration.FindProblem());
    }

    [Fact]
    public void ALimitOfOpenTradesBelowOneIsAProblemAndNoLimitIsNot()
    {
        var configuration = Valid();

        configuration.MaxOpenPerSymbol = 0;
        Assert.NotNull(configuration.FindProblem());
        configuration.MaxOpenPerSymbol = 1;
        Assert.Null(configuration.FindProblem());
        configuration.MaxOpenPerSymbol = null;
        Assert.Null(configuration.FindProblem());
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(126)]
    public void ALeverageLimitOutsideOneToBinancesHighestIsAProblem(double leverage)
    {
        var configuration = Valid();
        configuration.MaxLeverage = (decimal)leverage;

        Assert.NotNull(configuration.FindProblem());
    }

    [Fact]
    public void TheSettingsAreWhatTheValidatorReads()
    {
        var configuration = Valid();
        configuration.MaxOpenPerSymbol = 3;

        var settings = configuration.ToSettings();

        Assert.True(settings.Enabled);
        Assert.Equal(TimeSpan.FromHours(1), settings.MaxSignalAge);
        Assert.Equal(0.25m, settings.MaxMoveOfStopDistance);
        Assert.Equal(3, settings.MaxOpenPerSymbol);
        Assert.Equal(20m, settings.MaxLeverage);
    }

    [Fact]
    public void ASwitchedOffStrategyGivesDisabledSettings()
    {
        var configuration = Valid();
        configuration.IsEnabled = false;

        Assert.False(configuration.ToSettings().Enabled);
    }
}
