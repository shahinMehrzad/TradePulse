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
        // --at looks at the days before another date (a summer day, the week the clocks changed): what the broker did then, not today.
        var at = options.Values.TryGetValue("at", out var atText)
            ? DateTime.SpecifyKind(DateTime.Parse(atText, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc)
            : (DateTime?)null;
        var now = at ?? DateTime.UtcNow;
        var timeFrame = options.Get("timeframe", "1h").ToLowerInvariant() switch
        {
            "1m" => TimeFrameEnum._1M, "3m" => TimeFrameEnum._3M, "5m" => TimeFrameEnum._5M,
            "15m" => TimeFrameEnum._15M, "30m" => TimeFrameEnum._30M, "1h" => TimeFrameEnum._1H,
            var other => throw new ArgumentException($"--timeframe {other} is not one of 1m, 3m, 5m, 15m, 30m, 1h."),
        };
        var interval = TimeSpan.FromSeconds((int)timeFrame);
        var lastClosed = Floor(now, interval) - interval;

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
        Console.WriteLine("holidays (date, recurring each year, hours of that day the market is shut, zone):");
        foreach (var holiday in full.Holiday)
            Console.WriteLine($"  {DateTime.UnixEpoch.AddDays(holiday.HolidayDate):yyyy-MM-dd}  {holiday.Name,-24} recurring {holiday.IsRecurring,-5}  {Clock(holiday.StartSecond)} -> {Clock(holiday.EndSecond)}  {holiday.ScheduleTimeZone}");

        var series = new CandleSeries(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, new ExchangeSymbol(symbol.Name), timeFrame);
        var digits = await directory.GetDigitsAsync(symbol, CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"== Candles of the {days} days before {now:u} through the collector's gateway ({timeFrame.ToString().TrimStart('_')})");
        var from = Floor(now.AddDays(-days), interval);
        var candles = await FetchAllAsync(gateway, series, from, lastClosed);
        Console.WriteLine($"{candles.Count} candles from {candles.FirstOrDefault()?.TimeRange.OpenTime:u} to {candles.LastOrDefault()?.TimeRange.OpenTime:u} (digits {digits})");
        foreach (var candle in candles.TakeLast(3))
            Console.WriteLine($"  {candle.TimeRange.OpenTime:u}  O {candle.Open.Value}  H {candle.High.Value}  L {candle.Low.Value}  C {candle.Close.Value}  volume {candle.Volume}");

        Console.WriteLine();
        Console.WriteLine("== Does the trading calendar explain the holes?");
        var opens = candles.Select(c => c.TimeRange.OpenTime).ToHashSet();
        var expectedHint = interval < TimeSpan.FromHours(1) ? " (every candle of a day is listed, so use a short --days)" : string.Empty;
        var expectedParts = calendar.ExpectedPart(series, new CandleGap(from, lastClosed));
        var expected = expectedParts.SelectMany(p => Steps(p.FirstMissingOpenTime, p.LastMissingOpenTime, interval)).ToList();
        var missing = expected.Where(t => !opens.Contains(t)).ToList();
        var unexpected = opens.Where(t => !expected.Contains(t)).Order().ToList();
        Console.WriteLine($"the calendar expects {expected.Count} candles; {missing.Count} of them are missing, and {unexpected.Count} candles exist where the calendar says the market was closed{expectedHint}");
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

            // A few real open times of the long periods, to see where a day starts (and whether it follows the clock of the broker).
            if (length >= TimeSpan.FromHours(4))
                Console.WriteLine("       e.g. " + string.Join("  ", answer.Trendbar.Take(4).Select(b => $"{Open(b):ddd HH:mm}")));
        }

        Console.WriteLine();
        Console.WriteLine("== How long a range may one request ask for? (the adapter asks for 3.5 days of M1 and 245 days of H1 at a time)");
        foreach (var (periodName, period, ranges) in new[]
        {
            ("M1", ProtoOATrendbarPeriod.M1, new[] { 7.0, 14, 30, 60 }),
            ("H1", ProtoOATrendbarPeriod.H1, new[] { 245.0, 490, 1000, 2000 }),
        })
            foreach (var askDays in ranges)
                await TryRangeAsync(connection, symbol.Id, periodName, period, now, askDays);

        Console.WriteLine();
        Console.WriteLine("== Ticks (for later): the last hour, and how far back they go (a weekday at noon UTC each time: the weekend has none)");
        if (at is null)
            await TryTicksAsync(connection, symbol.Id, now.AddHours(-1), now);
        foreach (var back in new[] { 30, 90, 180, 365, 730 })
        {
            var day = Weekday(now.AddDays(-back));
            await TryTicksAsync(connection, symbol.Id, day, day.AddHours(1));
        }

        return 0;
    }

    private static async Task<List<Domain.Entities.Candle>> FetchAllAsync(CtraderCandleGateway gateway, CandleSeries series, DateTime from, DateTime to)
    {
        var all = new List<Domain.Entities.Candle>();
        for (var start = from; ;)
        {
            var result = await gateway.GetCandlesAsync(series, start, to, CancellationToken.None);
            if (!result.Success)
                throw new CtraderException(result.Error ?? "The candle request failed.");

            all.AddRange(result.Value.Items);
            if (!result.Value.HasMore || result.Value.Items.Count == 0)
                return all;

            start = result.Value.Items[^1].TimeRange.OpenTime + series.Interval;
        }
    }

    private static async Task TryRangeAsync(CtraderConnection connection, long symbolId, string periodName, ProtoOATrendbarPeriod period, DateTime now, double askDays)
    {
        var from = now.AddDays(-askDays);
        try
        {
            var answer = await connection.RequestAsync<ProtoOAGetTrendbarsRes>(ProtoOAPayloadType.ProtoOaGetTrendbarsReq, new ProtoOAGetTrendbarsReq
            {
                CtidTraderAccountId = connection.AccountId, SymbolId = symbolId, Period = period, FromTimestamp = Ms(from), ToTimestamp = Ms(now),
            }, true, CancellationToken.None);

            // A range that is too long may be cut silently at either end, so say where the bars begin and end.
            var first = answer.Trendbar.Count > 0 ? Open(answer.Trendbar.First()) : (DateTime?)null;
            var last = answer.Trendbar.Count > 0 ? Open(answer.Trendbar.Last()) : (DateTime?)null;
            Console.WriteLine($"{periodName} over {askDays,6:F1} days: {answer.Trendbar.Count,6} bars, hasMore {answer.HasMore}, first {first:yyyy-MM-dd HH:mm}, last {last:yyyy-MM-dd HH:mm} (asked from {from:yyyy-MM-dd HH:mm})");
        }
        catch (CtraderException ex)
        {
            Console.WriteLine($"{periodName} over {askDays,6:F1} days: refused: {ex.Message}");
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

    private static DateTime Open(ProtoOATrendbar bar) => DateTime.UnixEpoch.AddMinutes(bar.UtcTimestampInMinutes);

    private static DateTime Weekday(DateTime near)
    {
        var day = new DateTime(near.Year, near.Month, near.Day, 12, 0, 0, DateTimeKind.Utc);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday or DayOfWeek.Monday or DayOfWeek.Friday)
            day = day.AddDays(-1);

        return day;
    }

    private static string Clock(int seconds) => $"{seconds / 3600:00}:{seconds % 3600 / 60:00}";

    private static IEnumerable<DateTime> Steps(DateTime first, DateTime last, TimeSpan step)
    {
        for (var t = first; t <= last; t += step)
            yield return t;
    }

    private static DateTime Floor(DateTime time, TimeSpan interval) => new(time.Ticks / interval.Ticks * interval.Ticks, DateTimeKind.Utc);

    private static long Ms(DateTime time) => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static string Moment(uint secondsFromSunday)
    {
        var t = TimeSpan.FromSeconds(secondsFromSunday);
        return $"{(DayOfWeek)(t.Days % 7)} {t.Hours:00}:{t.Minutes:00}";
    }
}
