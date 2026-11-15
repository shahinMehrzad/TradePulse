using Application.Exchange;
using Domain.Enums;
using Domain.Services;
using Domain.ValueObjects;
using Infrastructure.Exchange.Ctrader;
using Infrastructure.Exchange.Ctrader.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CtraderCheck;

/// <summary>
/// Looks at what the broker really does, through the adapter the collector uses, and prints what the collector's assumptions rest on: the symbol,
/// its trading hours, candles, whether each period starts on the epoch grid, how long a range one request may ask for, and how far back ticks go.
/// Read only; prints no secret.
/// </summary>
internal static class CheckCommand
{
    private static readonly (string Name, ProtoOATrendbarPeriod Period, TimeSpan Length)[] Periods =
    [
        ("M1", ProtoOATrendbarPeriod.M1, TimeSpan.FromMinutes(1)), ("M5", ProtoOATrendbarPeriod.M5, TimeSpan.FromMinutes(5)),
        ("M15", ProtoOATrendbarPeriod.M15, TimeSpan.FromMinutes(15)), ("M30", ProtoOATrendbarPeriod.M30, TimeSpan.FromMinutes(30)),
        ("H1", ProtoOATrendbarPeriod.H1, TimeSpan.FromHours(1)), ("H4", ProtoOATrendbarPeriod.H4, TimeSpan.FromHours(4)),
        ("H12", ProtoOATrendbarPeriod.H12, TimeSpan.FromHours(12)), ("D1", ProtoOATrendbarPeriod.D1, TimeSpan.FromDays(1)),
    ];

    public static async Task<int> RunAsync(CliOptions options)
    {
        var settings = new ConfigurationBuilder().AddUserSecrets(Secrets.Collector).AddEnvironmentVariables()
            .Build().GetSection(CtraderExchangeConfigurations.SectionName).Get<CtraderExchangeConfigurations>() ?? new();
        if (!settings.IsConfigured)
            throw new InvalidOperationException("cTrader is not configured: run `token` first (or set CtraderExchangeConfigurations__* in the environment).");

        var name = options.Get("symbol", "EURUSD");
        var days = int.Parse(options.Get("days", "14"));
        var now = DateTime.UtcNow;

        await using var connection = new CtraderConnection(Options.Create(settings), NullLogger<CtraderConnection>.Instance);
        var calendar = new MarketCalendar();
        var directory = new CtraderSymbolDirectory(connection, TimeProvider.System, NullLogger<CtraderSymbolDirectory>.Instance, calendar);
        var gateway = new CtraderCandleGateway(connection, directory, TimeProvider.System);

        Console.WriteLine($"== Connection ({settings.Environment}, account {settings.AccountId})");
        var probe = await new CtraderExchangeProbe(connection).PingAsync(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, CancellationToken.None);
        Console.WriteLine(probe.Success ? $"signed in, a request took {probe.Value.TotalMilliseconds:F0} ms" : $"FAILED: {probe.Error}");
        if (!probe.Success)
            return 1;

        Console.WriteLine();
        Console.WriteLine($"== Symbol {name}");
        var symbol = await directory.FindAsync(new ExchangeSymbol(name), CancellationToken.None);
        if (symbol is null)
        {
            Console.WriteLine("not listed by the broker. Names that contain it:");
            var all = await connection.RequestAsync<ProtoOASymbolsListRes>(ProtoOAPayloadType.ProtoOaSymbolsListReq,
                new ProtoOASymbolsListReq { CtidTraderAccountId = connection.AccountId }, false, CancellationToken.None);
            foreach (var near in all.Symbol.Where(s => s.SymbolName.Contains(name[..Math.Min(3, name.Length)], StringComparison.OrdinalIgnoreCase)).Take(15))
                Console.WriteLine($"  {near.SymbolName}");
            return 1;
        }

        var full = (await connection.RequestAsync<ProtoOASymbolByIdRes>(ProtoOAPayloadType.ProtoOaSymbolByIdReq,
            new ProtoOASymbolByIdReq { CtidTraderAccountId = connection.AccountId, SymbolId = { symbol.Id } }, false, CancellationToken.None)).Symbol.Single();
        Console.WriteLine($"id {symbol.Id}, name {symbol.Name}, enabled {symbol.Enabled}, digits {full.Digits}, time zone '{full.ScheduleTimeZone}', {full.Holiday.Count} holiday entries");
        Console.WriteLine("weekly sessions (as the broker gives them, in its zone):");
        foreach (var session in full.Schedule)
            Console.WriteLine($"  {Moment(session.StartSecond)} -> {Moment(session.EndSecond)}");

        var series = new CandleSeries(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, new ExchangeSymbol(symbol.Name), TimeFrameEnum._1H);
        var digits = await directory.GetDigitsAsync(symbol, CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"== Candles of the last {days} days through the collector's gateway (H1)");
        var from = new DateTime(now.AddDays(-days).Ticks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour, DateTimeKind.Utc);
        var candles = await FetchAllAsync(gateway, series, from);
        Console.WriteLine($"{candles.Count} candles from {candles.FirstOrDefault()?.TimeRange.OpenTime:u} to {candles.LastOrDefault()?.TimeRange.OpenTime:u} (digits {digits})");
        foreach (var candle in candles.TakeLast(3))
            Console.WriteLine($"  {candle.TimeRange.OpenTime:u}  O {candle.Open.Value}  H {candle.High.Value}  L {candle.Low.Value}  C {candle.Close.Value}  volume {candle.Volume}");

        Console.WriteLine();
        Console.WriteLine("== Does the trading calendar explain the holes?");
        var opens = candles.Select(c => c.TimeRange.OpenTime).ToHashSet();
        var expectedParts = calendar.ExpectedPart(series, new CandleGap(from, new DateTime(now.Ticks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour, DateTimeKind.Utc).AddHours(-1)));
        var expected = expectedParts.SelectMany(p => Hours(p.FirstMissingOpenTime, p.LastMissingOpenTime)).ToList();
        var missing = expected.Where(t => !opens.Contains(t)).ToList();
        var unexpected = opens.Where(t => !expected.Contains(t)).Order().ToList();
        Console.WriteLine($"the calendar expects {expected.Count} candles; {missing.Count} of them are missing, and {unexpected.Count} candles exist where the calendar says the market was closed");
        foreach (var t in missing.Take(10))
            Console.WriteLine($"  missing {t:u}");
        foreach (var t in unexpected.Take(10))
            Console.WriteLine($"  outside the hours {t:u}");

        Console.WriteLine();
        Console.WriteLine("== Do the periods start on the epoch grid? (the collector assumes they do)");
        foreach (var (periodName, period, length) in Periods)
        {
            var answer = await connection.RequestAsync<ProtoOAGetTrendbarsRes>(ProtoOAPayloadType.ProtoOaGetTrendbarsReq, new ProtoOAGetTrendbarsReq
            {
                CtidTraderAccountId = connection.AccountId, SymbolId = symbol.Id, Period = period,
                FromTimestamp = Ms(now.AddDays(-Math.Min(days, 10))), ToTimestamp = Ms(now),
            }, true, CancellationToken.None);

            var offsets = answer.Trendbar
                .Select(b => TimeSpan.FromMinutes(b.UtcTimestampInMinutes) is var open ? TimeSpan.FromTicks(open.Ticks % length.Ticks) : default)
                .Distinct().Order().ToList();
            Console.WriteLine($"{periodName,-4} {answer.Trendbar.Count,5} bars  offsets from the epoch grid: {(offsets.Count == 0 ? "no bars" : string.Join(", ", offsets.Select(o => o == TimeSpan.Zero ? "none" : o.ToString())))}"
                + (offsets.Any(o => o != TimeSpan.Zero) ? "   <-- NOT on the grid" : string.Empty));
        }

        Console.WriteLine();
        Console.WriteLine("== How long a range may one request ask for? (what the adapter assumes is in parentheses)");
        foreach (var (periodName, period, assumed) in new[] { ("M1", ProtoOATrendbarPeriod.M1, 3.5), ("H1", ProtoOATrendbarPeriod.H1, 245.0) })
            foreach (var askDays in new[] { assumed / 2, assumed, assumed * 2 })
                await TryRangeAsync(connection, symbol.Id, periodName, period, now, askDays, assumed);

        Console.WriteLine();
        Console.WriteLine("== Ticks (for later): the last hour, and the oldest hour that answers");
        await TryTicksAsync(connection, symbol.Id, now.AddHours(-1), now);
        foreach (var back in new[] { 30, 180, 365 })
            await TryTicksAsync(connection, symbol.Id, now.AddDays(-back), now.AddDays(-back).AddHours(1));

        return 0;
    }

    private static async Task<List<Domain.Entities.Candle>> FetchAllAsync(CtraderCandleGateway gateway, CandleSeries series, DateTime from)
    {
        var all = new List<Domain.Entities.Candle>();
        for (var start = from; ;)
        {
            var result = await gateway.GetCandlesAsync(series, start, null, CancellationToken.None);
            if (!result.Success)
                throw new CtraderException(result.Error ?? "The candle request failed.");

            all.AddRange(result.Value.Items);
            if (!result.Value.HasMore || result.Value.Items.Count == 0)
                return all;

            start = result.Value.Items[^1].TimeRange.OpenTime + series.Interval;
        }
    }

    private static async Task TryRangeAsync(CtraderConnection connection, long symbolId, string periodName, ProtoOATrendbarPeriod period, DateTime now, double askDays, double assumedDays)
    {
        try
        {
            var answer = await connection.RequestAsync<ProtoOAGetTrendbarsRes>(ProtoOAPayloadType.ProtoOaGetTrendbarsReq, new ProtoOAGetTrendbarsReq
            {
                CtidTraderAccountId = connection.AccountId, SymbolId = symbolId, Period = period,
                FromTimestamp = Ms(now.AddDays(-askDays)), ToTimestamp = Ms(now),
            }, true, CancellationToken.None);
            Console.WriteLine($"{periodName} over {askDays,6:F1} days (assumed limit {assumedDays}): answered with {answer.Trendbar.Count} bars, hasMore {answer.HasMore}");
        }
        catch (CtraderException ex)
        {
            Console.WriteLine($"{periodName} over {askDays,6:F1} days (assumed limit {assumedDays}): refused: {ex.Message}");
        }
    }

    private static async Task TryTicksAsync(CtraderConnection connection, long symbolId, DateTime from, DateTime to)
    {
        try
        {
            var answer = await connection.RequestAsync<ProtoOAGetTickDataRes>(ProtoOAPayloadType.ProtoOaGetTickdataReq, new ProtoOAGetTickDataReq
            {
                CtidTraderAccountId = connection.AccountId, SymbolId = symbolId, Type = ProtoOAQuoteType.Bid, FromTimestamp = Ms(from), ToTimestamp = Ms(to),
            }, true, CancellationToken.None);
            Console.WriteLine($"{from:u} .. {to:u}: {answer.TickData.Count} bid ticks, hasMore {answer.HasMore}");
        }
        catch (CtraderException ex)
        {
            Console.WriteLine($"{from:u} .. {to:u}: refused: {ex.Message}");
        }
    }

    private static IEnumerable<DateTime> Hours(DateTime first, DateTime last)
    {
        for (var t = first; t <= last; t = t.AddHours(1))
            yield return t;
    }

    private static long Ms(DateTime time) => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static string Moment(uint secondsFromSunday)
    {
        var t = TimeSpan.FromSeconds(secondsFromSunday);
        return $"{(DayOfWeek)(t.Days % 7)} {t.Hours:00}:{t.Minutes:00}";
    }
}
