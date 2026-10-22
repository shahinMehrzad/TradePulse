using BackgroundWorker.CandleSeries;
using Infrastructure.Configurations;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackgroundWorker.UnitTests.CandleSeries;

internal sealed class FakeCandleSeriesSource : ICandleSeriesSource
{
    public List<BinanceSymbolConfigurations> Series { get; } = [];

    /// <summary>When set, every read throws it (the database being down).</summary>
    public Exception? Failure { get; set; }

    public int Calls { get; private set; }

    public Task<IReadOnlyList<BinanceSymbolConfigurations>> LoadAsync(CancellationToken cancellationToken)
    {
        Calls++;
        if (Failure is not null)
            throw Failure;

        return Task.FromResult<IReadOnlyList<BinanceSymbolConfigurations>>(Series.ToList());
    }
}

internal static class CandleSeriesTestSupport
{
    public static CandleSeriesProvider NewProvider(FakeCandleSeriesSource source, TimeProvider time) =>
        new(source, time, NullLogger<CandleSeriesProvider>.Instance);

    /// <summary>A provider that has already read the given series (started, and waited until they were known).</summary>
    public static async Task<CandleSeriesProvider> LoadedProviderAsync(TimeProvider time, params BinanceSymbolConfigurations[] series)
    {
        var source = new FakeCandleSeriesSource();
        source.Series.AddRange(series);
        var provider = NewProvider(source, time);

        await provider.StartAsync(CancellationToken.None);
        await provider.WaitForSeriesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CandleSeriesStatus.Loaded, provider.Status);
        return provider;
    }
}
