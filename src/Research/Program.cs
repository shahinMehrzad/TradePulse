using System.Globalization;
using Application;
using Application.Interfaces;
using Application.Research;
using Domain.Enums;
using Domain.Repositories;
using Domain.ValueObjects;
using Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// Run-and-exit analysis of the stored market data (not a service):
//   dotnet run --project src/Research -- detect --symbol BTCUSDT --from 2026-01-01 [--to 2026-11-01]
//   dotnet run --project src/Research -- list   --symbol BTCUSDT --from 2026-01-01 [--to 2026-11-01]
// Connection strings come from user secrets or the environment (ConnectionStrings__DefaultConnection), like the other programs.
// Only Binance Futures is looked at for now.

if (args is not [("detect" or "list") and var command, ..var options])
{
    Console.Error.WriteLine("Usage: Research <detect|list> --symbol BTCUSDT --from yyyy-MM-dd [--to yyyy-MM-dd]");
    return 2;
}

var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i + 1 < options.Length; i += 2)
    values[options[i].TrimStart('-')] = options[i + 1];

if (!values.TryGetValue("symbol", out var symbolName) || !TryParseDay(values.GetValueOrDefault("from"), out var from))
{
    Console.Error.WriteLine("--symbol and --from (yyyy-MM-dd, UTC) are required.");
    return 2;
}

var to = DateTime.UtcNow;
if (values.TryGetValue("to", out var toText) && !TryParseDay(toText, out to))
{
    Console.Error.WriteLine("--to must be yyyy-MM-dd (UTC).");
    return 2;
}

var configuration = new ConfigurationBuilder()
    .AddUserSecrets(typeof(Program).Assembly, optional: true)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddInfrastructureServices(configuration);
services.AddApplicationServices();

await using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

var listings = await scope.ServiceProvider.GetRequiredService<ISymbolCatalogRepository>().GetListings(cancellation.Token);
var listing = listings.FirstOrDefault(l =>
    l.ExchangeId == (int)ExchangeEnum.Binance && l.MarketId == (int)MarketTypeEnum.Futures
    && string.Equals(l.ExchangeSymbol, symbolName, StringComparison.OrdinalIgnoreCase));

if (listing is null)
{
    Console.Error.WriteLine($"{symbolName} is not listed on Binance Futures.");
    return 1;
}

if (command == "detect")
{
    var result = await scope.ServiceProvider.GetRequiredService<ICommandDispatcher>()
        .SendAsync<DetectMarketEventsCommand, DetectMarketEventsResult>(
            new DetectMarketEventsCommand(ExchangeEnum.Binance, MarketTypeEnum.Futures, new ExchangeSymbol(listing.ExchangeSymbol), listing.Id, from, to),
            cancellation.Token);

    Console.WriteLine($"Read {result.CandlesRead} candles, found {result.Found} events, stored {result.Added} new.");
    return 0;
}

var events = await scope.ServiceProvider.GetRequiredService<IMarketEventStore>().ListAsync(listing.Id, from, to, cancellation.Token);
Console.WriteLine("started (UTC)         dir   5m move   z      volume x  next 5m   next 60m");
foreach (var e in events)
{
    var five = e.Reactions.FirstOrDefault(r => r.HorizonMinutes == 5)?.Return;
    var sixty = e.Reactions.FirstOrDefault(r => r.HorizonMinutes == 60)?.Return;
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{e.StartedAt:yyyy-MM-dd HH:mm}  {e.Direction,-5} {e.FiveMinuteReturn,8:P2} {e.ZScore,6:F1} {e.VolumeRatio,9:F1}  {Percent(five),8} {Percent(sixty),9}"));
}

Console.WriteLine($"{events.Count} events.");
return 0;

static bool TryParseDay(string? text, out DateTime day)
{
    var parsed = DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out day);

    // The tables hold timestamps without a zone (always UTC).
    day = DateTime.SpecifyKind(day, DateTimeKind.Unspecified);
    return parsed;
}

static string Percent(decimal? value) => value is { } v ? v.ToString("P2", CultureInfo.InvariantCulture) : "-";
