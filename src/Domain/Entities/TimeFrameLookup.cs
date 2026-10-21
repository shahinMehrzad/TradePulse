namespace Domain.Entities;

/// <summary>Reference row for <c>TimeFrameEnum</c>. <see cref="Id"/> is the enum's numeric value, which is also its length in seconds.</summary>
public class TimeFrameLookup
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int IntervalToSeconds { get; set; }
}
