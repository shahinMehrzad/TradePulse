using Application.Exchange;
using Domain.Entities;
using Domain.Enums;

namespace Application.Research.Watch;

public enum ShockSignalKind
{
    /// <summary>The first flagged second (or minute) of a new alert.</summary>
    Started,

    /// <summary>The alert is over (nothing flagged for the merge gap); it carries its largest move.</summary>
    Closed,
}

/// <summary>What the detector says: the kind, and the alert as it stands at that moment (a new object each time).</summary>
public sealed record ShockSignal(ShockSignalKind Kind, ShockAlert Alert);

/// <summary>
/// Watches the price of one symbol and runs two checks on it (see <see cref="ShockWatchOptions"/>). Trades go in; when a second is complete the
/// move over the last <see cref="ShockWatchOptions.WindowSeconds"/> is judged (the sudden check), and when a minute is complete the move over the
/// last <see cref="ShockWatchOptions.GradualWindowMinutes"/> is judged (the gradual check). Each is measured against what the symbol usually does:
/// the larger of the last hour and a share of the last 24 hours. The second or minute being judged is not part of its own baseline.
/// Pure and single-threaded: the owner calls it from one place only.
/// <para>
/// A second without trades keeps the last price and has no volume, so calm stretches count as calm. A gap longer than the memory of the
/// detector (an hour) starts it over, including the warm-up and the minutes it remembered.
/// </para>
/// </summary>
public sealed class ShockDetector(int symbolListingId, ShockWatchOptions options)
{
    private const int HourMinutes = ShockWatchOptions.HourMinutes;
    private const int GraceSeconds = 2;

    private readonly int _baseline = options.BaselineSeconds;
    private readonly int _window = options.WindowSeconds;
    private readonly int _gradualWindow = options.GradualWindowMinutes;
    private readonly long _mergeSeconds = (long)options.MergeGap.TotalSeconds;
    private readonly long _gradualMergeSeconds = (long)options.GradualMergeGap.TotalSeconds;
    private readonly int _capacity = CapacityOf(options);
    private readonly int _dayMinutes = options.DayBaselineMinutes;
    private readonly int _minuteCapacity = MinuteCapacityOf(options);

    // One slot per second, addressed by second modulo capacity. The prices and volumes of completed seconds, and the 1 second returns.
    private readonly decimal[] _prices = new decimal[CapacityOf(options)];
    private readonly decimal[] _volumes = new decimal[CapacityOf(options)];
    private readonly double[] _returns = new double[CapacityOf(options)];

    // One slot per minute, addressed by minute (since the epoch) modulo capacity: the close, the volume and the return of completed minutes.
    private readonly decimal[] _minuteCloses = new decimal[MinuteCapacityOf(options)];
    private readonly decimal[] _minuteVolumes = new decimal[MinuteCapacityOf(options)];
    private readonly double[] _minuteReturns = new double[MinuteCapacityOf(options)];

    private long _second = long.MinValue;  // the second being built
    private decimal _close;
    private decimal _secondVolume;
    private long _completed;               // seconds completed since the start or the last restart
    private double _sum;                   // of the 1 second returns of the last BaselineSeconds seconds
    private double _sumSquares;
    private decimal _windowVolume;         // traded in the last WindowSeconds seconds
    private Cluster? _cluster;             // the running sudden alert
    private double _seedVariance;          // variance of a 1 second return estimated from older, coarser candles; 0 = none

    private long _minute = long.MinValue;  // the last completed minute
    private long _minutesKnown;            // completed minutes remembered, the last of them being _minute
    private decimal _minuteVolumeSoFar;    // traded in the minute being built
    private double _hourSum;               // of the 1 minute returns of the last HourMinutes minutes
    private double _hourSumSquares;
    private double _daySum;                // of the 1 minute returns of the last DayBaselineMinutes minutes
    private double _daySumSquares;
    private Cluster? _gradualCluster;      // the running gradual alert

    /// <summary>
    /// True once the detector can judge the sudden check: the baseline is full, or (with a <see cref="Seed"/>) the window is. Before that it is silent.
    /// </summary>
    public bool IsWarm => _completed > _baseline || (_seedVariance > 0 && _completed >= _window);

    /// <summary>True once the baseline is made of real seconds only and the seed no longer counts.</summary>
    public bool IsSeedReplaced => _completed > _baseline;

    /// <summary>
    /// Lets the detector judge right away instead of after a full baseline: <paramref name="secondVolatility"/> is the usual standard
    /// deviation of a 1 second logarithmic return, estimated from the 1 minute candles that were already stored. Every real second then
    /// takes the place of one second of the estimate, until after BaselineSeconds the estimate is gone. It is an estimate, so it is only
    /// used while there is nothing better; it also stays after a long gap, when the real seconds are gone.
    /// </summary>
    public void Seed(double secondVolatility)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(secondVolatility, 0);
        _seedVariance = secondVolatility * secondVolatility;
    }

    /// <summary>
    /// Hands the detector the closes of the 1 minute candles already stored (oldest first), so the gradual check has its hour and its 24 hours
    /// from the start. Only before the first trade: later ones are left out, and so is any candle that is not newer than the last one given.
    /// A hole between candles is filled with the last close (no move).
    /// </summary>
    public void SeedMinutes(IEnumerable<MinuteClose> closes)
    {
        if (_second != long.MinValue)
            return;

        foreach (var candle in closes)
        {
            if (candle.Close <= 0 || (_minute != long.MinValue && candle.Minute <= _minute))
                continue;

            AddMinute(candle.Minute, candle.Close, 0, judge: false, signals: null);
        }
    }

    /// <summary>Trades older than the second being built, which cannot be placed any more and are left out.</summary>
    public long LateTrades { get; private set; }

    public void Add(LiveTrade trade, ICollection<ShockSignal> signals)
    {
        var second = SecondOf(trade.Time);

        if (_second == long.MinValue)
        {
            _second = second;
            _close = trade.Price;
        }
        else if (second < _second)
        {
            LateTrades++;
            return;
        }
        else
        {
            RollTo(second, signals);
        }

        _close = trade.Price;
        _secondVolume += trade.Quantity;
    }

    /// <summary>
    /// Lets time pass without a trade, so a quiet second is judged (and a finished alert closed) on time. The last two seconds are left
    /// open: the clock here and the exchange's differ a little, and a trade is on its way for a moment after it happened.
    /// </summary>
    public void Advance(DateTime now, ICollection<ShockSignal> signals)
    {
        if (_second != long.MinValue)
            RollTo(SecondOf(now) - GraceSeconds, signals);
    }

    /// <summary>Ends the alerts that are running, if any (the symbol is no longer followed), so they are stored with their final figures.</summary>
    public void Close(ICollection<ShockSignal> signals)
    {
        CloseCluster(ref _cluster, signals);
        CloseCluster(ref _gradualCluster, signals);
    }

    private static long SecondOf(DateTime time) => (long)(time - DateTime.UnixEpoch).TotalSeconds;

    // Room for the baseline, the window and the second being built.
    private static int CapacityOf(ShockWatchOptions options) => options.BaselineSeconds + options.WindowSeconds + 2;

    // Room for the day, the gradual window and the minute being built.
    private static int MinuteCapacityOf(ShockWatchOptions options) => options.DayBaselineMinutes + options.GradualWindowMinutes + 2;

    private int Slot(long second) => (int)(second % _capacity);

    private int MinuteSlot(long minute) => (int)(minute % _minuteCapacity);

    private void RollTo(long target, ICollection<ShockSignal> signals)
    {
        if (target - _second > _capacity)
        {
            Restart(target, signals);
            return;
        }

        while (_second < target)
        {
            Complete(signals);
            _second++;
            _secondVolume = 0;
        }
    }

    private void Restart(long target, ICollection<ShockSignal> signals)
    {
        Close(signals);
        _second = target;
        _secondVolume = 0;
        _completed = 0;
        _sum = 0;
        _sumSquares = 0;
        _windowVolume = 0;
        Array.Clear(_returns);

        // The minutes it remembered are no longer next to the ones to come.
        _minute = long.MinValue;
        _minutesKnown = 0;
        _minuteVolumeSoFar = 0;
        _hourSum = _hourSumSquares = _daySum = _daySumSquares = 0;
        Array.Clear(_minuteReturns);
    }

    private void Complete(ICollection<ShockSignal> signals)
    {
        var t = _second;
        var slot = Slot(t);
        var ret = _completed == 0 ? 0 : Math.Log((double)(_close / _prices[Slot(t - 1)]));

        _prices[slot] = _close;
        _volumes[slot] = _secondVolume;

        var windowVolume = _windowVolume + _secondVolume - (_completed >= _window ? _volumes[Slot(t - _window)] : 0);
        _windowVolume = windowVolume;

        // Judged before this second's return joins the baseline. The baseline then holds the returns of the seconds t-B .. t-1, which
        // all exist once B+1 seconds are complete (the first one has no return).
        if (IsWarm)
            Judge(t, windowVolume, signals);

        _returns[slot] = ret;
        _sum += ret;
        _sumSquares += ret * ret;
        if (_completed >= _baseline)
        {
            var leaving = _returns[Slot(t - _baseline)];
            _sum -= leaving;
            _sumSquares -= leaving * leaving;
        }

        _completed++;

        // Running sums drift a little over days; adding and removing the same numbers is exact in theory, not in floating point.
        if (_completed % _baseline == 0)
            Recompute(t);

        // A minute is complete with its last second.
        _minuteVolumeSoFar += _secondVolume;
        if ((t + 1) % 60 == 0)
        {
            AddMinute(t / 60, _close, _minuteVolumeSoFar, judge: true, signals);
            _minuteVolumeSoFar = 0;
        }
    }

    private void Recompute(long t)
    {
        _sum = 0;
        _sumSquares = 0;
        for (var s = t - _baseline + 1; s <= t; s++)
        {
            var r = _returns[Slot(s)];
            _sum += r;
            _sumSquares += r * r;
        }
    }

    /// <summary>The variance of a 1 minute return over the last 24 hours, or 0 while less than a quarter of that has been seen.</summary>
    private double DayMinuteVariance()
    {
        var n = Math.Min(_minutesKnown, _dayMinutes);
        if (n < Math.Max(HourMinutes, _dayMinutes / 4))
            return 0;

        return Math.Max(0, _daySumSquares / n - Math.Pow(_daySum / n, 2));
    }

    private void Judge(long t, decimal windowVolume, ICollection<ShockSignal> signals)
    {
        var startPrice = _prices[Slot(t - _window)];
        var endPrice = _close;
        var move = Math.Log((double)(endPrice / startPrice));

        // A full baseline of real seconds, or: the real seconds so far with the estimate for the rest of the baseline.
        var hourVariance = _completed > _baseline
            ? (_sumSquares - _sum * _sum / _baseline) / (_baseline - 1)
            : (_sumSquares + (_baseline - Math.Max(0, _completed - 1)) * _seedVariance) / _baseline;

        // The last 24 hours, known in minutes: a second is a 60th of a minute's variance, as in ShockSeed.
        var dayVariance = DayMinuteVariance() / 60;
        var variance = Math.Max(hourVariance, options.DayBaselineShare * options.DayBaselineShare * dayVariance);

        var flagged = false;
        double z = 0;

        if (variance > 1e-18)
        {
            z = move / (Math.Sqrt(variance) * Math.Sqrt(_window));
            flagged = Math.Abs(move) >= options.MinMove && Math.Abs(z) >= options.MinZScore;
        }

        Track(ref _cluster, flagged, t, _mergeSeconds, _window, options.SuddenVersion, move, z, startPrice, endPrice, windowVolume, signals);
    }

    /// <summary>
    /// A minute is complete: it joins the memory, and (unless it is only being remembered) the move over the gradual window is judged. The
    /// minute is judged before its own return joins the baseline.
    /// </summary>
    private void AddMinute(long minute, decimal close, decimal volume, bool judge, ICollection<ShockSignal>? signals)
    {
        // A hole (no trade for minutes, or candles that are missing) is filled with the last close, so the memory stays one slot per minute.
        if (_minute != long.MinValue)
        {
            var missing = Math.Min(minute - _minute - 1, _minuteCapacity);
            for (var i = 0; i < missing; i++)
                Remember(_minute + 1, _minuteCloses[MinuteSlot(_minute)], 0);
        }

        if (judge && signals is not null)
            JudgeGradual(minute, close, volume, signals);

        Remember(minute, close, volume);
    }

    private void Remember(long minute, decimal close, decimal volume)
    {
        var slot = MinuteSlot(minute);
        var ret = _minute == long.MinValue || _minuteCloses[MinuteSlot(_minute)] <= 0
            ? 0
            : Math.Log((double)(close / _minuteCloses[MinuteSlot(_minute)]));

        _minuteCloses[slot] = close;
        _minuteVolumes[slot] = volume;
        _minuteReturns[slot] = ret;
        _hourSum += ret;
        _hourSumSquares += ret * ret;
        _daySum += ret;
        _daySumSquares += ret * ret;

        // The returns that are now older than the hour and than the day leave their sums.
        if (_minutesKnown >= HourMinutes)
        {
            var leaving = _minuteReturns[MinuteSlot(minute - HourMinutes)];
            _hourSum -= leaving;
            _hourSumSquares -= leaving * leaving;
        }

        if (_minutesKnown >= _dayMinutes)
        {
            var leaving = _minuteReturns[MinuteSlot(minute - _dayMinutes)];
            _daySum -= leaving;
            _daySumSquares -= leaving * leaving;
        }

        _minute = minute;
        _minutesKnown++;
    }

    private void JudgeGradual(long minute, decimal close, decimal volume, ICollection<ShockSignal> signals)
    {
        // Needs an hour to compare with and the minute the window starts from.
        if (_minutesKnown < Math.Max(HourMinutes, _gradualWindow))
            return;

        var startPrice = _minuteCloses[MinuteSlot(minute - _gradualWindow)];
        if (startPrice <= 0)
            return;

        var move = Math.Log((double)(close / startPrice));
        var n = Math.Min(_minutesKnown, HourMinutes);
        var hourVariance = Math.Max(0, _hourSumSquares / n - Math.Pow(_hourSum / n, 2));
        var variance = Math.Max(hourVariance, options.DayBaselineShare * options.DayBaselineShare * DayMinuteVariance());

        var flagged = false;
        double z = 0;
        if (variance > 1e-18)
        {
            z = move / (Math.Sqrt(variance) * Math.Sqrt(_gradualWindow));
            flagged = Math.Abs(move) >= options.MinMove && Math.Abs(z) >= options.GradualMinZScore;
        }

        // Traded in the window: this minute and the ones before it.
        var windowVolume = volume;
        for (var m = minute - _gradualWindow + 1; m < minute; m++)
            windowVolume += _minuteVolumes[MinuteSlot(m)];

        // The minute ends with its last second, which is what the alert is dated by.
        var endSecond = minute * 60 + 59;
        Track(ref _gradualCluster, flagged, endSecond, _gradualMergeSeconds, _gradualWindow * 60, options.GradualVersion, move, z, startPrice, close, windowVolume, signals);
    }

    /// <summary>Starts, extends or closes the alert of one check, according to whether this second (or minute) was flagged.</summary>
    private void Track(
        ref Cluster? cluster, bool flagged, long t, long mergeSeconds, int windowSeconds, string version,
        double move, double z, decimal startPrice, decimal endPrice, decimal windowVolume, ICollection<ShockSignal> signals)
    {
        if (flagged)
        {
            if (cluster is null)
            {
                cluster = new Cluster(t, windowSeconds, version) { LastFlag = t, FlaggedSeconds = 1 };
                cluster.Take(move, z, startPrice, endPrice, windowVolume);
                signals.Add(new ShockSignal(ShockSignalKind.Started, ToAlert(cluster)));
            }
            else
            {
                cluster.LastFlag = t;
                cluster.FlaggedSeconds++;
                if (Math.Abs(move) > Math.Abs(cluster.Move))
                    cluster.Take(move, z, startPrice, endPrice, windowVolume);
            }
        }
        else if (cluster is not null && t - cluster.LastFlag > mergeSeconds)
        {
            CloseCluster(ref cluster, signals);
        }
    }

    private void CloseCluster(ref Cluster? cluster, ICollection<ShockSignal> signals)
    {
        if (cluster is null)
            return;

        signals.Add(new ShockSignal(ShockSignalKind.Closed, ToAlert(cluster)));
        cluster = null;
    }

    private ShockAlert ToAlert(Cluster cluster) => new()
    {
        SymbolListingId = symbolListingId,
        TriggeredAt = DateTime.UnixEpoch.AddSeconds(cluster.StartSecond),
        Direction = cluster.Move > 0 ? EventDirectionEnum.Up : EventDirectionEnum.Down,

        // One level: how big the move is shows in its z. (The numbers of the old levels stay in old rows.)
        Level = AlertLevelEnum.Attention,
        WindowSeconds = cluster.WindowSeconds,
        Move = (decimal)(Math.Exp(cluster.Move) - 1),
        ZScore = (decimal)cluster.ZScore,
        StartPrice = cluster.StartPrice,
        EndPrice = cluster.EndPrice,
        Volume = cluster.Volume,
        FlaggedSeconds = cluster.FlaggedSeconds,
        RuleVersion = cluster.Version,
    };

    /// <summary>The alert while it runs: its first second, and the figures of its largest move.</summary>
    private sealed class Cluster(long startSecond, int windowSeconds, string version)
    {
        public long StartSecond { get; } = startSecond;
        public int WindowSeconds { get; } = windowSeconds;
        public string Version { get; } = version;
        public long LastFlag { get; set; }
        public int FlaggedSeconds { get; set; }
        public double Move { get; private set; }
        public double ZScore { get; private set; }
        public decimal StartPrice { get; private set; }
        public decimal EndPrice { get; private set; }
        public decimal Volume { get; private set; }

        public void Take(double move, double z, decimal startPrice, decimal endPrice, decimal volume)
        {
            Move = move;
            ZScore = z;
            StartPrice = startPrice;
            EndPrice = endPrice;
            Volume = volume;
        }
    }
}

/// <summary>The close of a 1 minute candle that is already stored: the minute (whole minutes since the epoch, UTC) and the price.</summary>
public readonly record struct MinuteClose(long Minute, decimal Close);
