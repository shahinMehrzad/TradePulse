using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Infrastructure.Configurations;
using Microsoft.Extensions.Options;

namespace BackgroundWorker;

public class DatabaseSeeder(
    IOptions<BinanceExchangeConfigurations> config,
    IOptions<BinanceFuturesDataConfigurations> futuresConfig,
    ICandleRepository candleRepository,
    IMarketDataRepository<FundingRate> fundingRateRepository,
    IMarketDataRepository<OpenInterest> openInterestRepository,
    IMarketDataRepository<Liquidation> liquidationRepository)
{
    private readonly BinanceExchangeConfigurations _config = config.Value;
    private readonly BinanceFuturesDataConfigurations _futuresConfig = futuresConfig.Value;

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

            await candleRepository.EnsureTableExistsAsync(tableName, cancellationToken);
        }

        foreach (var funding in _futuresConfig.FundingRates.Values)
        {
            var tableName = TableNameRegistry.GetFundingRateTableName(ExchangeEnum.Binance, funding.Name);
            await fundingRateRepository.EnsureTableExistsAsync(tableName, cancellationToken);
        }

        foreach (var openInterest in _futuresConfig.OpenInterests.Values)
        {
            var tableName = TableNameRegistry.GetOpenInterestTableName(ExchangeEnum.Binance, openInterest.Name, openInterest.Interval);
            await openInterestRepository.EnsureTableExistsAsync(tableName, cancellationToken);
        }

        foreach (var liquidation in _futuresConfig.Liquidations.Values)
        {
            var tableName = TableNameRegistry.GetLiquidationTableName(ExchangeEnum.Binance, liquidation.Name);
            await liquidationRepository.EnsureTableExistsAsync(tableName, cancellationToken);
        }
    }
}
