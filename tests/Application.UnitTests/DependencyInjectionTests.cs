using Application.Commands.Candles;
using Application.Interfaces;
using Domain.Enums;
using Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Application.UnitTests;

public class DependencyInjectionTests
{
    private static ServiceProvider BuildProvider(FakeCandleRepository repository)
    {
        var services = new ServiceCollection();
        services.AddApplicationServices();
        services.AddSingleton<ICandleRepository>(repository);
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void RegistersEveryCommandHandlerUnderItsInterface()
    {
        using var provider = BuildProvider(new FakeCandleRepository());

        var handler = provider.GetRequiredService<ICommandHandler<SaveCandlesCommand, int>>();

        Assert.IsType<SaveCandlesCommandHandler>(handler);
    }

    [Fact]
    public async Task DispatcherRoutesACommandToItsHandler()
    {
        var repository = new FakeCandleRepository { InsertResult = 7 };
        using var provider = BuildProvider(repository);
        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var open = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var command = new SaveCandlesCommand(ExchangeEnum.Binance, MarketTypeEnum.Spot, SeededSymbol.BTCUSDT.Sym(), TimeFrameEnum._1D,
        [
            new(open, 1m, 2m, 1m, 2m, 3m, open.AddDays(1).AddMilliseconds(-1), 4m, 5, 1m, 2m)
        ]);

        var result = await dispatcher.SendAsync<SaveCandlesCommand, int>(command, TestContext.Current.CancellationToken);

        Assert.Equal(7, result);
        Assert.Equal("binance_spot_btcusdt_1d", Assert.Single(repository.Inserts).TableName);
    }

    [Fact]
    public async Task DispatcherFailsForCommandsWithoutAHandler()
    {
        using var provider = BuildProvider(new FakeCandleRepository());
        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.SendAsync<string, int>("unknown", TestContext.Current.CancellationToken));
    }
}
