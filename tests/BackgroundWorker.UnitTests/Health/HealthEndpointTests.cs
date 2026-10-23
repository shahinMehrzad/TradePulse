using System.Net;
using System.Text.Json;
using BackgroundWorker.CandleSeries;
using BackgroundWorker.Health;
using BackgroundWorker.UnitTests.CandleSeries;
using Domain.Enums;
using Infrastructure.Configurations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BackgroundWorker.UnitTests.Health;

/// <summary>Starts the real Kestrel endpoint on a free local port, the same way Program.cs does.</summary>
public class HealthEndpointTests
{
    private static async Task<(IHost Host, HttpClient Client, WorkerHealthState State)> StartAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=1",
            })
            .Build();

        // The checks that depend on the chosen series need a provider; one that already knows a futures series stands for the database.
        var series = await CandleSeriesTestSupport.LoadedProviderAsync(
            TimeProvider.System,
            TestPlans.Binance(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H));

        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel()
                .UseUrls("http://127.0.0.1:0")
                .ConfigureServices(services => services.AddRouting())
                .Configure(app => app.UseHealthEndpoints()))
            .ConfigureServices(services =>
            {
                services.AddSingleton<CandleSeriesProvider>(series);
                services.AddWorkerHealth(configuration);
                // The probe worker needs real Binance clients; only the endpoint and the checks are under test here.
                services.Remove(services.Single(s => s.ImplementationType == typeof(BinanceConnectivityWorker)));
            })
            .Build();

        await host.StartAsync(TestContext.Current.CancellationToken);

        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (host, new HttpClient { BaseAddress = new Uri(address) }, host.Services.GetRequiredService<WorkerHealthState>());
    }

    [Fact]
    public async Task Live_AnswersWithoutRunningAnyCheck()
    {
        var (host, client, _) = await StartAsync();
        using var _h = host;
        using var _c = client;

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ReturnsServiceUnavailableWithAJsonReportWhenSomethingIsUnhealthy()
    {
        var (host, client, state) = await StartAsync();
        using var _h = host;
        using var _c = client;
        state.SetConnected(HealthComponents.FuturesWebSocket, true);
        state.RecordMessage(HealthComponents.FuturesWebSocket);

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        var checks = json.RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("status").GetString());

        // The database is unreachable on purpose, so the overall status must be unhealthy.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("Unhealthy", checks["database"]);
        Assert.Equal("Healthy", checks["binance-futures-websocket"]);
        Assert.DoesNotContain("Password", body);
    }
}
