using Domain.Extensions;

namespace Domain.UnitTests;

public class DateTimeExtensionsTests
{
    private static readonly DateTime Midnight = new(2020, 1, 1, 0, 0, 0);

    [Fact]
    public void AUtcDateStaysTheSameInstant()
    {
        var result = DateTime.SpecifyKind(Midnight, DateTimeKind.Utc).AsUtc();

        Assert.Equal((Midnight, DateTimeKind.Utc), (result, result.Kind));
    }

    [Fact]
    public void ADateWithoutAnOffsetIsTakenToBeUtcNotShiftedByTheMachinesZone()
    {
        var result = DateTime.SpecifyKind(Midnight, DateTimeKind.Unspecified).AsUtc();

        Assert.Equal((Midnight, DateTimeKind.Utc), (result, result.Kind));
    }

    [Fact]
    public void ALocalDateIsConvertedToTheSameInstantInUtc()
    {
        var local = DateTime.SpecifyKind(Midnight, DateTimeKind.Utc).ToLocalTime();

        var result = local.AsUtc();

        Assert.Equal((Midnight, DateTimeKind.Utc), (result, result.Kind));
    }
}
