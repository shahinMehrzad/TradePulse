using System.Runtime.InteropServices;
using Application;
using Application.Notifications;
using Application.Research.Watch;
using Domain.Enums;
using Domain.Repositories;
using Domain.ValueObjects;
using Infrastructure;
using Infrastructure.Exchange.Binance;
using Infrastructure.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Research;

/// <summary>
/// <c>watch</c>: follows the live trades and tells the admin about sudden moves. Runs until Ctrl+C or SIGTERM. It is its own process, apart from
/// the collector, so nothing it does can slow the collector down.
/// <para>
/// Which symbols: the ones switched on in the admin panel (the watch list in the database), looked at again every 30 seconds, so
/// the admin changes them without restarting anything. <c>--symbols BTCUSDT[,ETHUSDT] [--market futures|spot]</c> gives a fixed list
/// instead, for trying it out by hand; the panel's list is then not read.
/// </para>
/// <para>
/// Settings (user secrets or environment, never appsettings): the rule under <c>Watch:Rule</c> (every field of
/// <see cref="ShockWatchOptions"/>), the alert channels under <c>Watch:Alerts</c> with the same shape as the collector's
/// <c>Alerts</c> but their own bot, so the watch's messages do not mix with the collector's. Without a channel the alerts only
/// go to the console.
/// </para>
/// </summary>
internal static class WatchCommand
{
    private const string Usage = "Usage: Research watch [--symbols BTCUSDT[,ETHUSDT] [--market futures|spot]]";

    public static async Task<int> RunAsync(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
            values[args[i].TrimStart('-')] = args[i + 1];

        // Without --symbols the list is the panel's. A --symbols with nothing in it is a mistake, not "the panel's list".
        var names = values.GetValueOrDefault("symbols")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (args.Length % 2 != 0 || (values.ContainsKey("symbols") && names is not { Length: > 0 }))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var market = values.GetValueOrDefault("market", "futures").ToLowerInvariant() switch
        {
            "futures" => MarketTypeEnum.Futures,
            "spot" => MarketTypeEnum.Spot,
            _ => (MarketTypeEnum?)null,
        };
        if (market is null)
        {
            Console.Error.WriteLine("--market must be futures or spot.");
            return 2;
        }

        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(typeof(WatchCommand).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

        var rule = configuration.GetSection("Watch:Rule").Get<ShockWatchOptions>() ?? new ShockWatchOptions();

        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            // UTC like the alerts, and without a line for every database command.
            logging.AddSimpleConsole(console =>
            {
                console.TimestampFormat = "HH:mm:ss ";
                console.UseUtcTimestamp = true;
            });
            logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        });
        services.AddInfrastructureServices(configuration);
        services.AddBinanceExchange(configuration);
        services.AddNotifiers(configuration.GetSection("Watch"));
        services.AddSingleton<INotifier, ConsoleNotifier>();
        services.AddSingleton(rule);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ShockWatcher>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        // `docker stop` sends SIGTERM, not Ctrl+C: end the same way, so the subscriptions are closed and the alerts in flight are sent.
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });

        IWatchedSymbolSource source;
        string following;
        if (names is { Length: > 0 })
        {
            var listings = await scope.ServiceProvider.GetRequiredService<ISymbolCatalogRepository>().GetListings(cancellation.Token);
            var watched = new List<WatchedSymbol>();
            foreach (var name in names)
            {
                var listing = listings.FirstOrDefault(l =>
                    l.ExchangeId == (int)ExchangeEnum.Binance && l.MarketId == (int)market
                    && string.Equals(l.ExchangeSymbol, name, StringComparison.OrdinalIgnoreCase));
                if (listing is null)
                {
                    Console.Error.WriteLine($"{name} is not listed on Binance {market}.");
                    return 1;
                }

                watched.Add(new WatchedSymbol(ExchangeEnum.Binance, market.Value, listing.Id, new ExchangeSymbol(listing.ExchangeSymbol)));
            }

            source = new FixedWatchedSymbols(watched);
            following = $"{string.Join(", ", watched.Select(w => w.Symbol.Value))} on Binance {market} (fixed list)";
        }
        else
        {
            source = scope.ServiceProvider.GetRequiredService<IWatchedSymbolSource>();
            following = "the symbols switched on in the admin panel";
        }

        await TellStartedAsync(scope.ServiceProvider.GetServices<INotifier>(), following, rule);

        await scope.ServiceProvider.GetRequiredService<ShockWatcher>().RunAsync(source, cancellation.Token);
        return 0;
    }

    /// <summary>One message when the watch starts, so the admin sees restarts too. A channel that fails is skipped.</summary>
    private static async Task TellStartedAsync(IEnumerable<INotifier> notifiers, string following, ShockWatchOptions rule)
    {
        var message = ShockAlertMessage.Started(following, rule);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var notifier in notifiers)
        {
            try
            {
                await notifier.SendAsync(message, timeout.Token);
            }
            catch (Exception ex)
            {
                // A NotificationException is meant to be told (it has no token in it); any other text may carry the address it called.
                Console.Error.WriteLine($"{notifier.Name} could not send the start message ({(ex is NotificationException ? ex.Message : ex.GetType().Name)}).");
            }
        }
    }
}
