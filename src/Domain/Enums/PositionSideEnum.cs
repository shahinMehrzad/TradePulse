namespace Domain.Enums;

/// <summary>Which way a position profits. Stored as a number: only add members, never renumber.</summary>
public enum PositionSideEnum : byte
{
    Long = 1,
    Short = 2
}
