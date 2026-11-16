using Infrastructure.Exchange.Ctrader;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BackgroundWorker.Health;

/// <summary>
/// How long the cTrader access token still lives (about 30 days). cTrader does not say when it ends, so the day it was made and its life are known only
/// to whoever made it: <c>ctrader-check</c> prints <c>CTRADER_ACCESS_TOKEN_EXPIRES_AT</c> with the token, and that is the setting this reads. A day before
/// the end (<c>AccessTokenWarnBefore</c>) the check turns degraded, which the alert channels announce like any other change, once, and again when
/// a new token has been given and the collector restarted. The connection check turns unhealthy only when the token has already stopped working: this
/// is the warning before that. It never touches the token itself and never prints it.
/// </summary>
public sealed class CtraderTokenHealthCheck(IOptions<CtraderExchangeConfigurations> options, TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (settings.AccessTokenExpiresAt is not { } expiresAt)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "The end of the access token is not set, so nobody is warned before it comes (CTRADER_ACCESS_TOKEN_EXPIRES_AT, printed by ctrader-check)."));
        }

        var left = expiresAt - timeProvider.GetUtcNow();
        var data = new Dictionary<string, object> { ["expiresAt"] = expiresAt.UtcDateTime.ToString("O") };

        if (left <= TimeSpan.Zero)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"The access token ran out at {expiresAt:yyyy-MM-dd HH:mm} UTC. Make a new one (ctrader-check refresh), put it in deploy/.env and restart collector and api.", data: data));
        }

        if (left <= settings.AccessTokenWarnBefore)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"The access token runs out in {Describe(left)} (at {expiresAt:yyyy-MM-dd HH:mm} UTC). Make a new one (ctrader-check refresh), put it in deploy/.env and restart collector and api.", data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy($"The access token is good for {Describe(left)} more (until {expiresAt:yyyy-MM-dd HH:mm} UTC).", data));
    }

    private static string Describe(TimeSpan left) => left.TotalHours < 48 ? $"{Math.Max(1, (int)Math.Round(left.TotalHours))} hours" : $"{(int)left.TotalDays} days";
}
