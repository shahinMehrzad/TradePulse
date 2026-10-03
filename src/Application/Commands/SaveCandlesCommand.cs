using Application.Dtos;
using Domain.Enums;

namespace Application.Commands
{
    public record SaveCandlesCommand(ExchangeEnum Exchange, MarketTypeEnum MarketType, SymbolEnum Symbol, TimeFrameEnum Timeframe, IEnumerable<CandleDto> Candles);
}
