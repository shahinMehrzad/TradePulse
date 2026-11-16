using Domain.Enums;
using Domain.Services;
using Domain.ValueObjects;
using Infrastructure.Exchange.Ctrader.Protocol;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Exchange.Ctrader;

/// <summary>What the gateways need to know about one symbol of the broker.</summary>
/// <param name="Id">cTrader's number for it; different brokers number the same symbol differently.</param>
/// <param name="Name">The broker's name, as it spells it.</param>
/// <param name="Enabled">Whether the broker shows the symbol to traders.</param>
internal sealed record CtraderSymbolInfo(long Id, string Name, bool Enabled);

/// <summary>
/// The broker's symbols: which names exist and what number each has (cTrader asks for a symbol by number), the digits its prices are
/// rounded to, and when its market is open. The list is read once and kept for an hour; a name that is not in it is looked for again at most every
/// few minutes, so a typo cannot make a request each time. When the broker's hours are learnt they go to the calendar, so gap repair and the
/// freshness check leave the closed hours alone.
/// </summary>
internal sealed class CtraderSymbolDirectory(ICtraderConnection connection, TimeProvider time, ILogger<CtraderSymbolDirectory> logger, MarketCalendar? calendar = null)
{
    private static readonly TimeSpan ListLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan MissRetry = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, int> _digits = [];
    private Dictionary<string, CtraderSymbolInfo> _byName = [];
    private DateTimeOffset _listedAt = DateTimeOffset.MinValue;

    /// <summary>The symbol the broker lists under <paramref name="name"/> (in any case, <c>EUR/USD</c> and <c>EURUSD</c> alike), or null.</summary>
    public async Task<CtraderSymbolInfo?> FindAsync(ExchangeSymbol name, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            var key = Key(name.Value);

            if (now - _listedAt >= ListLifetime)
                await ReloadAsync(now, cancellationToken);

            if (_byName.TryGetValue(key, out var found))
                return found;

            // Maybe the broker added it since: ask again, but not for every typo.
            if (now - _listedAt >= MissRetry)
            {
                await ReloadAsync(now, cancellationToken);
                return _byName.GetValueOrDefault(key);
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The digits the symbol's prices are rounded to. Reading them also teaches the calendar when the symbol's market is open.</summary>
    public async Task<int> GetDigitsAsync(CtraderSymbolInfo symbol, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_digits.TryGetValue(symbol.Id, out var known))
                return known;

            var answer = await connection.RequestAsync<ProtoOASymbolByIdRes>(ProtoOAPayloadType.ProtoOaSymbolByIdReq,
                new ProtoOASymbolByIdReq { CtidTraderAccountId = connection.AccountId, SymbolId = { symbol.Id } }, historical: false, cancellationToken);

            var full = answer.Symbol.FirstOrDefault(s => s.SymbolId == symbol.Id)
                ?? throw new CtraderException($"cTrader sent no details for the symbol {symbol.Name}.");

            LearnHours(symbol, full);
            _digits[symbol.Id] = full.Digits;
            return full.Digits;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReloadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var answer = await connection.RequestAsync<ProtoOASymbolsListRes>(ProtoOAPayloadType.ProtoOaSymbolsListReq,
            new ProtoOASymbolsListReq { CtidTraderAccountId = connection.AccountId }, historical: false, cancellationToken);

        var byName = new Dictionary<string, CtraderSymbolInfo>(StringComparer.Ordinal);
        foreach (var light in answer.Symbol.Where(s => s.HasSymbolName))
            byName[Key(light.SymbolName)] = new CtraderSymbolInfo(light.SymbolId, light.SymbolName, light.Enabled);

        _byName = byName;
        _listedAt = now;
    }

    private void LearnHours(CtraderSymbolInfo symbol, ProtoOASymbol full)
    {
        if (calendar is null)
            return;

        var exchangeSymbol = new ExchangeSymbol(symbol.Name);
        if (full.Schedule.Count == 0)
        {
            calendar.SetHours(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, exchangeSymbol, null);
            return;
        }

        try
        {
            var zone = string.IsNullOrWhiteSpace(full.ScheduleTimeZone) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(full.ScheduleTimeZone);
            var sessions = full.Schedule.Select(i => (TimeSpan.FromSeconds(i.StartSecond), TimeSpan.FromSeconds(i.EndSecond)));
            var hours = new TradingHours(sessions, zone).WithClosures(Closures(symbol, full, zone));
            calendar.SetHours(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, exchangeSymbol, hours);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            // Without the hours the symbol counts as always open: a weekend is then reported as a hole, but no candle is ever skipped.
            logger.LogWarning(ex, "The trading hours of {Symbol} could not be understood (time zone {Zone}); it is treated as always open", symbol.Name, full.ScheduleTimeZone);
        }
    }

    /// <summary>
    /// The days the broker lists as holidays, as closures. An entry shuts the market from <c>startSecond</c> to <c>endSecond</c> of that day (00:00 to 00:00
    /// is the whole day) in the time zone of the entry, which is not the zone of the weekly hours (Bucharest, Moscow). An early close ends at 23:59, which
    /// means "for the rest of the day": the closure then lasts to the end of the session it ends in. The yearly ones are not understood yet (the broker has
    /// listed none): they are left out, which only costs a gap that is reported.
    /// </summary>
    private IEnumerable<TradingClosure> Closures(CtraderSymbolInfo symbol, ProtoOASymbol full, TimeZoneInfo symbolZone)
    {
        foreach (var holiday in full.Holiday.Where(h => !h.IsRecurring))
        {
            TradingClosure closure;
            try
            {
                var zone = string.IsNullOrWhiteSpace(holiday.ScheduleTimeZone) ? symbolZone : TimeZoneInfo.FindSystemTimeZoneById(holiday.ScheduleTimeZone);
                var day = DateTime.SpecifyKind(DateTime.UnixEpoch.AddDays(holiday.HolidayDate), DateTimeKind.Unspecified);
                int start = holiday.StartSecond, end = holiday.EndSecond;
                if (start == 0 && end == 0)
                    end = 86400;

                var restOfTheDay = end >= 86340;
                if (restOfTheDay)
                    end = 86400;

                closure = new TradingClosure(ToUtc(zone, day.AddSeconds(start)), ToUtc(zone, day.AddSeconds(end)), ThroughSession: restOfTheDay);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentOutOfRangeException)
            {
                logger.LogWarning(ex, "The holiday {Holiday} of {Symbol} could not be understood and is left out", holiday.Name, symbol.Name);
                continue;
            }

            yield return closure;
        }
    }

    private static DateTime ToUtc(TimeZoneInfo zone, DateTime wallClock) =>
        TimeZoneInfo.ConvertTimeToUtc(zone.IsInvalidTime(wallClock) ? wallClock.AddHours(1) : wallClock, zone);

    // EUR/USD, eurusd and EURUSD are one symbol; the broker's own spelling is kept in the info.
    private static string Key(string name) => name.Replace("/", "", StringComparison.Ordinal).ToUpperInvariant();
}
