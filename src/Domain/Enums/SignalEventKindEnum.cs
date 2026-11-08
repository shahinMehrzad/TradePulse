namespace Domain.Enums;

/// <summary>What a row of the signal log records. Stored as a number: only add members, never renumber.</summary>
public enum SignalEventKindEnum : byte
{
    Received = 1,
    Rejected = 2,
    Opened = 3,
    Closed = 4,
    MessageSent = 5,
    MessageEdited = 6,
    MessageFailed = 7
}
