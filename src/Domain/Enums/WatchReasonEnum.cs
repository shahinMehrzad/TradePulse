namespace Domain.Enums;

/// <summary>Why a symbol is on the watch list. Numeric values are stored: only add, never renumber.</summary>
public enum WatchReasonEnum
{
    /// <summary>The admin added it to be watched. Only the admin removes it.</summary>
    Manual = 1,

    /// <summary>Put there because the symbol is enabled for trading. It goes when the symbol stops being tradable.</summary>
    Trading = 2,
}
