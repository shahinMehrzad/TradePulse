using BackgroundWorker.SymbolData;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackgroundWorker.UnitTests.SymbolData;

internal sealed class FakeSymbolDataSource : ISymbolDataSource
{
    public List<SymbolDataPlan> Plans { get; } = [];

    /// <summary>When set, every read throws it (the database being down).</summary>
    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<SymbolDataPlan>> LoadAsync(CancellationToken cancellationToken)
    {
        if (Failure is not null)
            throw Failure;

        return Task.FromResult<IReadOnlyList<SymbolDataPlan>>(Plans.ToList());
    }
}

internal static class SymbolDataTestSupport
{
    public static SymbolDataPlan Binance(
        SymbolEnum symbol = SymbolEnum.BTCUSDT, bool funding = true, bool liquidation = true, MarketTypeEnum market = MarketTypeEnum.Futures) =>
        new(ExchangeEnum.Binance, market, symbol, funding, funding ? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null, liquidation);

    public static SymbolDataProvider NewProvider(FakeSymbolDataSource source, TimeProvider time) =>
        new(source, time, NullLogger<SymbolDataProvider>.Instance);

    /// <summary>A provider that has already read the given plans (started, and waited until they were known).</summary>
    public static async Task<SymbolDataProvider> LoadedProviderAsync(TimeProvider time, params SymbolDataPlan[] plans)
    {
        var source = new FakeSymbolDataSource();
        source.Plans.AddRange(plans);
        var provider = NewProvider(source, time);

        await provider.StartAsync(CancellationToken.None);
        await provider.WaitForPlansAsync(TestContext.Current.CancellationToken);
        return provider;
    }
}
