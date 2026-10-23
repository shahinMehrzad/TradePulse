namespace Application.Dtos.Lookups;

/// <summary>One kind of data an exchange market provides.</summary>
/// <param name="DataType">The name of a <c>MarketDataTypeEnum</c> member (Candle, FundingRate, OpenInterest, Liquidation).</param>
/// <param name="TimeFrameIds">The time frames it is available for (ids are the enum values); null when the data has no time frame.</param>
public record MarketDataSupportResponseDto(int ExchangeId, int MarketId, string DataType, IReadOnlyList<int>? TimeFrameIds);
