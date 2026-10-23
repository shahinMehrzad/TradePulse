using Application.Dtos.Lookups;
using Application.Interfaces;
using Domain.Enums;
using Domain.Services;

namespace Application.Queries.Lookups;

public readonly record struct GetMarketDataSupportQuery();

/// <summary>
/// What each exchange market can provide, for the admin panel to offer only choices the API would accept. It is
/// <see cref="MarketDataSupport"/> itself, listed, so the panel never keeps a copy of the rules.
/// </summary>
public sealed class GetMarketDataSupportQueryHandler : ICommandHandler<GetMarketDataSupportQuery, IReadOnlyList<MarketDataSupportResponseDto>>
{
    public Task<IReadOnlyList<MarketDataSupportResponseDto>> HandleAsync(GetMarketDataSupportQuery request, CancellationToken cancellationToken)
    {
        var supported = new List<MarketDataSupportResponseDto>();

        foreach (var exchange in Enum.GetValues<ExchangeEnum>())
            foreach (var market in Enum.GetValues<MarketTypeEnum>())
                foreach (var dataType in Enum.GetValues<MarketDataTypeEnum>())
                {
                    // Data without a time frame is supported with none given; data split by time frame needs one.
                    if (MarketDataSupport.IsSupported(exchange, market, dataType))
                    {
                        supported.Add(new((int)exchange, (int)market, dataType.ToString(), TimeFrameIds: null));
                        continue;
                    }

                    var timeFrames = MarketDataSupport.GetTimeFrames(exchange, market, dataType);
                    if (timeFrames.Count > 0)
                        supported.Add(new((int)exchange, (int)market, dataType.ToString(), [.. timeFrames.Select(t => (int)t)]));
                }

        return Task.FromResult<IReadOnlyList<MarketDataSupportResponseDto>>(supported);
    }
}
