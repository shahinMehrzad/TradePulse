namespace Domain.Enums;

/// <summary>How strong a <see cref="Entities.ShockAlert"/> is. Numeric values are stored: only add, never renumber.</summary>
public enum AlertLevelEnum
{
    /// <summary>A move past the lower threshold: the earliest sign, worth a look.</summary>
    Attention = 1,

    /// <summary>A move past the higher threshold as well.</summary>
    Alarm = 2,
}
