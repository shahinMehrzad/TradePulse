using Application.Dtos;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Commands.Candles;

public record SaveCandlesCommand(ExchangeEnum Exchange, MarketTypeEnum MarketType, ExchangeSymbol Symbol, TimeFrameEnum Timeframe, IEnumerable<CandleDto> Candles);
