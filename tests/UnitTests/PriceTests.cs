using Domain.ValueObjects;

namespace UnitTests;

public class PriceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.00000001)]
    [InlineData(65432.12345678)]
    public void Constructor_AcceptsZeroAndPositiveValues(double value)
    {
        var price = new Price((decimal)value);

        Assert.Equal((decimal)value, price.Value);
    }

    [Fact]
    public void Constructor_RejectsNegativeValues()
    {
        var ex = Assert.Throws<ArgumentException>(() => new Price(-0.01m));

        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void ImplicitConversion_ReturnsUnderlyingDecimal()
    {
        decimal asDecimal = new Price(42.5m);

        Assert.Equal(42.5m, asDecimal);
    }

    [Fact]
    public void ExplicitConversion_ValidatesValue()
    {
        Assert.Throws<ArgumentException>(() => (Price)(-1m));
    }

    [Fact]
    public void Equality_IsBasedOnValue()
    {
        Assert.Equal(new Price(10m), new Price(10m));
        Assert.NotEqual(new Price(10m), new Price(11m));
    }
}
