using Domain.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Infrastructure.Configurations;
using Microsoft.Extensions.Options;

namespace BackgroundWorker.Collection;

public class FuturesDataTableInitializer(
    IOptions<BinanceFuturesDataConfigurations> futuresConfig,
    IMarketDataRepository<FundingRate> fundingRateRepository,
    IMarketDataRepository<Liquidation> liquidationRepository)
{
    private readonly BinanceFuturesDataConfigurations _futuresConfig = futuresConfig.Value;

    public async Task EnsureTablesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var funding in _futuresConfig.FundingRates.Values)
        {
            var tableName = TableNameRegistry.GetFundingRateTableName(ExchangeEnum.Binance, funding.Name);
            await fundingRateRepository.EnsureTableExistsAsync(tableName, cancellationToken);
        }

        foreach (var liquidation in _futuresConfig.Liquidations.Values)
        {
            var tableName = TableNameRegistry.GetLiquidationTableName(ExchangeEnum.Binance, liquidation.Name);
            await liquidationRepository.EnsureTableExistsAsync(tableName, cancellationToken);
        }
    }
}
