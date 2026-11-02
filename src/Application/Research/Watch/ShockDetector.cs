using Application.Exchange;
using Domain.Entities;
using Domain.Enums;

namespace Application.Research.Watch;

public enum ShockSignalKind
{
    /// <summary>The first flagged second of a new alert.</summary>
    Started,

    /// <summary>A running alert went from attention to alarm.</summary>
    Escalated,

    /// <summary>The alert is over (nothing flagged for the merge gap); it carries its largest move.</summary>
    Closed,
}

/// <summary>What the detector says: the kind, and the alert as it stands at that moment (a new object each time).</summary>
public sealed record ShockSignal(ShockSignalKind Kind, ShockAlert Alert);

/// <summary>
/// Watches the price of one symbol second by second. Trades go in; when a second is complete, the price move over the last
/// <see cref="ShockWatchOptions.WindowSeconds"/> is judged against what the previous <see cref="ShockWatchOptions.BaselineSeconds"/> seconds
/// usually did (the second itself is not part of its own baseline). Pure and single-threaded: the owner calls it from one place only.
/// <para>
/// A second without trades keeps the last price and has no volume, so calm stretches count as calm. A gap longer than the memory
/// of the detector (an hour) starts it over, including the warm-up.
/// </para>
/// </summary>
public sealed class ShockDetector(int symbolListingId, ShockWatchOptions options)
{
    private readonly int _baseline = options.BaselineSeconds;
    private readonly int _window = options.WindowSeconds;
    private readonly long _mergeSeconds = (long)options.MergeGap.TotalSeconds;
    private readonly int _capacity = CapacityOf(options);

    // One slot per second, addressed by second modulo capacity. The prices and volumes of completed seconds, and the 1 second returns.
    private readonly decimal[] _prices = new decimal[CapacityOf(options)];
    private readonly decimal[] _volumes = new decimal[CapacityOf(options)];
    private readonly double[] _returns = new double[CapacityOf(options)];

    private long _second = long.MinValue;  // the second being built
    private decimal _close;
    private decimal _secondVolume;
    private long _completed;               // seconds completed since the start or the last restart
    private double _sum;                   // of the 1 second returns of the last BaselineSeconds seconds
    private double _sumSquares;
    private decimal _windowVolume;         // traded in the last WindowSeconds seconds
    private Cluster? _cluster;
    private double _seedVariance;          // variance of a 1 second return estimated from older, coarser candles; 0 = none

    /// <summary>
    /// True once the detector can judge: the baseline is full, or (with a <see cref="Seed"/>) the window is. Before that it is silent.
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

    private const int GraceSeconds = 2;

    /// <summary>Ends the alert that is running, if any (the symbol is no longer followed), so it is stored with its final figures.</summary>
    public void Close(ICollection<ShockSignal> signals) => CloseCluster(signals);

    private static long SecondOf(DateTime time) => (long)(time - DateTime.UnixEpoch).TotalSeconds;

    // Room for the baseline, the window and the second being built.
    private static int CapacityOf(ShockWatchOptions options) => options.BaselineSeconds + options.WindowSeconds + 2;

    private int Slot(long second) => (int)(second % _capacity);

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
        CloseCluster(signals);
        _second = target;
        _secondVolume = 0;
        _completed = 0;
        _sum = 0;
        _sumSquares = 0;
        _windowVolume = 0;
        Array.Clear(_returns);
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

    private void Judge(long t, decimal windowVolume, ICollection<ShockSignal> signals)
    {
        var startPrice = _prices[Slot(t - _window)];
        var endPrice = _close;
        var move = Math.Log((double)(endPrice / startPrice));

        // A full baseline of real seconds, or: the real seconds so far with the estimate for the rest of the baseline.
        var variance = _completed > _baseline
            ? (_sumSquares - _sum * _sum / _baseline) / (_baseline - 1)
            : (_sumSquares + (_baseline - Math.Max(0, _completed - 1)) * _seedVariance) / _baseline;
        var flagged = false;
        double z = 0;

        if (variance > 1e-18)
        {
            z = move / (Math.Sqrt(variance) * Math.Sqrt(_window));
            flagged = Math.Abs(move) >= options.AttentionMove && Math.Abs(z) >= options.MinZScore;
        }

        if (flagged)
        {
            var level = Math.Abs(move) >= options.AlarmMove ? AlertLevelEnum.Alarm : AlertLevelEnum.Attention;

            if (_cluster is null)
            {
                _cluster = new Cluster(t, level) { LastFlag = t, FlaggedSeconds = 1 };
                _cluster.Take(move, z, startPrice, endPrice, windowVolume);
                signals.Add(new ShockSignal(ShockSignalKind.Started, ToAlert(_cluster)));
            }
            else
            {
                _cluster.LastFlag = t;
                _cluster.FlaggedSeconds++;
                if (Math.Abs(move) > Math.Abs(_cluster.Move))
                    _cluster.Take(move, z, startPrice, endPrice, windowVolume);

                if (level > _cluster.Level)
                {
                    _cluster.Level = level;
                    signals.Add(new ShockSignal(ShockSignalKind.Escalated, ToAlert(_cluster)));
                }
            }
        }
        else if (_cluster is not null && t - _cluster.LastFlag > _mergeSeconds)
        {
            CloseCluster(signals);
        }
    }

    private void CloseCluster(ICollection<ShockSignal> signals)
    {
        if (_cluster is null)
            return;

        signals.Add(new ShockSignal(ShockSignalKind.Closed, ToAlert(_cluster)));
        _cluster = null;
    }

    private ShockAlert ToAlert(Cluster cluster) => new()
    {
        SymbolListingId = symbolListingId,
        TriggeredAt = DateTime.UnixEpoch.AddSeconds(cluster.StartSecond),
        Direction = cluster.Move > 0 ? EventDirectionEnum.Up : EventDirectionEnum.Down,
        Level = cluster.Level,
        WindowSeconds = _window,
        Move = (decimal)(Math.Exp(cluster.Move) - 1),
        ZScore = (decimal)cluster.ZScore,
        StartPrice = cluster.StartPrice,
        EndPrice = cluster.EndPrice,
        Volume = cluster.Volume,
        FlaggedSeconds = cluster.FlaggedSeconds,
        RuleVersion = options.Version,
    };

    /// <summary>The alert while it runs: its first second, its highest level, and the figures of its largest move.</summary>
    private sealed class Cluster(long startSecond, AlertLevelEnum level)
    {
        public long StartSecond { get; } = startSecond;
        public AlertLevelEnum Level { get; set; } = level;
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
