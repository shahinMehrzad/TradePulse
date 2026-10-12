using Domain.ValueObjects;

namespace Domain.UnitTests;

public class PriceTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("0.00000001")]
    [InlineData("67123.45678901")]
    public void Constructor_AcceptsZeroAndPositiveValues(string value)
    {
        var price = new Price(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), price.Value);
    }

    [Fact]
    public void Constructor_RejectsNegativeValues()
    {
        var ex = Assert.Throws<ArgumentException>(() => new Price(-0.01m));

        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void ConvertsToAndFromDecimal()
    {
        decimal asDecimal = new Price(10.5m);
        var asPrice = (Price)10.5m;

        Assert.Equal(10.5m, asDecimal);
        Assert.Equal(new Price(10.5m), asPrice);
    }

    [Fact]
    public void ExplicitConversion_RejectsNegativeValues()
    {
        Assert.Throws<ArgumentException>(() => (Price)(-1m));
    }
}
