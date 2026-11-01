using Domain.Entities;
using Domain.Enums;

namespace Application.Research;

/// <summary>
/// Finds the minutes in which the price moved far more than it usually does. Pure: candles in, events out, so the same rule runs over
/// a year of history or over the last day.
/// <para>
/// A minute is flagged when its 5 minute logarithmic return is at least <see cref="MarketEventDetectorOptions.MinFiveMinuteMove"/> and at
/// least <see cref="MarketEventDetectorOptions.MinZScore"/> standard deviations of the 1 minute returns of the window before it (the
/// minute itself is not part of its own baseline). Flagged minutes close together are one event, described by its most extreme minute.
/// </para>
/// </summary>
public sealed class MarketEventDetector(MarketEventDetectorOptions options)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <param name="bars">1 minute candles, oldest first. The first <see cref="MarketEventDetectorOptions.Window"/> only warm up the baseline.</param>
    /// <param name="symbolListingId">The listing the candles belong to.</param>
    /// <param name="from">Events that start before this are left out (they belong to the warm-up). Null keeps everything.</param>
    public IReadOnlyList<MarketEvent> Detect(IReadOnlyList<MinuteBar> bars, int symbolListingId, DateTime? from = null)
    {
        var window = options.Window;
        var n = bars.Count;
        var events = new List<MarketEvent>();
        if (n <= window + 1)
            return events;

        // Running sums, so the baseline of every minute costs the same however long the window is.
        var sum = new double[n];
        var sumSquares = new double[n];
        var volume = new double[n];
        for (var i = 0; i < n; i++)
        {
            var r = i == 0 ? 0 : Math.Log((double)bars[i].Close / (double)bars[i - 1].Close);
            sum[i] = (i == 0 ? 0 : sum[i - 1]) + r;
            sumSquares[i] = (i == 0 ? 0 : sumSquares[i - 1]) + r * r;
            volume[i] = (i == 0 ? 0 : volume[i - 1]) + (double)bars[i].Volume;
        }

        Cluster? cluster = null;

        for (var i = window + 1; i < n; i++)
        {
            // The five minutes must really be five minutes; a hole in the data is not a move.
            if (bars[i].OpenTime - bars[i - 5].OpenTime != 5 * Minute)
                continue;

            // The baseline is the window of returns that ends at the minute before this one.
            var windowSum = sum[i - 1] - sum[i - 1 - window];
            var windowSquares = sumSquares[i - 1] - sumSquares[i - 1 - window];
            var variance = (windowSquares - windowSum * windowSum / window) / (window - 1);
            if (variance <= 0)
                continue;

            var fiveMinute = Math.Log((double)bars[i].Close / (double)bars[i - 5].Close);
            var z = fiveMinute / (Math.Sqrt(variance) * Math.Sqrt(5));
            if (Math.Abs(fiveMinute) < options.MinFiveMinuteMove || Math.Abs(z) < options.MinZScore)
                continue;

            var averageVolume = (volume[i - 1] - volume[i - 1 - window]) / window;
            var volumeRatio = averageVolume > 0 ? (double)bars[i].Volume / averageVolume : 0;
            var time = bars[i].OpenTime;

            if (cluster is not null && time - cluster.LastTime <= options.MergeGap)
                cluster.Add(time, fiveMinute, z, volumeRatio);
            else
            {
                if (cluster is not null)
                    Emit(cluster);
                cluster = new Cluster(time, fiveMinute, z, volumeRatio);
            }
        }

        if (cluster is not null)
            Emit(cluster);

        return events;

        void Emit(Cluster found)
        {
            if (from is { } start && found.StartedAt < start)
                return;

            events.Add(new MarketEvent
            {
                SymbolListingId = symbolListingId,
                StartedAt = found.StartedAt,
                Direction = found.FiveMinute > 0 ? EventDirectionEnum.Up : EventDirectionEnum.Down,
                FiveMinuteReturn = Math.Round((decimal)(Math.Exp(found.FiveMinute) - 1), 8),
                ZScore = Math.Round((decimal)found.Z, 4),
                VolumeRatio = Math.Round((decimal)found.VolumeRatio, 4),
                FlaggedMinutes = found.Minutes,
                DetectorVersion = options.Version,
            });
        }
    }

    /// <summary>The flagged minutes of one event so far.</summary>
    private sealed class Cluster(DateTime startedAt, double fiveMinute, double z, double volumeRatio)
    {
        public DateTime StartedAt { get; } = startedAt;
        public DateTime LastTime { get; private set; } = startedAt;

        /// <summary>The 5 minute return and z-score of the most extreme minute (by z-score), kept together so they always agree on the direction.</summary>
        public double FiveMinute { get; private set; } = fiveMinute;
        public double Z { get; private set; } = z;

        public double VolumeRatio { get; private set; } = volumeRatio;
        public int Minutes { get; private set; } = 1;

        public void Add(DateTime time, double fiveMinute, double z, double volumeRatio)
        {
            LastTime = time;
            Minutes++;
            VolumeRatio = Math.Max(VolumeRatio, volumeRatio);

            if (Math.Abs(z) > Math.Abs(Z))
            {
                FiveMinute = fiveMinute;
                Z = z;
            }
        }
    }
}
