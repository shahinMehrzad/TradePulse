namespace Application.Dtos.CandleWorkerConfigurations;

public record struct BaseCandleWorkerConfigurationDto(int ExchangeId, int MarketId, int SymbolId, int TimeFrameId, DateTime StartDate);

