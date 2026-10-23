using Infrastructure.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Infrastructure.UnitTests;

public class DatabaseHealthCheckTests
{
    [Fact]
    public async Task UnreachableHostIsUnhealthyAndDoesNotLeakTheConnectionString()
    {
        const string secret = "s3cret-password";
        var check = new DatabaseHealthCheck($"Host=127.0.0.1;Port=1;Database=x;Username=u;Password={secret};Timeout=2");

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.DoesNotContain(secret, result.Description);
        Assert.DoesNotContain("127.0.0.1", result.Description);
    }
}
