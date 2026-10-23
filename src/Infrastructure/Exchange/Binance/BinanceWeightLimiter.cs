namespace Infrastructure.Exchange.Binance;

public sealed class BinanceWeightLimiter(int maxWeightPerMinute, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _used;
    private DateTime _windowStart = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    public async Task AcquireAsync(int weight, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            while (true)
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                if (now - _windowStart >= TimeSpan.FromMinutes(1))
                {
                    _windowStart = now;
                    _used = 0;
                }

                if (_used + weight <= maxWeightPerMinute)
                {
                    _used += weight;
                    return;
                }

                var wait = _windowStart.AddMinutes(1) - now;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, _timeProvider, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
