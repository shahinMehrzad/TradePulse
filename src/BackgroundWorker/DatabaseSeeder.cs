using Application.Services;
using Domain.Enums;
using Domain.Repositories;
using Infrastructure.Configurations;
using Microsoft.Extensions.Options;

namespace BackgroundWorker;

public class DatabaseSeeder(IOptions<BinanceExchangeConfigurations> config, ICandleRepository candleRepository)
{
    private readonly BinanceExchangeConfigurations _config = config.Value;
    private readonly ICandleRepository _candleRepository = candleRepository;

    public async Task InitializeBinanceAsync(CancellationToken cancellationToken = default)
    {
        foreach (var symbolConfig in _config.Symbols.Values)
        {
            var tableName = TableNameRegistry.GetCandleTableName(
                ExchangeEnum.Binance,
                symbolConfig.MarketType,
                symbolConfig.Name,
                symbolConfig.Interval
            );

            await _candleRepository.EnsureTableExistsAsync(tableName, cancellationToken);
        }
    }
}
