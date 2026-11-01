namespace Application.Research;

/// <summary>One closed 1 minute candle, reduced to what the event detector looks at.</summary>
public readonly record struct MinuteBar(DateTime OpenTime, decimal High, decimal Low, decimal Close, decimal Volume);
