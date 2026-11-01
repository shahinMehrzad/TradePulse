using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Research;

/// <summary>Finds the events in the 1 minute candles of a listing between two UTC times and stores the ones not stored yet.</summary>
public record DetectMarketEventsCommand(
    ExchangeEnum Exchange, MarketTypeEnum MarketType, ExchangeSymbol Symbol, int SymbolListingId, DateTime From, DateTime To);

public readonly record struct DetectMarketEventsResult(int CandlesRead, int Found, int Added);
