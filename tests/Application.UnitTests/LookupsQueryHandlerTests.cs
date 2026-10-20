using Application.Dtos.Lookups;
using Application.Interfaces;
using Application.Queries.Lookups;
using Domain.Entities;
using Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Application.UnitTests;

/// <summary>
/// Every query goes through the real dispatcher and container, the way the controller calls it. The dispatcher looks a handler
/// up by the exact (request, response) pair, so a mismatch between what the controller asks for and what a handler is
/// registered as only shows up at run time: these tests are the compile-time substitute.
/// </summary>
public class LookupsQueryHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeLookupsRepository _repository = new();

    private async Task<TResponse> SendAsync<TRequest, TResponse>(TRequest query)
    {
        var services = new ServiceCollection();
        services.AddApplicationServices();
        services.AddSingleton<ILookupsRepository>(_repository);

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandDispatcher>().SendAsync<TRequest, TResponse>(query, Ct);
    }

    [Fact]
    public async Task ExchangesComeFromTheRepository()
    {
        _repository.Exchanges.Add(new ExchangeLookup { Id = 1, Name = "Binance" });

        var result = await SendAsync<GetExchangesQuery, IEnumerable<ExchangesResponseDto>>(new GetExchangesQuery());

        Assert.Equal("Binance", Assert.Single(result).Name);
    }

    [Fact]
    public async Task MarketsComeFromTheRepository()
    {
        _repository.Markets.Add(new MarketLookup { Id = 1, Name = "Spot" });
        _repository.Markets.Add(new MarketLookup { Id = 2, Name = "Futures" });

        var result = await SendAsync<GetMarketsQuery, IEnumerable<MarketsResponseDto>>(new GetMarketsQuery());

        Assert.Equal(["Spot", "Futures"], result.Select(m => m.Name));
    }

    [Theory]
    [InlineData("btc")]
    [InlineData(null)]
    public async Task SymbolsPassTheKeywordDown(string? keyword)
    {
        _repository.Symbols.Add(new SymbolLookup { Id = 1, Name = "BTCUSDT" });

        var result = await SendAsync<GetSymbolsQuery, IEnumerable<SymbolsResponseDto>>(new GetSymbolsQuery(keyword));

        Assert.Equal("BTCUSDT", Assert.Single(result).Name);
        Assert.Equal(keyword, _repository.LastKeyword);
    }

    [Theory]
    [InlineData(0, int.MaxValue)]
    [InlineData(3600, int.MaxValue)]
    [InlineData(3600, 14400)]
    [InlineData(0, 900)]
    public async Task TimeFramesPassTheRangeDownUnchanged(int min, int max)
    {
        _repository.TimeFrames.Add(new TimeFrameLookup { Id = 3600, Name = "_1H", IntervalToSeconds = 3600 });

        var result = await SendAsync<GetTimeFramesQuery, IEnumerable<TimeFramesResponseDto>>(new GetTimeFramesQuery(min, max));

        Assert.Single(result);
        Assert.Equal((min, max), _repository.LastRange);
    }
}
