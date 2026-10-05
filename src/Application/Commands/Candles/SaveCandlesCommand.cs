using Application.Dtos;
using Domain.Enums;

namespace Application.Commands.Candles;

public record SaveCandlesCommand(ExchangeEnum Exchange, MarketTypeEnum MarketType, SymbolEnum Symbol, TimeFrameEnum Timeframe, IEnumerable<CandleDto> Candles);
